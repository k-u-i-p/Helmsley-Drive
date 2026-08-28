using System.ComponentModel;
using HelmsleyDrive.CloudFilter;

namespace HelmsleyDrive.App;

/// <summary>
/// What the window shows and what the buttons do — the port of Mac/AppShared/AppModel.swift, with
/// one structural difference it cannot paper over: there is no extension process. On the Mac the
/// app registers a domain and Finder is serviced elsewhere; here the engine lives in this process,
/// so "mounted" means the sync root is registered <em>and this process is connected to it</em>,
/// and closing the window is what unmounting-until-next-launch actually is.
/// </summary>
public sealed class AppModel : INotifyPropertyChanged
{
    readonly string _root;
    readonly string _snapshotPath;

    Mirror? _mirror;
    SyncConnection? _connection;
    CancellationTokenSource? _polling;
    Task? _loop;

    public AppModel(string root, string snapshotPath)
    {
        _root = root;
        _snapshotPath = snapshotPath;
    }

    // The window redraws wholesale from the model, so one "something changed" signal is the whole
    // of the protocol — per-property names would buy nothing.
    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public Admin? Admin { get; private set; }
    public bool IsMounted { get; private set; }
    public bool IsWorking { get; private set; }
    public string? ErrorMessage { get; private set; }

    public string Root => _root;

    /// <summary>
    /// The stored credential decides, not a successful whoami: the portal being unreachable is not
    /// a sign-out, so the window goes on saying "signed in" with the identity left unknown.
    /// </summary>
    public bool IsSignedIn => Admin is not null || TokenProvider.Shared.IsSignedIn;

    // MARK: - Lifecycle

    /// <summary>Called once when the window appears: if a credential is stored, mount without asking.</summary>
    public async Task Startup()
    {
        if (!TokenProvider.Shared.IsSignedIn) return;
        await Perform(async () =>
        {
            await Greet();
            await MountAndRun();
        });
    }

    /// <summary>Window closed: stop the engine, leave the registration and the credential alone.</summary>
    public void Shutdown()
    {
        // Off the dispatcher before blocking on it — StopEngine awaits the poll loop, and awaiting
        // anything from a blocked UI thread whose context the await wants back is the deadlock
        // everyone gets to write once.
        Task.Run(StopEngine).Wait(TimeSpan.FromSeconds(15));
    }

    // MARK: - Actions

    /// <summary>The Sign In and Mount button: the browser round trip, then the same mount as startup.</summary>
    public Task Connect() => Perform(async () =>
    {
        await SignIn.Run();
        await Greet();
        await MountAndRun();
    });

    /// <summary>
    /// The Mount button, shown only when the credential is good but the drive is not up — a failed
    /// registration, or an --unregister while signed in. Separate from Connect for the Mac's
    /// reason: undoing it must not cost another password and another text message.
    /// </summary>
    public Task Mount() => Perform(MountAndRun);

    public Task Disconnect() => Perform(async () =>
    {
        // The engine first: a mounted drive whose credential has been cleared could not answer a
        // single request, so nothing of it may survive the sign-out.
        await Task.Run(async () =>
        {
            await StopEngine();
            SyncRoot.Unregister(_root);
        });
        IsMounted = false;
        Changed();

        TokenProvider.Shared.SignOut();
        Program.DeleteSnapshots();
        Admin = null;
    });

    // MARK: - The engine

    /// <summary>
    /// A greeting, not a gate: the portal being briefly unreachable — rate limits included — must
    /// not stop the drive from mounting. Only a grant the server has declared dead stops anything,
    /// by falling through to the sign-in panel.
    /// </summary>
    async Task Greet()
    {
        try { Admin = await HelmsleyApi.Shared.Whoami(); }
        catch (NotAuthenticatedException) { throw; }
        catch (Exception e)
        {
            ErrorMessage = $"The portal is not answering right now ({e.Message}) — mounting anyway.";
        }
    }

    async Task MountAndRun()
    {
        if (IsMounted) return;

        await Task.Run(() =>
        {
            Directory.CreateDirectory(_root);
            SyncRoot.Register(_root);

            var store = new HelmsleyRemoteStore();
            var mirror = new Mirror(store, _root, _snapshotPath);
            try { _connection = SyncRoot.Connect(_root, store, mirror); }
            catch { mirror.Dispose(); throw; }
            mirror.StartWatching();
            _mirror = mirror;
        });

        IsMounted = true;
        Changed();

        // On the pool, not the dispatcher: the loop must be awaitable from Shutdown, which blocks
        // the dispatcher while it waits.
        _polling = new CancellationTokenSource();
        var quit = _polling.Token;
        _loop = Task.Run(() => Poll(quit));
    }

    /// <summary>
    /// No walk of the tree: startup costs one listing (the root, which is no placeholder and so can
    /// never ask for itself), everything below fetches the first time it is looked inside, and only
    /// folders that have been looked inside are re-checked by the poll.
    /// </summary>
    async Task Poll(CancellationToken quit)
    {
        while (!quit.IsCancellationRequested)
        {
            try
            {
                await _mirror!.SyncPass();
                // A pass that succeeded is the portal answering, which retires whatever failure
                // the window was still showing.
                if (ErrorMessage is not null) { ErrorMessage = null; Changed(); }
            }
            catch (Exception e)
            {
                // A failed pass changes nothing on disk; the next one starts from the same snapshot.
                Console.Error.WriteLine($"sync pass failed: {e.Message}");
                ErrorMessage = $"The last check with the portal failed ({e.Message}). Trying again in {Configuration.PollInterval.TotalMinutes:0} minutes.";
                Changed();
            }

            try { await Task.Delay(Configuration.PollInterval, quit); }
            catch (OperationCanceledException) { break; }
        }
    }

    async Task StopEngine()
    {
        _polling?.Cancel();
        if (_loop is not null) await _loop; // never faults: Poll catches everything, cancellation included
        _mirror?.Dispose();
        if (_connection is not null) SyncRoot.Disconnect(_connection);
        _polling = null;
        _loop = null;
        _mirror = null;
        _connection = null;
    }

    // MARK: - Plumbing

    async Task Perform(Func<Task> work)
    {
        IsWorking = true;
        ErrorMessage = null;
        Changed();
        try
        {
            await work();
        }
        catch (NotAuthenticatedException)
        {
            // The grant is dead and the token store already cleared; the window falls back to the
            // sign-in panel, which says everything this could.
            Admin = null;
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
        }
        IsWorking = false;
        Changed();
    }
}
