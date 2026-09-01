using System.ComponentModel;
using System.Windows;
using HelmsleyDrive.CloudFilter;

namespace HelmsleyDrive.App;

/// <summary>
/// What the window shows and what the buttons do — the port of Mac/AppShared/AppModel.swift, with
/// one structural difference it cannot paper over: there is no extension process. On the Mac the
/// app registers a domain and Finder is serviced elsewhere; here the engine lives in this process,
/// so "mounted" means the sync root is registered <em>and this process is connected to it</em> —
/// from which it follows that the registration may not outlive either. Quitting unmounts, and so
/// does a session the portal has ended; closing the window to the tray is the one thing that does
/// not, because the engine is still there behind it.
/// </summary>
public sealed class AppModel : INotifyPropertyChanged
{
    readonly string _root;
    readonly string _snapshotPath;

    Mirror? _mirror;
    SyncConnection? _connection;
    CancellationTokenSource? _polling;
    Task? _loop;

    /// <summary>
    /// Whether this run put the registration there, which is what makes taking it away this run's
    /// business. Set before the connection rather than with it, because the two can come apart: a
    /// Register that succeeds and a Connect that does not leaves the drive in Explorer with nothing
    /// behind it, and that is precisely the state a quit has to clear.
    /// </summary>
    bool _registered;

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

    /// <summary>
    /// Quit — and only Quit; the close box has gone to the notification area since the tray
    /// existed. The drive comes down with the process, registration and tree together, because
    /// nothing answers for a sync root once this exits: a registration left standing is a
    /// "Helmsley Drive" in the navigation pane that opens nothing and explains nothing. The
    /// credential stays, so the next launch mounts again without asking.
    /// </summary>
    public void Shutdown()
    {
        // Nothing was registered this run, so there is nothing of ours to take away. A
        // registration and a tree left by an earlier run belong to whoever signed in then, and a
        // launch that got no further than the sign-in panel has no business deleting them.
        if (!_registered) return;

        // Off the dispatcher before blocking on it — Unmount awaits the poll loop, and awaiting
        // anything from a blocked UI thread whose context the await wants back is the deadlock
        // everyone gets to write once.
        //
        // And nothing thrown here may escape: this runs on the way out of Application.Run, where
        // an exception is not a failed shutdown but a crash dialog raised at somebody who has
        // already quit — with the console redirected to a file nobody is watching.
        //
        // The wait is long because the sequence is: the quiesce alone is allowed ten seconds and
        // removing the tree comes after it. Past the deadline the process exits with the delete
        // half done, which is the outcome worth waiting a while to avoid.
        try
        {
            if (!Task.Run(() => Unmount()).Wait(TimeSpan.FromSeconds(60)))
                Console.Error.WriteLine("shutdown gave up waiting; the drive may not have come down cleanly");
        }
        catch (Exception e) { Console.Error.WriteLine($"shutdown was not clean: {e.Message}"); }
    }

    // MARK: - Actions

    /// <summary>The Sign In and Mount button: the sign-in sheet, then the same mount as startup.</summary>
    public Task Connect() => Perform(async () =>
    {
        // The window is what the sheet hangs off, and asking the Application for it rather than
        // holding one keeps this model free of the view. Null under --console, which is the case
        // that has no desktop to open a sheet on anyway and takes the browser instead.
        await SignIn.Run(Application.Current?.MainWindow);
        await Greet();
        await MountAndRun();
    });

    /// <summary>
    /// The Mount button, shown only when the credential is good but the drive is not up — a failed
    /// registration, or an --unregister while signed in. Separate from Connect for the Mac's
    /// reason: undoing it must not cost another password and another text message.
    /// </summary>
    public Task Mount() => Perform(MountAndRun);

    /// <summary>
    /// Sign Out and Unmount, which now means all of it: the engine, the registration, the tree, the
    /// credential and the snapshots. Leaving the tree behind was the older behaviour and it left
    /// dehydrated placeholders standing under a registration that had gone — entries the user can
    /// see and cannot open, and a tree the next account's first pass would meet as if it were
    /// theirs.
    /// </summary>
    public Task Disconnect() => Perform(async () =>
    {
        // The engine first: a mounted drive whose credential has been cleared could not answer a
        // single request, so nothing of it may survive the sign-out.
        var keptAt = await Task.Run(() => Unmount());
        Changed();

        TokenProvider.Shared.SignOut();
        Program.DeleteSnapshots();
        SignIn.ForgetBrowserSession();
        Admin = null;
        if (keptAt is not null)
            ErrorMessage = $"Signed out. Files the portal had not taken yet were kept at {keptAt}.";
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
        if (IsMounted || _connection is not null) return;

        await Task.Run(() =>
        {
            Directory.CreateDirectory(_root);
            SyncRoot.Register(_root);
            _registered = true;

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
            catch (NotAuthenticatedException)
            {
                // The grant is dead and the store is already cleared. Left in the general branch
                // this looked like a portal hiccup and the loop went on forever: the window kept
                // saying "signed in" and "mounted" off a stale Admin, while every listing and every
                // hydration failed, and only a restart got the user back.
                Console.Error.WriteLine("the portal ended this session; unmounting and asking for a sign-in");
                Admin = null;
                // All of it, not just the engine: the credential has gone with the grant, so this
                // is a sign-out that the portal rather than the user asked for, and a registration
                // outliving it would leave "Helmsley Drive" in the navigation pane for an account
                // nothing can any longer fetch a byte for.
                var keptAt = await Unmount(fromThePollLoop: true);
                Program.DeleteSnapshots();
                SignIn.ForgetBrowserSession();
                ErrorMessage = "Signing in again is needed — the portal ended this session."
                    + (keptAt is null ? "" : $" Files it had not taken yet were kept at {keptAt}.");
                Changed();
                return;
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

    async Task StopEngine(bool fromThePollLoop = false)
    {
        _polling?.Cancel();
        // Poll catches everything including cancellation, so this only faults if StopEngine itself
        // was re-entered — which the null-out below makes a no-op rather than a fault.
        //
        // Not awaited when the loop is the caller: a task cannot await itself, and the session
        // ending is exactly that call. It was a deadlock rather than a slow shutdown — the branch
        // never reached the line that clears IsMounted, so an expired session left the window
        // saying "mounted" over a half-stopped engine until the process was killed. There is
        // nothing to wait for there anyway: the pass that would be waited for is the one that
        // threw its way into the catch.
        if (!fromThePollLoop && _loop is not null && _loop != Task.CompletedTask) await _loop;

        if (_mirror is { } mirror)
        {
            // Before the disconnect, and before the credential goes: an upload still running here
            // would otherwise finish against a disconnected sync root and write a snapshot for the
            // account that has just signed out — which the next sign-in then diffs against a tree
            // that was never theirs.
            await mirror.Quiesce(TimeSpan.FromSeconds(10));
            mirror.Dispose();
        }
        if (_connection is not null) SyncRoot.Disconnect(_connection);
        _polling = null;
        _loop = null;
        _mirror = null;
        _connection = null;
    }

    // MARK: - Coming down

    /// <summary>
    /// The drive taken down whole: the engine stopped, the registration removed, and the mirrored
    /// tree removed with it. Answers where anything the portal had not taken was set aside, or null.
    ///
    /// The order is the entire reason it is safe, and it is the order <see cref="Program.SignOut"/>
    /// keeps too: what exists only here is identified while the filter can still be asked, and
    /// nothing is deleted until the registration that would route those deletes back into the
    /// engine has gone.
    ///
    /// Runs on the pool and never on the dispatcher — it awaits the poll loop and a quiesce.
    /// </summary>
    async Task<string?> Unmount(bool fromThePollLoop = false)
    {
        // Stop the poll and drain what is still in flight, but stay connected for one more moment:
        // which of these files the portal already holds is a question only the filter can answer,
        // and it will not answer it for a cloud file whose provider has gone. So the tree is read
        // while there is still somebody behind the sync root, and only then torn down.
        _polling?.Cancel();
        if (!fromThePollLoop && _loop is not null) await _loop;
        if (_mirror is { } running) await running.Quiesce(TimeSpan.FromSeconds(10));

        var keep = LocalTree.LocalOnly(_root);

        await StopEngine(fromThePollLoop);

        // Best-effort, and deliberately not allowed to fail what follows. A root already
        // unregistered from a console throws here, and letting that through used to skip
        // everything below — leaving the credential on disk under a window still saying "Signed
        // in", which on a shared machine is the one outcome the sign-out button must not have.
        try { SyncRoot.Unregister(_root); }
        catch (Exception e) { Console.Error.WriteLine($"unregister failed: {e.Message}"); }

        var keptAt = LocalTree.Discard(_root, keep);

        // The snapshot described the tree that has just gone, and a snapshot outliving its tree is
        // not merely stale: every folder it remembers counts as materialised, so the next mount's
        // first pass would recreate their placeholders eagerly — for a tree whose whole economy is
        // that it fills in as it is browsed.
        Program.DeleteSnapshot(_snapshotPath);

        _registered = false;
        IsMounted = false;
        return keptAt;
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
