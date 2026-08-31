using System.Text.Json;

namespace HelmsleyDrive.CloudFilter;

/// <summary>
/// Remembers what each folder held the last time it was listed.
///
/// The portal has no change feed, so "what changed" is a listing diffed against this — and a
/// deletion is the one thing a listing cannot show: the server says what is there now, never what
/// has stopped being there. On disk, because a cold start still has to know what was removed while
/// nothing was running; stale entries would otherwise sit in Explorer until someone thought to
/// unregister.
///
/// The Mac counterpart (Mac/FileProvider/SnapshotStore.swift) keeps a history of five per folder,
/// because the system replays sync anchors it sampled on its own schedule. Nothing replays here —
/// the mirror is the only reader of its own past — so one snapshot per folder is the whole store.
/// </summary>
public sealed class SnapshotStore : IDisposable
{
    readonly string _path;
    readonly object _lock = new();

    // Folder id ("" for the root) -> what it held, keyed by item id.
    Dictionary<string, Dictionary<string, RemoteItem>> _folders;

    public SnapshotStore(string path)
    {
        _path = path;
        _folders = Load(path);
        _writer = Task.Run(WriteWhenDirty);
    }

    static Dictionary<string, Dictionary<string, RemoteItem>> Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, RemoteItem>>>(stream) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // No snapshot is a first run; an unreadable one becomes a first run. That is survivable
            // rather than free — a folder the filter has already marked fully populated will never
            // ask to be populated again, so Mirror.SyncPass falls back to what the disk holds to
            // decide what to walk. See its "materialised" test.
            return new();
        }
    }

    /// <summary>
    /// Whether the folder has ever been listed. A folder this store knows is materialised — its
    /// entries exist on disk and the poll keeps them true; one it does not know has either never
    /// been looked inside or lost its snapshot, which the pass tells apart by looking at the disk.
    /// </summary>
    public bool Knows(string? folderId)
    {
        lock (_lock)
        {
            return _folders.ContainsKey(folderId ?? "");
        }
    }

    /// <summary>What the folder held when last listed — empty for one never listed, which is what a first run is.</summary>
    public Dictionary<string, RemoteItem> Current(string? folderId)
    {
        lock (_lock)
        {
            return _folders.TryGetValue(folderId ?? "", out var items) ? new(items) : new();
        }
    }

    /// <summary>Replaces what a folder is known to hold, for a listing nothing could have raced.</summary>
    public void Record(string? folderId, IEnumerable<RemoteItem> items)
    {
        lock (_lock)
        {
            _folders[folderId ?? ""] = Keyed(items);
            Persist();
        }
    }

    /// <summary>
    /// The same, for a pass: what the pass accomplished, laid over whatever the folder holds *now*
    /// rather than over the copy the pass read minutes ago.
    ///
    /// A local write that landed mid-pass — an upload minting a row, a rename — recorded itself
    /// through <see cref="NoteItem"/> while the listing was already in flight. Replacing the folder
    /// wholesale would erase it, and a row the store has forgotten is a row the next save mints
    /// again: the fork the "bytes wearing a known name are a save" rule exists to prevent. So rows
    /// the pass never saw at all are kept, and only rows it did see are overwritten or dropped.
    /// </summary>
    public void Reconcile(string? folderId, IReadOnlyDictionary<string, RemoteItem> before, IEnumerable<RemoteItem> after)
    {
        lock (_lock)
        {
            var settled = Keyed(after);
            if (_folders.TryGetValue(folderId ?? "", out var live))
            {
                foreach (var (id, item) in live)
                    if (!before.ContainsKey(id) && !settled.ContainsKey(id)) settled[id] = item;
            }
            _folders[folderId ?? ""] = settled;
            Persist();
        }
    }

    // Last write wins rather than throwing: a portal that ever answered with one id twice would
    // otherwise abort the whole pass from inside a dictionary constructor.
    static Dictionary<string, RemoteItem> Keyed(IEnumerable<RemoteItem> items)
    {
        var keyed = new Dictionary<string, RemoteItem>();
        foreach (var item in items) keyed[item.Id] = item;
        return keyed;
    }

    /// <summary>
    /// Drops a folder's snapshot — for a folder that no longer exists. Its subfolders' snapshots
    /// go with it, walked through what this store last knew them to contain.
    /// </summary>
    public void Forget(string folderId)
    {
        lock (_lock)
        {
            ForgetLocked(folderId);
            Persist();
        }
    }

    void ForgetLocked(string folderId)
    {
        if (!_folders.Remove(folderId, out var items)) return;
        foreach (var child in items.Values.Where(i => i.IsFolder)) ForgetLocked(child.Id);
    }

    /// <summary>
    /// The same folder under a new row id: what the portal had only declared has been written, and
    /// the reference it was minted (<c>v&lt;parent&gt;_&lt;type&gt;</c>) has given way to a serial.
    /// The entry Explorer is holding survives that, so its memory of what it contains must too —
    /// forgetting it strands a directory the filter has already marked fully populated, which
    /// nothing will ever list again.
    /// </summary>
    public void Rekey(string folderId, string nowFolderId)
    {
        lock (_lock)
        {
            if (folderId == nowFolderId) return;
            if (!_folders.Remove(folderId, out var items)) return;
            _folders[nowFolderId] = items;
            Persist();
        }
    }

    /// <summary>One local write's worth of adjustment, so a pass between the write and the next listing does not undo it.</summary>
    public void NoteItem(string? folderId, RemoteItem item)
    {
        lock (_lock)
        {
            if (!_folders.TryGetValue(folderId ?? "", out var items))
                _folders[folderId ?? ""] = items = new();
            items[item.Id] = item;
            Persist();
        }
    }

    /// <summary>
    /// The item is no longer in the folder that held it — binned, or moved somewhere this store
    /// will re-learn it. Its own snapshot is left alone, because a moved folder still holds
    /// everything it did: <see cref="Forget"/> is the separate sentence for a folder that is gone.
    /// </summary>
    public void NoteRemoved(string id)
    {
        lock (_lock)
        {
            foreach (var items in _folders.Values) items.Remove(id);
            Persist();
        }
    }

    public void NoteRenamed(string id, string name)
    {
        lock (_lock)
        {
            foreach (var items in _folders.Values)
            {
                if (items.TryGetValue(id, out var item)) items[id] = item with { Name = name };
            }
            Persist();
        }
    }

    /// <summary>The row as some folder last knew it, wherever it sits — null for one this store has never held.</summary>
    public RemoteItem? Find(string id)
    {
        lock (_lock)
        {
            foreach (var items in _folders.Values)
                if (items.TryGetValue(id, out var item)) return item;
            return null;
        }
    }

    /// <summary>
    /// The row last known to answer to a name in a folder. It is how bytes that arrive with no
    /// identity — an overwrite that stripped the placeholder, a copy dropped over a file — are
    /// recognised as a save over an existing row rather than mistaken for something new.
    /// </summary>
    public RemoteItem? FindByName(string? folderId, string name)
    {
        lock (_lock)
        {
            return _folders.TryGetValue(folderId ?? "", out var items)
                ? items.Values.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
                : null;
        }
    }

    /// <summary>Wipes everything — for a sign-out, after which nothing remembered is about an account still mirrored.</summary>
    public void ForgetEverything()
    {
        lock (_lock)
        {
            _folders = new();
            _dirty = false;
            try { File.Delete(_path); } catch (DirectoryNotFoundException) { }
        }
    }

    // MARK: - Getting it to disk

    // Every mutation marks the store dirty and one writer drains it. Writing whole on every
    // mutation is what made a copy of ten thousand files ten thousand serialisations of the entire
    // tree; a snapshot half a second stale costs a repeat of work the diff makes idempotent, which
    // is the trade this file is already built on.
    readonly Task _writer;
    readonly CancellationTokenSource _stopping = new();
    bool _dirty;

    void Persist() => _dirty = true;   // called under _lock

    async Task WriteWhenDirty()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), _stopping.Token); }
            catch (OperationCanceledException) { break; }
            Flush();
        }
    }

    /// <summary>Writes now if anything is owed. Called on the way out, so nothing is owed at exit.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                // Written whole and swapped into place: a torn snapshot reads as a first run, a
                // stale one merely repeats work the diff makes idempotent.
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var staging = _path + ".tmp";
                File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(_folders));
                File.Move(staging, _path, overwrite: true);
                _dirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still dirty, so the next tick tries again. A snapshot that cannot be written is
                // a slower next start, not a reason to stop mirroring.
                Console.Error.WriteLine($"snapshot could not be written: {e.Message}");
            }
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try { _writer.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        Flush();
        _stopping.Dispose();
    }
}
