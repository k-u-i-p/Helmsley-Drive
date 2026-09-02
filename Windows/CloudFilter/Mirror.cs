using System.Collections.Concurrent;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// Keeps the local tree and the portal tree agreeing, in both directions.
///
/// Portal to disk: a refresh lists one folder, diffs it against the <see cref="SnapshotStore"/>,
/// and acts only on the difference — create what appeared, update what changed version, rename
/// what changed name, delete what went. An item whose version still matches needs no work at all.
/// Which folder, and when, is the economy: the one Explorer has just navigated into, and the
/// ones it is showing, on the poll. Nothing walks. See "Portal to disk" below.
///
/// Disk to portal: the callback handlers below, called by <see cref="LocalChanges"/> when the
/// filter reports a close, a rename or a delete. Each maps one local event onto one portal call
/// and then adjusts the snapshot, so the next pass does not mistake the echo of a local write for
/// a remote change to act on.
/// </summary>
public sealed class Mirror : IDisposable
{
    readonly IRemoteStore _remote;
    readonly string _root;
    readonly SnapshotStore _snapshots;

    // One refresh at a time. A second ask does not merge into the running one — it waits its turn
    // and lists again — so nothing on a callback path may wait for one.
    readonly SemaphoreSlim _pass = new(1, 1);

    // One folder minted at a time: a directory and its first file arrive as separate events, and
    // without the gate both would find no placeholder and mint the portal two rows for one folder.
    readonly SemaphoreSlim _minting = new(1, 1);

    // How many transfers may be in the air at once. A copy of ten thousand files is ten thousand
    // closes in one breath, and one task each would put the portal under a stampede and the thread
    // pool under blocking native work; the ones that wait are no later than the retry would make them.
    readonly SemaphoreSlim _transfers = new(4, 4);

    // Paths with an upload in flight. The value is "a newer save arrived while this one was in
    // flight" — see Upload, where dropping that event rather than remembering it is how the newer
    // bytes used to be lost.
    readonly ConcurrentDictionary<string, bool> _uploading = new(StringComparer.OrdinalIgnoreCase);

    public Mirror(IRemoteStore remote, string root, string snapshotPath)
    {
        _remote = remote;
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _snapshots = new SnapshotStore(snapshotPath);
    }

    // MARK: - Hearing about local changes at all
    //
    // The filter only speaks of placeholders: an ordinary file — which is what everything a user
    // creates begins as, and what an overwriting save can quietly turn a placeholder back into —
    // is written and renamed in silence. So the primary detector is a filesystem watcher,
    // debounced until a path has been quiet for a moment; the filter's close notification stays
    // registered for the placeholder saves it does report, and both funnel into OnClosed, which
    // decides everything from on-disk state rather than from which messenger arrived first.
    //
    // Deletion is the one thing neither covers: the watcher listens for arrivals and writes, and
    // an ordinary file deleted before its upload gave it a row is simply gone. Everything past
    // Placeholders.Convert is a placeholder, and NOTIFY_DELETE answers for those.

    FileSystemWatcher? _watcher;
    readonly ConcurrentDictionary<string, DateTime> _touched = new(StringComparer.OrdinalIgnoreCase);

    public void StartWatching()
    {
        _watcher = new FileSystemWatcher(_root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024 };
        _watcher.Created += (_, e) => _touched[e.FullPath] = DateTime.UtcNow;
        _watcher.Changed += (_, e) => _touched[e.FullPath] = DateTime.UtcNow;
        _watcher.Renamed += (_, e) => _touched[e.FullPath] = DateTime.UtcNow;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
        _ = Task.Run(DrainTouched);
    }

    /// <summary>
    /// The watcher is the only thing that ever looks at a file the portal has no row for, so a
    /// dropped event is not deferred work — it is a file that is never uploaded at all. Windows
    /// also switches the watcher off on anything but an overflow, so rearming is what keeps the
    /// drain loop alive, and a sweep is what stands in for the events nobody received.
    /// </summary>
    void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Console.Error.WriteLine($"watcher failed ({e.GetException().Message}); rearming and sweeping for local-only files");
        try
        {
            if (_watcher is { } watcher)
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception rearm)
        {
            Console.Error.WriteLine($"watcher could not be rearmed ({rearm.Message}); local changes will wait for a restart");
        }
        Spawn(_root, SweepForLocalOnly);
    }

    async Task DrainTouched()
    {
        while (_watcher is { EnableRaisingEvents: true })
        {
            await Task.Delay(500);
            foreach (var (path, at) in _touched)
            {
                // Still being written to — a copy in progress touches its path continuously —
                // or our own placeholder work echoing back; both settle, and settled is when the
                // on-disk state is worth reading.
                if (DateTime.UtcNow - at < TimeSpan.FromSeconds(1.5)) continue;
                if (_touched.TryRemove(path, out _)) Spawn(path, OnClosed);
            }
        }
    }

    /// <summary>
    /// Everything under here that the portal has no row for. Only for the case where the watcher
    /// dropped events: an entry with an identity is the portal's already, and a dehydrated folder
    /// enumerates empty to this process — the filter never populates for the provider's own eyes —
    /// so the walk stays inside what has actually been populated.
    /// </summary>
    async Task SweepForLocalOnly(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (LocalNames.IsTransient(Path.GetFileName(entry))) continue;
            if (Directory.Exists(entry))
            {
                if (Placeholders.TryGetState(entry) is null) await EnsureRemoteFolder(entry);
                await SweepForLocalOnly(entry);
            }
            else if (Placeholders.TryGetState(entry) is null)
            {
                await Upload(entry);
            }
        }
    }

    // MARK: - Work in flight
    //
    // The callbacks and the drain both hand work off rather than hold a filter thread on it, so
    // the mirror has to know what it started: an upload still running after the sync root is
    // disconnected writes a snapshot for an account that has signed out, which the next sign-in
    // then diffs against a tree that was never theirs.

    readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    volatile bool _stopped;

    internal void Spawn(string path, Func<string, Task> work)
    {
        if (_stopped) return;
        var task = Task.Run(() => work(path));
        _inFlight[task] = 0;
        _ = task.ContinueWith(done => _inFlight.TryRemove(done, out _), TaskScheduler.Default);
    }

    /// <summary>Stops taking new work and waits for what is already running. The way out.</summary>
    public async Task Quiesce(TimeSpan within)
    {
        _stopped = true;
        if (_watcher is { } watcher) watcher.EnableRaisingEvents = false;

        var deadline = Task.Delay(within);
        while (!_inFlight.IsEmpty)
        {
            var running = Task.WhenAll(_inFlight.Keys);
            if (await Task.WhenAny(running, deadline).ConfigureAwait(false) == deadline)
            {
                Console.Error.WriteLine($"{_inFlight.Count} local changes were still in flight at shutdown");
                return;
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _watcher?.Dispose();
        _watcher = null;
        // Last, and after the work has been waited for: whatever the running tasks recorded is
        // owed to disk, and the debounced writer will not get another tick.
        _snapshots.Dispose();
    }

    public bool IsUnderRoot(string path)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path this cannot even normalise is not one of ours.
            return false;
        }
    }

    // MARK: - Portal to disk
    //
    // The portal answers for what is being looked at, and for nothing else. Two things cause a
    // folder to be listed. A first look — a foreign process enumerating a directory the filter
    // has never populated, which the populator answers with a listing — and this: the folders
    // Explorer is showing, told to the mirror by whoever can see that (ExplorerWatch, in the
    // app). A folder newly shown is refreshed at once, and the poll re-lists the ones still
    // showing. Neither walks. A folder nobody has opened sits unpopulated and costs nothing;
    // one opened last week and left costs nothing either, until it is opened again.
    //
    // The design this replaced re-listed every folder ever opened, on every pass, breadth-first
    // from the root: a cost that grew with the admin's history rather than with what they were
    // doing, and one request per folder of it.

    /// <summary>A folder as the filter names it — the portal's row id, or null for the root — and where it stands on disk.</summary>
    sealed record Folder(string? Id, string Directory);

    // The folders being shown right now, as last told, and when. Replaced whole on every tell.
    volatile IReadOnlyList<string> _open = Array.Empty<string>();
    DateTime _toldAt = DateTime.MinValue;

    // When each folder was last listed, by whichever route. A folder that becomes open just
    // after its population — which is the ordinary way a folder is first shown — is not listed
    // twice for it. Two seconds is that and no more: a folder navigated out of and back into is
    // a folder the user wants looked at again, and a longer window was measured to swallow it.
    readonly ConcurrentDictionary<string, DateTime> _listedAt = new(StringComparer.OrdinalIgnoreCase);
    public static TimeSpan RecentlyListed { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What is showing now, and which of it was just navigated into. A folder that was not
    /// showing a moment ago counts as navigated into whether or not the teller says so — that
    /// is all a teller without windows to watch (the harness) can say — and a folder the teller
    /// names is refreshed even if it was showing already, which is how going up and coming back
    /// to a folder another window still shows gets its listing. Refreshes run in the background,
    /// in one request, parents before children, so a rename of the parent has settled the child's
    /// path first; a folder listed moments ago is skipped. Folders no longer showing drop out of
    /// the poll.
    /// </summary>
    public void SetOpen(IEnumerable<string> directories, IEnumerable<string>? navigatedInto = null)
    {
        var was = _open;
        var now = Normalised(directories);
        _open = now;
        _toldAt = DateTime.UtcNow;

        var fresh = now
            .Where(d => !was.Contains(d, StringComparer.OrdinalIgnoreCase))
            .Concat(navigatedInto is null ? Array.Empty<string>() : Normalised(navigatedInto))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => !_listedAt.TryGetValue(d, out var at) || DateTime.UtcNow - at >= RecentlyListed)
            .OrderBy(Depth)
            .ToList();
        if (fresh.Count == 0) return;
        // Claimed before the work starts, so the next tell does not queue the same listing again.
        foreach (var d in fresh)
        {
            _listedAt[d] = DateTime.UtcNow;
            Console.WriteLine($"@ {d} (navigated into)");
        }
        Spawn(fresh[0], _ => RefreshMany(fresh));
    }

    List<string> Normalised(IEnumerable<string> directories) => directories
        .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
        .Where(IsUnderRootOrRoot)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The populator's listing, for a folder looked into for the first time — or again, after a prune: its snapshot from then on, and its latest listing.</summary>
    public void NoteListed(string? folderId, string directory, IReadOnlyList<RemoteItem> items)
    {
        _snapshots.Record(folderId, items);
        _listedAt[directory] = DateTime.UtcNow;
        _pruned.TryRemove(directory, out _);
    }

    /// <summary>
    /// The poll: re-lists the folders that are showing, and only those, in one request — then
    /// prunes what nobody is looking at. A drive mounted with no Explorer window on it has
    /// nothing open, and costs nothing.
    /// </summary>
    public async Task SyncPass()
    {
        await RefreshMany(_open).ConfigureAwait(false);
        await Prune().ConfigureAwait(false);
    }

    /// <summary>
    /// One folder, named by path, brought true against one listing: what appeared is created,
    /// what changed version is re-dehydrated, what changed name is renamed, what went is removed,
    /// and what only this disk holds is uploaded.
    /// </summary>
    public async Task Refresh(string directory)
    {
        if (Resolve(directory) is { } folder) await Refresh(folder.Id, folder.Directory).ConfigureAwait(false);
    }

    /// <summary>
    /// Several folders, one round trip: the portal answers them all together (<c>POST
    /// listings</c>) and each is brought true in turn, parents before children. One folder is
    /// asked for on its own. This is the poll over what Explorer is showing, and a navigation that
    /// opened several folders at once.
    /// </summary>
    public async Task RefreshMany(IEnumerable<string> directories)
    {
        var folders = Normalised(directories).OrderBy(Depth).Select(Resolve).OfType<Folder>().ToList();
        if (folders.Count == 0) return;
        if (folders.Count == 1)
        {
            await Refresh(folders[0].Id, folders[0].Directory).ConfigureAwait(false);
            return;
        }

        var mine = new TaskCompletionSource();
        // Whichever are not already being refreshed; those are somebody else's, and in hand.
        var claimed = folders.Where(f => _refreshing.TryAdd(f.Directory, mine.Task)).ToList();
        try
        {
            if (claimed.Count == 0) return;
            await _pass.WaitAsync().ConfigureAwait(false);
            try
            {
                var due = claimed.Where(f => Directory.Exists(f.Directory) && !_pruned.ContainsKey(f.Directory)).ToList();
                if (due.Count == 0) return;

                IReadOnlyList<FolderListing> answers;
                try { answers = await _remote.ListManyLocally(due.Select(f => f.Id).ToList()).ConfigureAwait(false); }
                catch (Exception e)
                {
                    // Transient, and the whole round: every snapshot still describes the last
                    // listing acted on, so the next round simply has more difference to act on.
                    Console.Error.WriteLine($"listing {due.Count} folders together failed: {e.Message}");
                    return;
                }

                for (var i = 0; i < due.Count; i++)
                {
                    var folder = due[i];
                    if (answers[i].Error is { } error)
                    {
                        // Gone is the parent's refresh to act on; anything else is the next round's.
                        if (!_remote.IsNotFound(error)) Console.Error.WriteLine($"list {folder.Id ?? "/"} failed: {error.Message}");
                        continue;
                    }
                    try
                    {
                        await Apply(folder.Id, folder.Directory, answers[i].Items!).ConfigureAwait(false);
                        _listedAt[folder.Directory] = DateTime.UtcNow;
                    }
                    catch (Exception e)
                    {
                        // One folder must not cost the round the others.
                        Console.Error.WriteLine($"refresh of {folder.Directory} failed: {e.Message}");
                    }
                }
            }
            finally { _pass.Release(); }
        }
        finally
        {
            foreach (var folder in claimed) _refreshing.TryRemove(folder.Directory, out _);
            mine.SetResult();
        }
    }

    /// <summary>
    /// A directory as a folder the portal can be asked about: the root, or a placeholder whose
    /// identity says which row it is. An ordinary directory wearing a listed name has no identity
    /// yet — its parent's refresh is what adopts it — so it is left rather than guessed at.
    /// </summary>
    Folder? Resolve(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (PathsEqual(directory, _root)) return new Folder(null, directory);
        if (!Directory.Exists(directory)) return null;
        try
        {
            if (Placeholders.TryGetState(directory) is { } state) return new Folder(state.Id, directory);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"could not read the identity of {directory} ({e.Message})");
            return null;
        }
        Console.Error.WriteLine($"{directory} carries no row identity; a refresh of its parent is what gives it one");
        return null;
    }

    // Refreshes in flight, by directory. Never two of the same folder at once, and never two
    // back to back for one moment's asking: a second ask while one runs — the poll and a
    // navigation naming the same folder in the same breath, two ticks of the watcher — joins the
    // running one and returns when it does. The pass gate below then keeps refreshes of different
    // folders from running over each other.
    readonly ConcurrentDictionary<string, Task> _refreshing = new(StringComparer.OrdinalIgnoreCase);

    async Task Refresh(string? folderId, string directory)
    {
        var mine = new TaskCompletionSource();
        var running = _refreshing.GetOrAdd(directory, mine.Task);
        if (running != mine.Task)
        {
            await running.ConfigureAwait(false);
            return;
        }
        try
        {
            await _pass.WaitAsync().ConfigureAwait(false);
            try
            {
                // Renamed or removed by a refresh of its parent since it was opened — Explorer
                // follows the rename, and the next tell names the folder as it is now — or
                // pruned, in which case its next look is what lists it.
                if (!Directory.Exists(directory) || _pruned.ContainsKey(directory)) return;
                if (await RefreshFolder(folderId, directory).ConfigureAwait(false) is not null)
                    _listedAt[directory] = DateTime.UtcNow;
            }
            finally { _pass.Release(); }
        }
        finally
        {
            _refreshing.TryRemove(directory, out _);
            // Joiners are released whatever happened here; a fault is the asker's to hear.
            mine.SetResult();
        }
    }

    // MARK: - Pruning
    //
    // A populated folder nobody is looking at is a stale one: the filter never asks about it
    // again, the poll covers only what Explorer shows, and a script reading it directly sees it
    // as it was last listed, however long ago. So a folder idle for long enough is emptied and
    // put back on demand, and its next look — Explorer's, a script's, anyone's — is a population,
    // which is a fresh listing. Direct access is then at most PruneAfter stale, and the tree on
    // disk stays the size of what is being used.
    //
    // What is never pruned: the root, which is no placeholder; anything Explorer shows, and its
    // ancestors, which the navigation pane holds; and anything holding bytes the portal does not
    // have — a local create, an unsynced save — or a file pinned to stay on this device. A file
    // somebody has open protects its folder by the handle: the delete fails on it, and the folder
    // is listed again at once to put back what went rather than left half empty. Not by recency
    // of use — that was tried, and the indexer and the antimalware scan open and close every
    // fresh file, which made a folder they had just been through look like one in use. What this
    // cannot see is a folder open in another program's Open or Save dialog without a file open.

    public static TimeSpan PruneAfter { get; set; } = TimeSpan.FromMinutes(5);

    // How long a tell of what is showing is trusted. A watcher that has gone quiet cannot say
    // what Explorer has open, and a prune on its last word would empty folders out from under
    // windows it can no longer see.
    static readonly TimeSpan TellPatience = TimeSpan.FromSeconds(10);

    readonly DateTime _started = DateTime.UtcNow;

    // Emptied and on demand, awaiting a look. A refresh must not run on one: entries created
    // into an on-demand directory are the callback storm PORTING.md describes.
    readonly ConcurrentDictionary<string, byte> _pruned = new(StringComparer.OrdinalIgnoreCase);

    async Task Prune()
    {
        if (DateTime.UtcNow - _toldAt > TellPatience) return;
        await _pass.WaitAsync().ConfigureAwait(false);
        try
        {
            var due = new List<string>();
            Consider(_root, due, isRoot: true);
            foreach (var directory in due) await PruneOne(directory).ConfigureAwait(false);
        }
        finally { _pass.Release(); }
    }

    /// <summary>
    /// Whether this whole subtree may go. If it may, the parent decides; if it may not, whichever
    /// of its children may go on their own are put on the list. An unpopulated directory
    /// enumerates empty to this process, so the walk stays inside what is populated — which,
    /// once pruning has settled, is what is being used.
    /// </summary>
    bool Consider(string directory, List<string> due, bool isRoot = false)
    {
        List<string> subdirectories;
        try { subdirectories = Directory.EnumerateDirectories(directory).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }

        var wholly = new List<string>();
        var allChildren = true;
        foreach (var subdirectory in subdirectories)
        {
            if (Consider(subdirectory, due)) wholly.Add(subdirectory);
            else allChildren = false;
        }
        if (!isRoot && allChildren && Prunable(directory)) return true;
        due.AddRange(wholly);
        return false;
    }

    bool Prunable(string directory) =>
        !_pruned.ContainsKey(directory) && !Shown(directory) && Idle(directory) && !HoldsAnythingToKeep(directory);

    bool Shown(string directory) => _open.Any(open =>
        PathsEqual(open, directory)
        || open.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    bool Idle(string directory)
    {
        var last = _started;
        if (_listedAt.TryGetValue(directory, out var listed) && listed > last) last = listed;
        return DateTime.UtcNow - last >= PruneAfter;
    }

    /// <summary>Bytes the portal does not have, a file the user pinned, or a file whose state cannot be read: any of them keeps the folder.</summary>
    static bool HoldsAnythingToKeep(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (LocalNames.IsTransient(Path.GetFileName(file))) continue;
                PlaceholderState? state;
                try { state = Placeholders.TryGetState(file); }
                catch (Exception) { return true; }
                if (state is null || state.DataDirty || state.Pinned) return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    async Task PruneOne(string directory)
    {
        string? id;
        try { id = Placeholders.TryGetState(directory)?.Id; }
        catch (Exception) { return; }
        // No row: the parent's refresh has not adopted it yet, and nothing could list it afresh.
        if (id is null) return;

        var removed = 0;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).ToList())
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
                removed++;
            }
            _snapshots.Forget(id);
            Placeholders.EnableOnDemand(directory);
            _pruned[directory] = 0;
            _listedAt.TryRemove(directory, out _);
            Console.WriteLine($"~ {directory} pruned ({removed} entries); its next look lists it afresh");
        }
        catch (Exception e)
        {
            // Half empty is the one state it must not be left in. The listing puts back what went.
            Console.Error.WriteLine($"could not prune {directory} ({e.Message}); listing it again to put back what went");
            await RefreshFolder(id, directory).ConfigureAwait(false);
        }
    }

    bool IsUnderRootOrRoot(string path) => PathsEqual(path, _root) || IsUnderRoot(path);

    static int Depth(string path) => path.Count(c => c == Path.DirectorySeparatorChar);

    async Task<IReadOnlyList<RemoteItem>?> RefreshFolder(string? folderId, string directory)
    {
        IReadOnlyList<RemoteItem> listing;
        try { listing = await _remote.ListLocally(folderId).ConfigureAwait(false); }
        catch (Exception e) when (_remote.IsNotFound(e))
        {
            // The folder is gone; the pass over its parent is what deletes it. Nothing to do here.
            return null;
        }
        catch (Exception e)
        {
            // Transient. The snapshot still describes the last listing acted on, so skipping the
            // subtree loses nothing — the next pass simply has more difference to act on.
            Console.Error.WriteLine($"list {folderId ?? "/"} failed: {e.Message}");
            return null;
        }
        return await Apply(folderId, directory, listing).ConfigureAwait(false);
    }

    /// <summary>One listing brought onto one directory; answers the listing, which is what the caller records as listed.</summary>
    async Task<IReadOnlyList<RemoteItem>> Apply(string? folderId, string directory, IReadOnlyList<RemoteItem> listing)
    {
        var previous = _snapshots.Current(folderId);
        var current = new Dictionary<string, RemoteItem>();
        foreach (var item in listing) current[item.Id] = item;

        RemoveVanished(directory, previous, current, listing);

        // One enumeration answers every "is this entry there?" below. A stat call per item per
        // pass is a cost the unchanged case should not be paying at ten thousand items a folder.
        var onDisk = Contents(directory);

        // What the snapshot will say this folder holds: each item as this pass left it — as
        // listed where its work succeeded, as it was where it failed, so the next pass sees the
        // difference again and retries rather than believing work that never happened.
        var accomplished = new List<RemoteItem>(listing.Count);

        foreach (var item in listing)
        {
            previous.TryGetValue(item.Id, out var was);
            try
            {
                accomplished.Add(await ApplyOne(folderId, directory, item, was, onDisk).ConfigureAwait(false));
            }
            catch (Exception e)
            {
                // One item must not cost the folder: a name the filesystem refuses, a file held
                // open — log it and let the rest of the listing land.
                Console.Error.WriteLine($"mirror {item.Name} in {directory} failed: {e.Message.Trim()}"
                    + (e is System.Runtime.InteropServices.COMException ? $" (0x{e.HResult:X8})" : ""));
                if (was is not null) accomplished.Add(was);
            }
        }

        _snapshots.Reconcile(folderId, previous, accomplished);

        await UploadLocalOnly(directory, onDisk, listing).ConfigureAwait(false);
        return listing;
    }

    /// <summary>
    /// What the directory holds that the listing does not name and no row claims: a local create
    /// whose upload never landed, or a folder moved in whole. The close path has no second try
    /// to give these — the filter says nothing about an ordinary file, and the watcher speaks
    /// only when one changes — so a first upload that met a sharing violation or a portal that
    /// was down stayed local until the user happened to edit the file again. The pass is where
    /// they are retried; it is already standing in the directory with its entries in hand.
    /// </summary>
    async Task UploadLocalOnly(string directory, HashSet<string> onDisk, IReadOnlyList<RemoteItem> listing)
    {
        var listed = new HashSet<string>(listing.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var name in onDisk.Where(n => !listed.Contains(n) && !LocalNames.IsTransient(n)).ToList())
        {
            var path = Path.Combine(directory, name);
            try
            {
                if (Directory.Exists(path))
                {
                    if (Placeholders.TryGetState(path) is null) await OnArrival(path).ConfigureAwait(false);
                }
                else if (File.Exists(path) && Placeholders.TryGetState(path) is null && Settled(path))
                {
                    await Upload(path).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"upload of {path} failed: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Whether a file has been left alone long enough to be read whole. The watcher's debounce
    /// answers this for the close path; the pass, which meets a file at whatever moment it
    /// happens to be walking, has to ask for itself — a copy still landing would otherwise go up
    /// half-written, and a Word save caught between its rename and its write would go up empty.
    /// </summary>
    static bool Settled(string path)
    {
        try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromSeconds(3); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Entries the snapshot held that the listing no longer names. A name the new listing still
    /// claims is never deleted: that is a folder the portal had only declared materialising as a
    /// real row under the same name, and the create pass refreshes the identity of the entry
    /// Explorer is already holding rather than replacing it.
    /// </summary>
    void RemoveVanished(
        string directory,
        Dictionary<string, RemoteItem> previous,
        Dictionary<string, RemoteItem> current,
        IReadOnlyList<RemoteItem> listing)
    {
        var names = listing.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var was in previous.Values.Where(w => !current.ContainsKey(w.Id)))
        {
            if (names.TryGetValue(was.Name, out var now))
            {
                // The entry survives the identity change, so its memory of what it contains must
                // too: forgotten, the next look inside it would be answered as a first population
                // laid over entries it already holds, rather than as the difference it is.
                if (was.IsFolder && now.IsFolder) _snapshots.Rekey(was.Id, now.Id);
                continue;
            }
            if (was.IsFolder) _snapshots.Forget(was.Id);
            var path = Path.Combine(directory, was.Name);
            if (KeepLocalOnly(path)) DeleteLocal(path, was.IsFolder);
        }
    }

    /// <summary>
    /// Before an entry the portal has stopped naming is removed: anything under it that only this
    /// machine holds — a save that never went up, a file in a folder that never earned a row — is
    /// copied out to the same place a sign-out keeps such files. The delete here is the portal's,
    /// not the user's, and the engine's rule that no local operation may destroy the only copy of
    /// any bytes has no exception for it. Answers whether the delete may go ahead: a copy that
    /// could not be made leaves the entry standing, which the next pass will look at again.
    /// </summary>
    bool KeepLocalOnly(string path)
    {
        if (!IsUnderRoot(path)) return true;
        IReadOnlyList<string> keep;
        try
        {
            if (Directory.Exists(path)) keep = LocalTree.LocalOnly(path);
            else if (File.Exists(path) && LocalTree.IsLocalOnly(path)) keep = new[] { path };
            else return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not tell what under {path} is local-only ({e.Message}); leaving it");
            return false;
        }
        if (keep.Count == 0) return true;

        var keptAt = LocalTree.CopyAside(_root, keep, out var complete);
        if (keptAt is not null)
            Console.WriteLine($"! {path}: {keep.Count} file(s) the portal never took were copied to {keptAt}");
        if (!complete)
            Console.Error.WriteLine($"{path} is kept in place: not everything under it could be set aside");
        return complete;
    }

    /// <summary>
    /// One listed row against the entry standing in for it: created if it is new, renamed if its
    /// name moved, recreated if the disk lost it, re-dehydrated if its bytes changed, and uploaded
    /// if what is on disk is newer than what the portal holds. Answers the row as the snapshot
    /// should now record it, which is not always the row that was listed.
    /// </summary>
    async Task<RemoteItem> ApplyOne(string? folderId, string directory, RemoteItem item, RemoteItem? was, HashSet<string> onDisk)
    {
        var path = Path.Combine(directory, item.Name);
        var settled = item;

        if (was is null)
        {
            Placeholders.CreateOne(directory, item);
            Console.WriteLine($"+ {path}");
        }
        else
        {
            if (!string.Equals(was.Name, item.Name, StringComparison.Ordinal))
            {
                RenameLocal(directory, was, item);
                onDisk.Remove(was.Name);
                onDisk.Add(item.Name);
            }
            if (!onDisk.Contains(item.Name))
            {
                // The snapshot believes in an entry the disk does not hold — a create that failed,
                // a population answered while nothing was listening. Recreating it is the
                // self-heal; believing the snapshot is the bug.
                Placeholders.CreateOne(directory, item);
                Console.WriteLine($"+ {path} (recreated)");
            }
            else if (!item.IsFolder && was.Version != item.Version)
            {
                // A refused update is one the disk did not take — the entry is holding a local
                // write, and these bytes are not the ones on it. Recording the version anyway
                // would tell every later pass the work was done.
                if (Placeholders.Update(path, item, dehydrate: true)) Console.WriteLine($"~ {path}");
                else settled = was;
            }
        }

        // A placeholder still holding an unsynced local write is a save whose upload failed or
        // never ran; the pass is its retry — and the net under the narrow race where a close
        // arriving as a transfer ends is dropped. One cleared by mere metadata motion — a rename
        // clears in-sync as readily as a write — is set right instead, or it would wear a pending
        // badge forever.
        if (onDisk.Contains(item.Name))
        {
            var standing = Placeholders.TryGetState(path);
            if (standing is null)
            {
                // An ordinary entry wearing this row's name. A directory that is only a directory
                // — what a tree removal that stopped at a locked file leaves behind — takes the
                // row's identity and carries on. A file is a save over the row: an overwriting
                // CREATE_ALWAYS strips the placeholder, and a leftover hydrated file looks the
                // same, so the bytes go up under this id rather than being left to sit as an
                // orphan nothing will ever list or upload.
                if (item.IsFolder)
                {
                    Placeholders.Convert(path, item.Id, onDemand: true);
                    Console.WriteLine($"= {path}{Path.DirectorySeparatorChar} adopted ({item.Id})");
                }
                else if (LocalTree.Dehydrated(path))
                {
                    // A placeholder shell whose registration has gone: the attribute says its
                    // bytes are in the bucket and nowhere here, whatever its length claims. Not
                    // adoptable — converting it would claim nothing as the row's content — so it
                    // gives way to a placeholder that can fetch.
                    File.Delete(path);
                    Placeholders.CreateOne(directory, item);
                    Console.WriteLine($"+ {path} (in place of a stale shell)");
                }
                else if (Settled(path)) settled = await Adopt(folderId, directory, path, item).ConfigureAwait(false);
            }
            else if (!item.IsFolder)
            {
                if (standing.DataDirty || Truncated(path, standing)) await Upload(path).ConfigureAwait(false);
                else if (!standing.InSync) Placeholders.MarkInSync(path);
            }
        }

        return settled;
    }

    /// <summary>
    /// An ordinary file standing where a row's placeholder should be. If its bytes hash to the
    /// row's version they are the portal's own and the identity simply goes back on — a re-send
    /// would cost the transfer and move the row's Date Modified for nothing. If they are the
    /// row's length but not its bytes, they are almost certainly nothing at all: unregistration
    /// turns a dehydrated placeholder into an ordinary file of exactly the declared length with
    /// no content, and neither the reparse point nor the recall attribute survives to say so.
    /// Those are set aside, never uploaded, and the row gets a placeholder that can fetch.
    /// Any other length is a save the portal has not seen — a shell is always the declared
    /// length — and it goes up over the row: the snapshot is told the row first so that the
    /// upload finds the name known and replaces rather than mints. Answers the row as the
    /// snapshot should record it afterwards.
    /// </summary>
    async Task<RemoteItem> Adopt(string? folderId, string directory, string path, RemoteItem item)
    {
        long length;
        try { length = new FileInfo(path).Length; }
        catch (IOException) { return item; }

        if (length == item.Size)
        {
            if (HasBytesOf(path, item))
            {
                Placeholders.Convert(path, item.Id);
                Console.WriteLine($"= {path} adopted ({item.Id})");
                return item;
            }

            var keptAt = LocalTree.CopyAside(_root, new[] { path }, out var complete);
            if (!complete)
            {
                Console.Error.WriteLine($"{path} holds bytes that are not the row's and could not be set aside; leaving it");
                return item;
            }
            File.Delete(path);
            Placeholders.CreateOne(directory, item);
            Console.WriteLine($"+ {path} (in place of bytes that were not the row's; those are at {keptAt})");
            return item;
        }

        _snapshots.NoteItem(folderId, item);
        await Upload(path).ConfigureAwait(false);
        return _snapshots.Find(item.Id) ?? item;
    }

    /// <summary>
    /// Whether the file's bytes are the row's. The version is the portal's content hash — the
    /// bucket's MD5, in hex — where the row has one, and an <c>id:</c> reference where it does
    /// not, which verifies nothing and so answers no.
    /// </summary>
    static bool HasBytesOf(string path, RemoteItem item)
    {
        if (item.Version.Length != 32) return false;
        try
        {
            using var stream = File.OpenRead(path);
            var digest = System.Convert.ToHexString(System.Security.Cryptography.MD5.HashData(stream));
            return string.Equals(digest, item.Version, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>What the directory actually holds, by name, for a pass that would otherwise stat every item twice.</summary>
    static HashSet<string> Contents(string directory)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                names.Add(Path.GetFileName(entry));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"could not read {directory}: {e.Message}");
        }
        return names;
    }

    void RenameLocal(string directory, RemoteItem was, RemoteItem now)
    {
        var from = Path.Combine(directory, was.Name);
        var to = Path.Combine(directory, now.Name);

        // Only this row's entry moves. The old name may by now be worn by another row's
        // placeholder — a remote create that claimed it earlier in this same pass — and moving
        // that one would carry the wrong identity to the new name and leave the claimant missing.
        // An ordinary file there is a save over this row, and follows the name as it always has.
        if (Path.Exists(from) && Placeholders.TryGetState(from) is { } standing
            && !string.Equals(standing.Id, was.Id, StringComparison.Ordinal))
        {
            Console.WriteLine($"> {from} is {standing.Id}'s now; {now.Name} is created afresh");
            if (!Path.Exists(to)) Placeholders.CreateOne(directory, now);
            return;
        }

        try
        {
            if (now.IsFolder) Directory.Move(from, to);
            else File.Move(from, to);
            Console.WriteLine($"> {from} -> {now.Name}");
        }
        catch (IOException) when (!Path.Exists(from))
        {
            // Already at the new name — the local rename this listing echoes — or missing
            // entirely, in which case creating it is the whole of what renaming meant.
            if (!Path.Exists(to)) Placeholders.CreateOne(directory, now);
        }
    }

    void DeleteLocal(string path, bool isFolder)
    {
        // The name came from the portal, and a name is not a path: Path.Combine hands back its
        // second argument whole when that argument is rooted, so a row called C:\Users\Ben\Documents
        // would address exactly that. LocalNames takes the separators out on the way in; this is
        // the second lock on the same door, because what is behind it is a recursive delete.
        if (!IsUnderRoot(path))
        {
            Console.Error.WriteLine($"refusing to remove {path}: it is not inside the drive");
            return;
        }
        try
        {
            if (isFolder) Directory.Delete(path, recursive: true);
            else File.Delete(path);
            Console.WriteLine($"- {path}");
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
        catch (Exception e)
        {
            // Held open, most likely. It lingers until a later pass finds it deletable.
            Console.Error.WriteLine($"could not remove {path}: {e.Message}");
        }
    }

    // MARK: - Disk to portal

    /// <summary>
    /// A handle under the root closed. If it left an ordinary file behind, that file is a local
    /// create the portal has no row for yet; if it left a placeholder marked out of sync, it is a
    /// save whose bytes the portal no longer has. Either way the answer is an upload.
    /// </summary>
    public async Task OnClosed(string path)
    {
        try
        {
            // An editor's working file is not a document, and the safe-save dance it belongs to
            // is handled by the rename and delete callbacks rather than by uploading its steps.
            if (LocalNames.IsTransient(Path.GetFileName(path))) return;
            if (Directory.Exists(path))
            {
                // A directory with no identity is a local mkdir — or a folder moved in whole from
                // outside the drive, which the watcher reports as one event for the directory and
                // none at all for what is inside it. OnArrival mints the row and then walks the
                // contents, which for a moved-in folder is the only way its files are ever heard
                // of. (EnsureRemoteFolder also runs lazily, the moment anything inside needs a
                // parent id.)
                if (Placeholders.TryGetState(path) is null) await OnArrival(path).ConfigureAwait(false);
                return;
            }
            if (!File.Exists(path)) return;
            await Upload(path).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The file stays out of sync on disk, which is the truth; the next close of it, or
            // the next refresh of its folder — which looks for exactly these — is the retry.
            Console.Error.WriteLine($"upload of {path} failed: {e.Message}");
        }
    }

    /// <summary>
    /// Gets whatever is on disk at this path to the portal, once, whoever asked and however often.
    ///
    /// A close landing while a transfer is running used to be dropped outright, which lost those
    /// bytes: the transfer already in the air would finish, mark the placeholder in sync, and clear
    /// the one record on disk that anything was still owed. So the second close is remembered
    /// instead, and the loop runs again for it.
    /// </summary>
    async Task Upload(string path)
    {
        // Claim the path, or leave a mark for whoever holds it — and each of those has to be one
        // atomic step. A mark written after the holder had already let go was an entry nobody
        // would ever clear: every later upload of that path, from the close callback, the watcher
        // and the pass alike, found it, left its own mark, and returned, and the file stayed dirty
        // until the app restarted. So a failed claim marks conditionally, and a failed mark — the
        // holder left between the two calls — goes back round to claim.
        while (!_uploading.TryAdd(path, false))
        {
            if (_uploading.TryUpdate(path, true, false) || _uploading.TryUpdate(path, true, true)) return;
        }
        await _transfers.WaitAsync().ConfigureAwait(false);
        try
        {
            var minted = false;
            do
            {
                _uploading[path] = false;
                if (!File.Exists(path)) continue;

                var state = Placeholders.TryGetState(path);
                if (state is null)
                {
                    await UploadNew(path).ConfigureAwait(false);
                    minted = true;
                }
                // Replace's own in-sync mark is skipped when a close lands mid-transfer, so a save
                // that raced it is still standing here as a dirty placeholder and needs no special
                // case. UploadNew's does not have that choice — a file with no row has to be given
                // one, and converting it says in-sync — so the iteration after a mint re-sends
                // unconditionally rather than trusting a flag that mint just cleared.
                else if (state.DataDirty || minted || Truncated(path, state))
                {
                    await Replace(path, state.Id).ConfigureAwait(false);
                    minted = false;
                }
                else if (!state.InSync) Placeholders.MarkInSync(path);
            }
            // Leaving is the same atomic step in reverse: the claim goes only if nothing has
            // marked it since the top of this iteration, and a mark found here is another turn.
            while (!_uploading.TryRemove(new KeyValuePair<string, bool>(path, false)));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException
            || (e is System.Runtime.InteropServices.COMException
                && e.HResult is unchecked((int)0x80070002) or unchecked((int)0x80070003)))
        {
            // Gone between the existence check and the read — renamed or moved on by the user
            // while the debounce was still counting, which a safe save does within milliseconds.
            // Not a failed upload; there is nothing at this path to upload.
            _uploading.TryRemove(path, out _);
        }
        catch
        {
            // Thrown out mid-loop the claim is still standing; it comes down whatever it says, or
            // the path is stranded exactly as the race above used to strand it.
            _uploading.TryRemove(path, out _);
            throw;
        }
        finally
        {
            _transfers.Release();
        }
    }

    /// <summary>
    /// A save that emptied the file. ModifiedDataSize cannot describe it — a truncate leaves no
    /// modified range to point at — so the verdict that means an upload reads it as mere metadata
    /// motion, and the emptying is quietly reverted by the next dehydrate.
    ///
    /// Emptied, specifically, and not merely "a length that disagrees with the row". During a pass
    /// the row this is compared against is the one the folder held *before* the listing being
    /// applied, so any remote size change looks like a local one — and answering that with an
    /// upload pushes stale local bytes over the portal's newer version. Zero against a row that is
    /// not zero is the one disagreement that cannot be a remote change wearing local clothes.
    /// </summary>
    bool Truncated(string path, PlaceholderState state)
    {
        if (state.InSync || state.HasModifiedBytes) return false;
        if (_snapshots.Find(state.Id) is not { IsFolder: false, Size: > 0 }) return false;
        try { return new FileInfo(path).Length == 0; }
        catch (IOException) { return false; }
    }

    async Task UploadNew(string path)
    {
        var parentId = await EnsureRemoteFolder(Path.GetDirectoryName(path)!).ConfigureAwait(false);
        var name = Path.GetFileName(path);

        // An ordinary file wearing a name the folder's last listing already had is not new —
        // it is a save that shed its placeholder, an overwriting CREATE_ALWAYS above all. The
        // bytes replace the known row and the identity goes back on; minting a second row
        // would fork the file.
        var known = _snapshots.FindByName(parentId, name);
        var row = known is { IsFolder: false }
            ? await ReplaceKnown(parentId, known.Id, path).ConfigureAwait(false)
            : await _remote.Upload(parentId, name, path).ConfigureAwait(false);

        Placeholders.Convert(path, row.Id);
        // Under the name the entry wears on disk, not the one the portal answered with: a taken
        // name is numbered server-side, and it is the next pass seeing the two disagree that
        // renames the local entry onto the portal's answer. Recording the portal's name here
        // instead would have that pass create a second entry beside this one.
        _snapshots.NoteItem(parentId, row with { Name = name });
        if (!string.Equals(LocalNames.Legal(row.Name), name, StringComparison.Ordinal))
            Console.WriteLine($"^ {path} landed as {row.Name}; the next pass renames it");
        Console.WriteLine($"^ {path} ({(known is null ? "new, " : "")}{row.Id})");
    }

    async Task<RemoteItem> ReplaceKnown(string? parentId, string id, string path)
    {
        try { return await _remote.ReplaceContents(id, path).ConfigureAwait(false); }
        catch (Exception e) when (_remote.IsNotFound(e))
        {
            // The row went while the bytes were local-only. They are still the newest anywhere;
            // they go up as the new file they have effectively become — into the folder the caller
            // already resolved, rather than wherever a second guess would put them.
            return await _remote.Upload(parentId, Path.GetFileName(path), path).ConfigureAwait(false);
        }
    }

    async Task Replace(string path, string id)
    {
        try
        {
            var row = await _remote.ReplaceContents(id, path).ConfigureAwait(false);
            // Only if nothing landed while the transfer ran. Marking in sync clears the one record
            // on disk that a newer save exists, so it must never outrun the bytes it claims for.
            if (!(_uploading.TryGetValue(path, out var again) && again)) Placeholders.MarkInSync(path);
            _snapshots.NoteItem(ParentFolderId(path), row with { Name = Path.GetFileName(path) });
            Console.WriteLine($"^ {path}");
        }
        catch (Exception e) when (_remote.IsNotFound(e))
        {
            // The row is gone from the portal — trashed or deleted under us. The local bytes are
            // not discarded here; the next pass removes the entry once its folder listing says so.
            Console.Error.WriteLine($"upload of {path}: the portal no longer has this file");
        }
    }

    /// <summary>
    /// The id of the folder a path sits in — null at the root, read off the directory placeholder
    /// otherwise. The placeholder identity round trip is the whole identity scheme, and a
    /// directory that is neither the root nor a placeholder has no answer: reporting null for it
    /// would file the item at the top of the tree, where the next pass over the root would find a
    /// row the root does not hold and delete whatever shares its name.
    /// </summary>
    string? ParentFolderId(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (PathsEqual(directory, _root)) return null;
        return Placeholders.TryGetState(directory)?.Id
            ?? throw new InvalidOperationException($"{directory} has no row of its own yet.");
    }

    /// <summary>
    /// A directory's row id, minting rows up the chain for directories that have none — which is
    /// what a locally created folder is until something makes it real to the portal.
    /// </summary>
    async Task<string?> EnsureRemoteFolder(string directory)
    {
        // Bounded, because this is also reached from inside a held rename: the filter is holding
        // the user's operation while it waits, and Explorer hanging on a semaphore behind a stalled
        // upload is worse than a refusal the user can retry.
        if (!await _minting.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false))
            throw new TimeoutException("Another folder is still being created on the portal.");
        try { return await MintFolders(directory).ConfigureAwait(false); }
        finally { _minting.Release(); }
    }

    async Task<string?> MintFolders(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (PathsEqual(directory, _root)) return null;
        if (Placeholders.TryGetState(directory) is { } state) return state.Id;

        var parentId = await MintFolders(Path.GetDirectoryName(directory)!).ConfigureAwait(false);
        var name = Path.GetFileName(directory);
        var row = await _remote.CreateFolder(parentId, name).ConfigureAwait(false);
        Placeholders.Convert(directory, row.Id);
        _snapshots.NoteItem(parentId, row with { Name = name });
        if (!string.Equals(LocalNames.Legal(row.Name), name, StringComparison.Ordinal))
            Console.WriteLine($"^ {directory} landed as {row.Name}; the next pass renames it");
        Console.WriteLine($"^ {directory}{Path.DirectorySeparatorChar} (new folder, {row.Id})");
        return row.Id;
    }

    /// <summary>
    /// A rename the filter is holding for an answer. True lets it happen; false refuses it, and
    /// Explorer tells the user — with less to say than the portal's own sentence, which is why
    /// the refusal is also logged here.
    ///
    /// <paramref name="sourceIn"/> and <paramref name="targetIn"/> are the platform's own answer to
    /// whether each end is inside the drive, which is not the same as a prefix test on the path:
    /// the filter has already expanded short names and resolved mount points, and a junction is
    /// where the two would disagree — with a plain rename read as a drag out of the drive, and the
    /// row binned for it.
    /// </summary>
    public bool OnRenaming(string sourcePath, string targetPath, string? identity, bool sourceIn, bool targetIn)
    {
        if (identity is null) return true; // No row yet; arrival handling picks up the bytes.
        try
        {
            if (sourceIn && !targetIn)
            {
                // Dragged out of the drive — to the recycle bin, or anywhere else. Either way it
                // left the tree, and the portal's bin is what makes that recoverable.
                Trash(identity);
                return true;
            }
            if (!sourceIn && targetIn)
            {
                // A placeholder coming back — a recycle-bin restore. The row is in the portal's
                // bin; put it back where the entry landed, the top of the tree included.
                var into = Await(EnsureRemoteFolder(Path.GetDirectoryName(targetPath)!));
                var name = Await(_remote.Restore(identity, into));
                Console.WriteLine($"restored {name} ({identity})");
                return true;
            }
            if (!sourceIn) return true;

            var oldDirectory = Path.GetDirectoryName(sourcePath)!;
            var newDirectory = Path.GetDirectoryName(targetPath)!;
            var newName = Path.GetFileName(targetPath);

            // What the portal calls the row, which is not always what the entry was called here:
            // a name it was parked under (below) was never sent.
            var portalName = _snapshots.Find(identity)?.Name ?? Path.GetFileName(sourcePath);

            if (LocalNames.IsTransient(newName) && !LocalNames.IsTransient(portalName)
                && PathsEqual(oldDirectory, newDirectory))
            {
                // A safe save's first step, Word's above all: the original is set aside under a
                // working name while the new bytes take its place, and then deleted. The portal
                // is told none of it. The row keeps its name, so the new bytes arriving under
                // that name are a save over it rather than a new row; and the delete of the
                // parked entry is dropped by OnDeleting on the same test. Sent literally, every
                // save renamed the row, binned it and minted another for the same document.
                Console.WriteLine($"parked {identity} as {newName}");
                return true;
            }

            if (!PathsEqual(oldDirectory, newDirectory))
            {
                var moving = _snapshots.Find(identity);
                var destination = Await(EnsureRemoteFolder(newDirectory));
                var landed = LocalNames.Legal(Await(_remote.Move(identity, destination)));
                // Forgetting it in the old folder is what stops the pass over that one reading the
                // empty space as a remote deletion; filing it in the new one under the name the
                // entry is about to wear is what stops the pass over *that* one creating a second
                // entry beside it when the destination numbered the name.
                _snapshots.NoteRemoved(identity);
                if (moving is not null) _snapshots.NoteItem(destination, moving with { Name = newName });
                if (!string.Equals(landed, newName, StringComparison.Ordinal))
                    Console.WriteLine($"> {targetPath} landed as {landed}; the next pass renames it");
            }

            // Against the portal's name rather than the entry's: a parked row renamed back to its
            // real name — an editor restoring its backup after a failed save — needs no call at all.
            if (!string.Equals(portalName, newName, StringComparison.Ordinal) && !LocalNames.IsTransient(newName))
            {
                var landed = Await(_remote.Rename(identity, newName));
                _snapshots.NoteRenamed(identity, LocalNames.Legal(landed));
            }
            return true;
        }
        catch (Exception e) when (_remote.IsNotFound(e))
        {
            // The row went under the user's hands. Refusing leaves them fighting an entry that
            // nothing backs; letting it through and forgetting the row lets the next pass take the
            // entry away, which is what the portal already thinks has happened.
            Console.Error.WriteLine($"rename of {sourcePath}: the portal no longer has this row");
            _snapshots.NoteRemoved(identity);
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"rename of {sourcePath} refused: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// A delete the filter is holding for an answer. Maps to the portal's bin, never its permanent
    /// delete — Shift+Delete included, since nothing local should be able to destroy the only
    /// copy of the bytes. (A recycled item goes through the rename path instead: recycling is a
    /// move out of the root.)
    /// </summary>
    public bool OnDeleting(string path, string? identity)
    {
        if (identity is null) return true;
        try
        {
            // A parked entry going (see OnRenaming), the save that parked it having finished: the
            // row it carries lives on under its real name, and must not go to the bin with the
            // working copy. Only when the portal itself knows the row by a working name is the
            // delete a real one.
            var name = Path.GetFileName(path);
            if (LocalNames.IsTransient(name) && _snapshots.Find(identity) is { } row && !LocalNames.IsTransient(row.Name))
            {
                Console.WriteLine($"unparked {identity} ({name} deleted; the row stays as {row.Name})");
                return true;
            }
            Trash(identity);
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"delete of {path} refused: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The reverse of a delete, arriving through the same notification with a flag: the entry is
    /// coming back, so the row must too. A row that is gone for good stays gone — better to refuse
    /// the undelete than to bring back an entry whose bytes nothing can fetch.
    /// </summary>
    public bool OnUndeleting(string path, string? identity)
    {
        if (identity is null) return true;
        try
        {
            // Where it was, which is a different sentence from "the root" and must not be spelled
            // the same way: the portal reads a destination it was given as the caller saying where.
            Await(_remote.RestoreWhereItWas(identity));
            Console.WriteLine($"restored {identity}");
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"undelete of {path} refused: {e.Message}");
            return false;
        }
    }

    void Trash(string identity)
    {
        try
        {
            Await(_remote.Trash(identity));
            Forget(identity);
            Console.WriteLine($"binned {identity}");
        }
        catch (Exception e) when (_remote.IsNotFound(e))
        {
            // Already gone portal-side. The local entry going too is agreement, not a failure.
            Forget(identity);
        }
    }

    /// <summary>A binned row is out of its folder, and if it was a folder its own snapshot describes nothing.</summary>
    void Forget(string identity)
    {
        _snapshots.NoteRemoved(identity);
        _snapshots.Forget(identity);
    }

    /// <summary>
    /// Something landed under the root from outside — a move in, which closes no handles and so
    /// announces its content no other way. Files upload; directories mint rows and recurse.
    /// </summary>
    public async Task OnArrival(string path)
    {
        try
        {
            if (LocalNames.IsTransient(Path.GetFileName(path))) return;
            if (File.Exists(path))
            {
                if (Placeholders.TryGetState(path) is { } state)
                    // A placeholder from outside the tree — a restore the rename callback did not
                    // see. Restore is idempotent enough: restoring a live row does nothing.
                    await _remote.Restore(state.Id, await EnsureRemoteFolder(Path.GetDirectoryName(path)!).ConfigureAwait(false))
                        .ConfigureAwait(false);
                else
                    await Upload(path).ConfigureAwait(false);
                return;
            }
            if (!Directory.Exists(path)) return;

            await EnsureRemoteFolder(path).ConfigureAwait(false);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                await OnArrival(entry).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"upload of arrived {path} failed: {e.Message}");
        }
    }

    /// <summary>For a sign-out: what this mirror remembers is about an account no longer mirrored.</summary>
    public void ForgetEverything() => _snapshots.ForgetEverything();

    /// <summary>
    /// The blocking wait the acknowledged callbacks need. They run on the filter's own threads,
    /// outside any synchronization context, so there is no context for the continuation to be
    /// posted back to and nothing to deadlock against.
    /// </summary>
    static T Await<T>(Task<T> work) => work.GetAwaiter().GetResult();

    static void Await(Task work) => work.GetAwaiter().GetResult();

    static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
