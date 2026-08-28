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
public sealed class SnapshotStore
{
    readonly string _path;
    readonly object _lock = new();

    // Folder id ("" for the root) -> what it held, keyed by item id.
    Dictionary<string, Dictionary<string, RemoteItem>> _folders;

    public SnapshotStore(string path)
    {
        _path = path;
        _folders = Load(path);
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
            // No snapshot is a first run; an unreadable one becomes a first run, which costs a
            // full re-mirror and loses nothing — every entry it described is recreated tolerantly.
            return new();
        }
    }

    /// <summary>
    /// Whether the folder has ever been listed. A folder this store knows is materialised — its
    /// entries exist on disk and the poll keeps them true; one it does not know has never been
    /// looked inside, and costs nothing until someone does.
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

    /// <summary>Replaces what a folder is known to hold, after a pass has acted on the difference.</summary>
    public void Record(string? folderId, IEnumerable<RemoteItem> items)
    {
        lock (_lock)
        {
            _folders[folderId ?? ""] = items.ToDictionary(i => i.Id);
            Persist();
        }
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

    /// <summary>The item is no longer in the tree — trashed, or moved somewhere this store will re-learn it.</summary>
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
            try { File.Delete(_path); } catch (DirectoryNotFoundException) { }
        }
    }

    void Persist()
    {
        // Written whole and swapped into place: a torn snapshot reads as a first run, a stale one
        // merely repeats work the diff makes idempotent.
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staging = _path + ".tmp";
        File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes(_folders));
        File.Move(staging, _path, overwrite: true);
    }
}
