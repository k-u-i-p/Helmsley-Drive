using System.Collections.Concurrent;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// Keeps the local tree and the portal tree agreeing, in both directions.
///
/// Portal to disk: a sync pass lists each folder, diffs it against the <see cref="SnapshotStore"/>,
/// and acts only on the difference — create what appeared, update what changed version, rename
/// what changed name, delete what went. An item whose version still matches needs no work at all,
/// which is what makes the pass cheap enough to repeat forever.
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

    // One pass at a time. A second ask does not merge into the running one — it waits its turn and
    // then walks the materialised folders again, one listing each — so nothing on a callback path
    // should ask for a pass.
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
    /// so the walk stays inside what has actually been materialised.
    /// </summary>
    async Task SweepForLocalOnly(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
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

    /// <summary>
    /// Re-lists every *materialised* folder — one the snapshot knows, because population or an
    /// earlier pass listed it — and acts on the difference. Folders nobody has looked inside are
    /// not walked: their placeholders sit unpopulated, and their first enumeration fetches a
    /// listing that is fresh by construction. That asymmetry is the whole economy — the portal
    /// answers for what is being watched, not for everything it holds.
    /// </summary>
    public async Task SyncPass()
    {
        await _pass.WaitAsync().ConfigureAwait(false);
        try
        {
            // Breadth-first from the root, parents before children, so a rename applied to a
            // folder has already settled the path its children are addressed by.
            var due = new Queue<(string? Id, string Directory)>();
            if (IsMaterialised(null, _root)) due.Enqueue((null, _root));
            while (due.TryDequeue(out var folder))
            {
                var listing = await RefreshFolder(folder.Id, folder.Directory).ConfigureAwait(false);
                if (listing is null) continue;
                foreach (var child in listing.Where(i => i.IsFolder))
                {
                    var directory = Path.Combine(folder.Directory, child.Name);
                    if (IsMaterialised(child.Id, directory)) due.Enqueue((child.Id, directory));
                }
            }
        }
        finally { _pass.Release(); }
    }

    /// <summary>
    /// Whether this folder is one the pass has to keep true.
    ///
    /// The snapshot is the ordinary answer. The disk is the fallback, and it matters: once a
    /// folder has been populated the filter marks it fully populated *on disk* and never asks to
    /// populate it again, so a folder whose snapshot has gone — a sign-out that left the tree
    /// behind, an unreadable snapshot file, a folder that materialised under a new row id — would
    /// otherwise be frozen for the life of the registration, with neither the filter nor the pass
    /// willing to look at it. Entries on disk are what populated means; an unpopulated placeholder
    /// directory enumerates empty to this process, so the economy holds.
    /// </summary>
    bool IsMaterialised(string? folderId, string directory)
    {
        if (_snapshots.Knows(folderId)) return true;
        try { return Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Public for the populator: a fetched listing is a materialised folder from then on.</summary>
    public void NoteListed(string? folderId, IReadOnlyList<RemoteItem> items) =>
        _snapshots.Record(folderId, items);

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
                accomplished.Add(await ApplyOne(directory, item, was, onDisk).ConfigureAwait(false));
            }
            catch (Exception e)
            {
                // One item must not cost the folder: a name the filesystem refuses, a file held
                // open — log it and let the rest of the listing land.
                Console.Error.WriteLine($"mirror {item.Name} in {directory} failed: {e.Message}");
                if (was is not null) accomplished.Add(was);
            }
        }

        _snapshots.Reconcile(folderId, previous, accomplished);
        return listing;
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
                // too. Forgetting it here strands a directory the filter has already marked fully
                // populated: the filter will not offer to list it again, and the pass would no
                // longer know to.
                if (was.IsFolder && now.IsFolder) _snapshots.Rekey(was.Id, now.Id);
                continue;
            }
            if (was.IsFolder) _snapshots.Forget(was.Id);
            DeleteLocal(Path.Combine(directory, was.Name), was.IsFolder);
        }
    }

    /// <summary>
    /// One listed row against the entry standing in for it: created if it is new, renamed if its
    /// name moved, recreated if the disk lost it, re-dehydrated if its bytes changed, and uploaded
    /// if what is on disk is newer than what the portal holds. Answers the row as the snapshot
    /// should now record it, which is not always the row that was listed.
    /// </summary>
    async Task<RemoteItem> ApplyOne(string directory, RemoteItem item, RemoteItem? was, HashSet<string> onDisk)
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
        if (!item.IsFolder && onDisk.Contains(item.Name) && Placeholders.TryGetState(path) is { } standing)
        {
            if (standing.DataDirty || Truncated(path, standing)) await Upload(path).ConfigureAwait(false);
            else if (!standing.InSync) Placeholders.MarkInSync(path);
        }

        return settled;
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
            if (Directory.Exists(path))
            {
                // A directory with no identity is a local mkdir; give it a row. (Whether directory
                // handles report closes at all varies — EnsureRemoteFolder also runs lazily, the
                // moment anything inside needs a parent id.)
                if (Placeholders.TryGetState(path) is null) await EnsureRemoteFolder(path).ConfigureAwait(false);
                return;
            }
            if (!File.Exists(path)) return;
            await Upload(path).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // The file stays out of sync on disk, which is the truth; the next close of it, or
            // the next sync pass, is the retry.
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
        if (!_uploading.TryAdd(path, false)) { _uploading[path] = true; return; }
        await _transfers.WaitAsync().ConfigureAwait(false);
        try
        {
            var repeat = false;
            var minted = false;
            do
            {
                _uploading[path] = false;
                if (!File.Exists(path)) return;

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

                repeat = _uploading.TryGetValue(path, out var again) && again;
            }
            while (repeat);
        }
        finally
        {
            _transfers.Release();
            _uploading.TryRemove(path, out _);
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

            if (!string.Equals(Path.GetFileName(sourcePath), newName, StringComparison.Ordinal))
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
