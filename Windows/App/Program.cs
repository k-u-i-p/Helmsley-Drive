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

        var root = args.FirstOrDefault(a => !a.StartsWith("--"))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Helmsley Drive");

        // Named for the root it describes: two instances on two roots — the real drive and a probe —
        // must not share one memory of "what each folder held", or each mistakes the other's listings
        // for work already done.
        var rootKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.Unicode.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())))[..16];
        var snapshotPath = Path.Combine(Configuration.DataDirectory, $"snapshot-{rootKey}.json");

        if (args.Contains("--unregister"))
        {
            AttachToParentConsole();
            SyncRoot.Unregister(root);
            Console.WriteLine($"Unregistered sync root at {root}. The folder and its files remain.");
            return;
        }

        if (args.Contains("--sign-out"))
        {
            AttachToParentConsole();
            TokenProvider.Shared.SignOut();
            DeleteSnapshots();
            Console.WriteLine("Signed out. The next run will ask again.");
            return;
        }

        if (args.Contains("--console"))
        {
            AttachToParentConsole();
            RunConsole(root, snapshotPath).GetAwaiter().GetResult();
            return;
        }

        // One process is the drive; a second would fight the first for the same sync root. The
        // second launch just says so — Explorer offers no way to front a window it cannot see.
        using var singleton = new Mutex(initiallyOwned: true, @"Local\HelmsleyDrive.App", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Helmsley Drive is already running.", "Helmsley Drive",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RedirectConsoleToLog();

        // Software rendering, always: WPF's hardware path drew this window as a blank white sheet
        // on VMware's virtual GPU while the visual tree underneath was perfectly sound, and a
        // status window this size has no rendering load worth a driver lottery.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        var model = new AppModel(root, snapshotPath);
        var window = new MainWindow(model);
        window.Closed += (_, _) => model.Shutdown();
        new Application().Run(window);
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
            await SignIn.Run();

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

        // No walk of the tree: startup costs one listing (the root, which is no placeholder and so can
        // never ask for itself), everything below fetches the first time it is looked inside, and only
        // folders that have been looked inside are re-checked by the poll.
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

        mirror.Dispose();
        SyncRoot.Disconnect(key);
    }

    /// <summary>
    /// The engine narrates through <c>Console.*</c> — every hydration, upload and refusal. In a
    /// windowed process that narration has nowhere to go, so it goes to a file instead: one log
    /// per run, overwritten on the next, alongside the snapshots.
    /// </summary>
    static void RedirectConsoleToLog()
    {
        try
        {
            Directory.CreateDirectory(Configuration.DataDirectory);
            var log = TextWriter.Synchronized(new StreamWriter(
                new FileStream(Path.Combine(Configuration.DataDirectory, "app.log"),
                    FileMode.Create, FileAccess.Write, FileShare.Read))
            { AutoFlush = true });
            Console.SetOut(log);
            Console.SetError(log);
        }
        catch (IOException)
        {
            // No log is worth refusing to run over — the lines fall silently as they would anyway.
        }
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
    static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    static extern nint GetStdHandle(int handle);
}
