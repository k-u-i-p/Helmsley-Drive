using System.Runtime.InteropServices;
using System.Windows;
using HelmsleyDrive.CloudFilter;

namespace HelmsleyDrive.App;

/// <summary>
/// The entry point sorts out which of the app's four lives this process is leading:
/// <list type="bullet">
/// <item>The OAuth relay — the browser's redirect launches a second instance whose whole job is to
/// hand the callback URL to the instance that is waiting, and exit.</item>
/// <item>The maintenance flags (<c>--unregister</c>, <c>--sign-out</c>), which speak to whatever
/// console launched them and exit.</item>
/// <item><c>--console</c>, the old headless host, kept for driving the mirror over SSH where a
/// window has no desktop to open on.</item>
/// <item>Everything else: the window — the same small status window the Mac app shows, except that
/// here the engine lives inside it, so the window's process is also the drive's.</item>
/// </list>
/// </summary>
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var callback = args.FirstOrDefault(a => a.StartsWith("helmsley-drive:", StringComparison.OrdinalIgnoreCase));
        if (callback is not null)
        {
            SignIn.RelayCallback(callback);
            return;
        }

        var root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Helmsley Drive");

        // Named for the root it describes: two instances on two roots — the real drive and a probe —
        // must not share one memory of "what each folder held", or each mistakes the other's listings
        // for work already done. The same fold names the sync-root registration, which is why it is
        // asked for rather than spelled out twice.
        var rootKey = SyncRoot.KeyFor(root);
        var snapshotPath = Path.Combine(Configuration.DataDirectory, $"snapshot-{rootKey}.json");

        var headless = Given(args, "--console");
        var maintenance = Given(args, "--unregister") || Given(args, "--sign-out");
        if (headless || maintenance) AttachToParentConsole();

        // One process per root is the drive; a second would fight the first for the same sync root,
        // the same snapshot file and the same staging path beside it — and each would read the
        // other's mirror-driven renames as a user's, because the only thing telling the engine's own
        // work from a stranger's is a process id. Keyed on the root rather than the app, so a probe
        // root can still run beside the real one.
        //
        // Taken before every branch that touches the root, the maintenance flags included. It is
        // what stops --sign-out deleting the tree under a running drive, whose engine would see a
        // stranger's process id on each delete and dutifully bin the whole document tree on the
        // portal.
        using var singleton = new Mutex(initiallyOwned: true, $@"Local\HelmsleyDrive.App.{rootKey}", out var isFirst);
        if (!isFirst)
        {
            var busy = $"Helmsley Drive is already running on {root}.";
            if (headless || maintenance) Console.Error.WriteLine(busy + " Close it first.");
            // Not a message box first. Since the tray, a running instance need not be a visible
            // one, and this second launch is almost always somebody clicking the shortcut to get
            // the window back — so ask the instance that holds the root for it. The box is what
            // is left when nobody answers: an instance still starting, or a maintenance flag
            // holding the lock, neither of which has a window to offer.
            else if (!ShowRequest.Send(rootKey))
                MessageBox.Show("Helmsley Drive is already running.", "Helmsley Drive",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Given(args, "--unregister"))
        {
            SyncRoot.Unregister(root);
            Console.WriteLine($"Unregistered sync root at {root}. The folder and its files remain.");
            return;
        }

        if (Given(args, "--sign-out"))
        {
            SignOut(root);
            return;
        }

        if (headless)
        {
            RunConsole(root, snapshotPath).GetAwaiter().GetResult();
            return;
        }

        RedirectConsoleToLog();

        // Visual styles before anything has a window: the tray menu is a WinForms control, and
        // without this it draws in the pre-XP theme in the middle of a modern desktop.
        System.Windows.Forms.Application.EnableVisualStyles();

        // Software rendering, always: WPF's hardware path drew this window as a blank white sheet
        // on VMware's virtual GPU while the visual tree underneath was perfectly sound, and a
        // status window this size has no rendering load worth a driver lottery.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // The Application before the window, not after: the header's mark is loaded through a pack
        // URI, and the pack scheme is only registered once an Application exists — built the other
        // way round, the window's constructor throws on a URI that is perfectly well formed.
        // OnExplicitShutdown, because the window closing is no longer the app ending — it is the
        // window going to the tray, and the default mode would take the process with it.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var model = new AppModel(root, snapshotPath);
        var window = new MainWindow(model);
        app.MainWindow = window;

        using var tray = new TrayIcon(model, window);
        using var showRequests = ShowRequest.Listen(rootKey, tray.Reveal);

        // Mounting hangs off the application rather than off the window being shown: --background
        // is a login start, where the window is never shown at all and a drive that waited for it
        // would be a drive that never mounted.
        app.Startup += async (_, _) => await model.Startup();
        if (!Given(args, "--background")) window.Show();
        app.Run();

        // After Run returns, which is after Quit: the engine outlives every close but that one.
        model.Shutdown();
    }

    /// <summary>
    /// A flag, however it was typed. Ordinal so that no culture's collation has an opinion about
    /// it, and case-insensitive because a flag the user got right but capitalised opening a window
    /// instead of unregistering a sync root is not a defensible answer.
    /// </summary>
    static bool Given(string[] args, string flag) =>
        args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The whole of a sign-out: the tree, the registration, the credential and the snapshots. The
    /// same sequence <see cref="AppModel.Disconnect"/> runs, and in the same order, because the
    /// order is what makes it safe — what only exists locally is identified while the filter can
    /// still be asked, and nothing is deleted until the registration that would route those
    /// deletes back into the engine has gone.
    /// </summary>
    internal static void SignOut(string root)
    {
        var keep = LocalTree.LocalOnly(root);
        try { SyncRoot.Unregister(root); }
        catch (Exception e) { Console.Error.WriteLine($"unregister failed: {e.Message}"); }
        var keptAt = LocalTree.Discard(root, keep);

        TokenProvider.Shared.SignOut();
        DeleteSnapshots();
        SignIn.ForgetBrowserSession();

        Console.WriteLine("Signed out. The sync root is unregistered and its tree removed."
            + (keptAt is null ? "" : $" Files the portal had not taken yet are at {keptAt}."));
    }

    /// <summary>
    /// The snapshots describe the signed-out account's tree — every root's of them; remembering
    /// them into the next account's mirror would have the first pass "deleting" files that were
    /// never theirs.
    /// </summary>
    internal static void DeleteSnapshots()
    {
        if (!Directory.Exists(Configuration.DataDirectory)) return;
        foreach (var snapshot in Directory.EnumerateFiles(Configuration.DataDirectory, "snapshot*.json"))
            File.Delete(snapshot);
    }

    /// <summary>
    /// The old console host, verbatim: sign in if needed, register, connect, then keep the mirror
    /// true — a sync pass at start and on a timer, and the write callbacks in between — until Ctrl+C.
    /// </summary>
    static async Task RunConsole(string root, string snapshotPath)
    {
        if (!TokenProvider.Shared.IsSignedIn)
        {
            try { await SignIn.Run(); }
            catch (Exception e)
            {
                // A refused or abandoned sign-in is an answer, not a crash: the console host has
                // nothing to mount without one, and a stack trace says less than the sentence does.
                Console.Error.WriteLine($"sign-in failed: {e.Message}");
                return;
            }
        }

        // A greeting, not a gate: the portal being briefly unreachable — rate limits included — must
        // not stop the drive from mounting. Everything after this retries on its own schedule.
        try
        {
            var admin = await HelmsleyApi.Shared.Whoami();
            Console.WriteLine($"Signed in to {Configuration.BaseUri.Host} as {admin.Name ?? admin.Email ?? $"admin {admin.Id}"}");
        }
        catch (ApiException e)
        {
            Console.Error.WriteLine($"the portal is not answering right now ({e.Message}); mounting anyway");
        }

        Directory.CreateDirectory(root);
        SyncRoot.Register(root);

        var store = new HelmsleyRemoteStore();
        var mirror = new Mirror(store, root, snapshotPath);
        var key = SyncRoot.Connect(root, store, mirror);
        mirror.StartWatching();
        Console.WriteLine($"Connected: {root}");

        // No walk of the tree at all: the first pass has no materialised folder to keep true, the
        // root's own listing arrives when a foreign process first opens the drive — the filter asks
        // the populator for the root as readily as for any folder — everything below fetches the
        // first time it is looked inside, and only folders that have been looked inside are
        // re-checked by the poll.
        Console.WriteLine($"Ready. The tree fills in as it is browsed; browsed folders are re-checked every {Configuration.PollInterval.TotalMinutes:0} minutes. " +
            "Ctrl+C disconnects (and leaves the root registered — run with --unregister to remove it).");

        var quit = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };

        while (!quit.IsCancellationRequested)
        {
            try { await mirror.SyncPass(); }
            catch (Exception e)
            {
                // A failed pass changes nothing on disk; the next one starts from the same snapshot.
                Console.Error.WriteLine($"sync pass failed: {e.Message}");
            }

            try { await Task.Delay(Configuration.PollInterval, quit.Token); }
            catch (OperationCanceledException) { break; }
        }

        // The local write path runs off the callbacks, so stopping the poll is not stopping the
        // engine: anything still uploading has to land before the sync root goes.
        await mirror.Quiesce(TimeSpan.FromSeconds(10));
        mirror.Dispose();
        SyncRoot.Disconnect(key);
    }

    /// <summary>
    /// The engine narrates through <c>Console.*</c> — every hydration, upload and refusal. In a
    /// windowed process that narration has nowhere to go, so it goes to a file instead: one log
    /// per run, overwritten on the next, alongside the snapshots.
    ///
    /// Which is also the only account there will be of a failure on a thread nobody owns — a
    /// filter callback, a fire-and-forget upload — so the handler that catches those is installed
    /// here, once the log they should land in exists.
    /// </summary>
    static void RedirectConsoleToLog()
    {
        try
        {
            Directory.CreateDirectory(Configuration.DataDirectory);
            var log = TextWriter.Synchronized(new StreamWriter(
                new FileStream(Path.Combine(Configuration.DataDirectory, "app.log"),
                    // Not shared for reading while it is open: it holds every path in the tree and
                    // the signed-in administrator's name, which is the firm's client list.
                    FileMode.Create, FileAccess.Write, FileShare.None))
            { AutoFlush = true });
            Console.SetOut(log);
            Console.SetError(log);
        }
        catch (Exception)
        {
            // No log is worth refusing to run over — a locked profile, a read-only stale log — and
            // the lines fall silently as they would anyway.
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"unhandled: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Console.Error.WriteLine($"unobserved: {e.Exception}");
            e.SetObserved();
        };
    }

    /// <summary>
    /// A GUI-subsystem process gets no console of its own, which is the point — but the maintenance
    /// flags are run from one and owe it their answers. Launched over SSH the standard handles are
    /// pipes already and this changes nothing; launched from a console interactively they are null,
    /// so attach to the parent's and write to it directly.
    /// </summary>
    static void AttachToParentConsole()
    {
        if (!AttachConsole(AttachParentProcess)) return;
        if (GetStdHandle(StdOutputHandle) is 0 or -1)
            Console.SetOut(ConsoleWriter());
        if (GetStdHandle(StdErrorHandle) is 0 or -1)
            Console.SetError(ConsoleWriter());

        static StreamWriter ConsoleWriter() => new(
            new FileStream("CONOUT$", FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        { AutoFlush = true };
    }

    const uint AttachParentProcess = uint.MaxValue;
    const int StdOutputHandle = -11;
    const int StdErrorHandle = -12;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    static extern nint GetStdHandle(int handle);
}
