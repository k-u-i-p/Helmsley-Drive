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

    // One pass at a time; a second ask while one runs is the same ask.
    readonly SemaphoreSlim _pass = new(1, 1);

    // One folder minted at a time: a directory and its first file arrive as separate events, and
    // without the gate both would find no placeholder and mint the portal two rows for one folder.
    readonly SemaphoreSlim _minting = new(1, 1);

    // Paths with an upload in flight, so a burst of close events — apps save in flurries — costs
    // one transfer rather than one per event.
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
    // is closed, renamed and deleted in silence. So the primary detector is a filesystem watcher,
    // debounced until a path has been quiet for a moment; the filter's close notification stays
    // registered for the placeholder saves it does report, and both funnel into OnClosed, which
    // decides everything from on-disk state rather than from which messenger arrived first.

    FileSystemWatcher? _watcher;
    readonly ConcurrentDictionary<string, DateTime> _touched = new(StringComparer.OrdinalIgnoreCase);

    public void StartWatching()
    {
        _watcher = new FileSystemWatcher(_root) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024 };
        _watcher.Created += (_, e) => _touched[e.FullPath] = DateTime.UtcNow;
        _watcher.Changed += (_, e) => _touched[e.FullPath] = DateTime.UtcNow;
        _watcher.Renamed += (_, e) => _touched[e.FullPath] = DateTime.UtcNow;
        _watcher.Error += (_, e) =>
            Console.Error.WriteLine($"watcher overwhelmed, local changes may sit until touched again: {e.GetException().Message}");
        _watcher.EnableRaisingEvents = true;
        _ = Task.Run(DrainTouched);
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
                if (_touched.TryRemove(path, out _))
                    _ = Task.Run(() => OnClosed(path));
            }
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public bool IsUnderRoot(string path) =>
        Path.GetFullPath(path).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // MARK: - Portal to disk

    public async Task SyncPass()
    {
        await _pass.WaitAsync();
        try { await SyncFolder(null, _root); }
        finally { _pass.Release(); }
    }

    async Task SyncFolder(string? folderId, string directory)
    {
        IReadOnlyList<RemoteItem> listing;
        try { listing = await _remote.List(folderId); }
        catch (Exception e) when (_remote.IsNotFound(e))
        {
            // The folder is gone; the pass over its parent is what deletes it. Nothing to do here.
            return;
        }
        catch (Exception e)
        {
            // Transient. The snapshot still describes the last listing acted on, so skipping the
            // subtree loses nothing — the next pass simply has more difference to act on.
            Console.Error.WriteLine($"list {folderId ?? "/"} failed: {e.Message}");
            return;
        }

        var previous = _snapshots.Current(folderId);
        var current = listing.ToDictionary(i => i.Id);
        var names = listing.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Removals first, but never of a name the new listing still claims: a folder the portal
        // had only declared materialises as a new row under the same name, and the create pass
        // refreshes the identity of the entry Explorer is already holding rather than replacing it.
        foreach (var was in previous.Values.Where(w => !current.ContainsKey(w.Id)))
        {
            if (was.IsFolder) _snapshots.Forget(was.Id);
            if (names.Contains(was.Name)) continue;
            DeleteLocal(Path.Combine(directory, was.Name), was.IsFolder);
        }

        // What the snapshot will say this folder holds: each item as this pass left it — as
        // listed where its work succeeded, as it was where it failed, so the next pass sees the
        // difference again and retries rather than believing work that never happened.
        var accomplished = new List<RemoteItem>(listing.Count);

        foreach (var item in listing)
        {
            try
            {
                if (!previous.TryGetValue(item.Id, out var was))
                {
                    Placeholders.CreateOne(directory, item);
                    Console.WriteLine($"+ {Path.Combine(directory, item.Name)}");
                }
                else
                {
                    if (!string.Equals(was.Name, item.Name, StringComparison.Ordinal))
                        RenameLocal(directory, was, item);
                    if (!item.IsFolder && was.Version != item.Version)
                    {
                        Placeholders.Update(Path.Combine(directory, item.Name), item, dehydrate: true);
                        Console.WriteLine($"~ {Path.Combine(directory, item.Name)}");
                    }
                }

                // A placeholder still holding an unsynced local write is a save whose upload
                // failed or never ran; the pass is its retry. One cleared by mere metadata
                // motion — a rename clears in-sync as readily as a write — is set right instead,
                // or it would wear a pending badge forever.
                var path = Path.Combine(directory, item.Name);
                if (!item.IsFolder && File.Exists(path)
                    && Placeholders.TryGetState(path) is { } standing)
                {
                    if (standing.DataDirty) await UploadChanged(path);
                    else if (!standing.InSync) Placeholders.MarkInSync(path);
                }

                accomplished.Add(item);
            }
            catch (Exception e)
            {
                // One item must not cost the folder: a name the filesystem refuses, a file held
                // open — log it and let the rest of the listing land.
                Console.Error.WriteLine($"mirror {item.Name} in {directory} failed: {e.Message}");
                if (previous.TryGetValue(item.Id, out var was)) accomplished.Add(was);
            }
        }

        _snapshots.Record(folderId, accomplished);

        foreach (var folder in listing.Where(i => i.IsFolder))
            await SyncFolder(folder.Id, Path.Combine(directory, folder.Name));
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
                if (Placeholders.TryGetState(path) is null) await EnsureRemoteFolder(path);
                return;
            }
            if (!File.Exists(path)) return;

            switch (Placeholders.TryGetState(path))
            {
                case null:
                    await UploadNew(path);
                    break;
                case { DataDirty: true }:
                    await UploadChanged(path);
                    break;
                case { InSync: false }:
                    Placeholders.MarkInSync(path);
                    break;
            }
        }
        catch (Exception e)
        {
            // The file stays out of sync on disk, which is the truth; the next close of it, or
            // the next sync pass, is the retry.
            Console.Error.WriteLine($"upload of {path} failed: {e.Message}");
        }
    }

    async Task UploadNew(string path)
    {
        if (!_uploading.TryAdd(path, true)) return;
        try
        {
            var parentId = await EnsureRemoteFolder(Path.GetDirectoryName(path)!);

            // An ordinary file wearing a name the folder's last listing already had is not new —
            // it is a save that shed its placeholder, an overwriting CREATE_ALWAYS above all. The
            // bytes replace the known row and the identity goes back on; minting a second row
            // would fork the file.
            var known = _snapshots.FindByName(parentId, Path.GetFileName(path));
            var row = known is { IsFolder: false }
                ? await ReplaceKnown(known.Id, path)
                : await _remote.Upload(parentId, Path.GetFileName(path), path);

            Placeholders.Convert(path, row.Id);
            _snapshots.NoteItem(parentId, row);
            Console.WriteLine($"^ {path} ({(known is null ? "new, " : "")}{row.Id})");
        }
        finally { _uploading.TryRemove(path, out _); }
    }

    async Task<RemoteItem> ReplaceKnown(string id, string path)
    {
        try { return await _remote.ReplaceContents(id, path); }
        catch (Exception e) when (_remote.IsNotFound(e))
        {
            // The row went while the bytes were local-only. They are still the newest anywhere;
            // they go up as the new file they have effectively become.
            return await _remote.Upload(ParentFolderId(path), Path.GetFileName(path), path);
        }
    }

    async Task UploadChanged(string path)
    {
        if (!_uploading.TryAdd(path, true)) return;
        try
        {
            var state = Placeholders.TryGetState(path);
            if (state is not { DataDirty: true }) return;
            var row = await _remote.ReplaceContents(state.Id, path);
            Placeholders.MarkInSync(path);
            _snapshots.NoteItem(ParentFolderId(path), row);
            Console.WriteLine($"^ {path}");
        }
        catch (Exception e) when (_remote.IsNotFound(e))
        {
            // The row is gone from the portal — trashed or deleted under us. The local bytes are
            // not discarded here; the next pass removes the entry once its folder listing says so.
            Console.Error.WriteLine($"upload of {path}: the portal no longer has this file");
        }
        finally { _uploading.TryRemove(path, out _); }
    }

    /// <summary>
    /// The id of the folder a path sits in — null at the root, read off the directory placeholder
    /// otherwise. The placeholder identity round trip is the whole identity scheme.
    /// </summary>
    string? ParentFolderId(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (PathsEqual(directory, _root)) return null;
        return Placeholders.TryGetState(directory)?.Id;
    }

    /// <summary>
    /// A directory's row id, minting rows up the chain for directories that have none — which is
    /// what a locally created folder is until something makes it real to the portal.
    /// </summary>
    async Task<string?> EnsureRemoteFolder(string directory)
    {
        await _minting.WaitAsync();
        try { return await MintFolders(directory); }
        finally { _minting.Release(); }
    }

    async Task<string?> MintFolders(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (PathsEqual(directory, _root)) return null;
        if (Placeholders.TryGetState(directory) is { } state) return state.Id;

        var parentId = await MintFolders(Path.GetDirectoryName(directory)!);
        var row = await _remote.CreateFolder(parentId, Path.GetFileName(directory));
        Placeholders.Convert(directory, row.Id);
        _snapshots.NoteItem(parentId, row);
        Console.WriteLine($"^ {directory}{Path.DirectorySeparatorChar} (new folder, {row.Id})");
        return row.Id;
    }

    /// <summary>
    /// A rename the filter is holding for an answer. True lets it happen; false refuses it, and
    /// Explorer tells the user — with less to say than the portal's own sentence, which is why
    /// the refusal is also logged here.
    /// </summary>
    public bool OnRenaming(string sourcePath, string targetPath, string identity)
    {
        if (identity.Length == 0) return true; // No row yet; arrival handling picks up the bytes.
        try
        {
            var sourceIn = IsUnderRoot(sourcePath);
            var targetIn = IsUnderRoot(targetPath);

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
                // bin; put it back where the entry landed.
                var name = _remote.Restore(identity, EnsureRemoteFolder(Path.GetDirectoryName(targetPath)!)
                    .GetAwaiter().GetResult()).GetAwaiter().GetResult();
                Console.WriteLine($"restored {name} ({identity})");
                return true;
            }
            if (!sourceIn) return true;

            var oldDirectory = Path.GetDirectoryName(sourcePath)!;
            var newDirectory = Path.GetDirectoryName(targetPath)!;
            if (!PathsEqual(oldDirectory, newDirectory))
            {
                var destination = EnsureRemoteFolder(newDirectory).GetAwaiter().GetResult();
                _remote.Move(identity, destination).GetAwaiter().GetResult();
                // The next pass re-learns it in its new folder; forgetting it here is what stops
                // the pass over the old one reading the empty space as a remote deletion.
                _snapshots.NoteRemoved(identity);
            }
            var oldName = Path.GetFileName(sourcePath);
            var newName = Path.GetFileName(targetPath);
            if (!string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                var landed = _remote.Rename(identity, newName).GetAwaiter().GetResult();
                _snapshots.NoteRenamed(identity, landed);
            }
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
    public bool OnDeleting(string path, string identity)
    {
        if (identity.Length == 0) return true;
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
    public bool OnUndeleting(string path, string identity)
    {
        if (identity.Length == 0) return true;
        try
        {
            _remote.Restore(identity, null).GetAwaiter().GetResult();
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
            _remote.Trash(identity).GetAwaiter().GetResult();
            _snapshots.NoteRemoved(identity);
            Console.WriteLine($"binned {identity}");
        }
        catch (Exception e) when (_remote.IsNotFound(e))
        {
            // Already gone portal-side. The local entry going too is agreement, not a failure.
            _snapshots.NoteRemoved(identity);
        }
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
                switch (Placeholders.TryGetState(path))
                {
                    case null:
                        await UploadNew(path);
                        break;
                    case { } state:
                        // A placeholder from outside the tree — a restore the rename callback did
                        // not see. Restore is idempotent enough: restoring a live row does nothing.
                        await _remote.Restore(state.Id, await EnsureRemoteFolder(Path.GetDirectoryName(path)!));
                        break;
                }
                return;
            }
            if (!Directory.Exists(path)) return;

            await EnsureRemoteFolder(path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                await OnArrival(entry);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"upload of arrived {path} failed: {e.Message}");
        }
    }

    /// <summary>For a sign-out: what this mirror remembers is about an account no longer mirrored.</summary>
    public void ForgetEverything() => _snapshots.ForgetEverything();

    static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
