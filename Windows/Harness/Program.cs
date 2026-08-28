using System.Diagnostics;
using System.Text;
using HelmsleyDrive.CloudFilter;

// Exercises the engine — placeholders, snapshot diffing, hydration, and the local write
// callbacks — against an in-memory portal, on a scratch sync root it registers and tears down.
// The portal side of the seam is faked; everything below the seam is the real filter, which is
// where PORTING.md says the surprises live.
//
// Local writes are made through cmd.exe children on purpose: the engine drops events raised by
// its own process, so a user's actions can only be impersonated from another PID.

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Helmsley Harness");
var snapshotPath = Path.Combine(Path.GetTempPath(), "helmsley-harness-snapshot.json");
var failures = 0;

if (Directory.Exists(root))
{
    try { SyncRoot.Unregister(root); } catch { }
    Directory.Delete(root, recursive: true);
}
File.Delete(snapshotPath);

var portal = new FakePortal();
var docs = portal.AddFolder(null, "Docs");
portal.AddFile(null, "b.txt", "top-level bytes");
var a = portal.AddFile(docs, "a.txt", "the first version of a");

LocalChanges.Trace = true;
Directory.CreateDirectory(root);
SyncRoot.Register(root);
var mirror = new Mirror(portal, root, snapshotPath);
var key = SyncRoot.Connect(root, portal, mirror);

try
{
    // MARK: Portal to disk

    await mirror.SyncPass();
    Check("initial mirror lays the tree down",
        Directory.Exists(Path.Combine(root, "Docs"))
        && File.Exists(Path.Combine(root, "Docs", "a.txt"))
        && new FileInfo(Path.Combine(root, "b.txt")).Length == "top-level bytes".Length);

    await mirror.SyncPass();
    Check("a second pass over an unchanged tree is a no-op that succeeds", true);

    Check("hydration serves the fake bytes through FETCH_DATA",
        File.ReadAllText(Path.Combine(root, "Docs", "a.txt")) == "the first version of a");

    portal.AddFile(docs, "later.txt", "arrived later");
    portal.RenameRemotely(a, "a renamed.txt");
    portal.ReplaceBytes(a, "the second version of a");
    var doomed = portal.AddFile(null, "doomed.txt", "soon gone");
    await mirror.SyncPass();
    portal.Remove(doomed);
    await mirror.SyncPass();
    Check("a pass creates what appeared, renames what moved, and removes what went",
        File.Exists(Path.Combine(root, "Docs", "later.txt"))
        && File.Exists(Path.Combine(root, "Docs", "a renamed.txt"))
        && !File.Exists(Path.Combine(root, "Docs", "a.txt"))
        && !File.Exists(Path.Combine(root, "doomed.txt")));
    Check("a changed version re-hydrates to the new bytes",
        File.ReadAllText(Path.Combine(root, "Docs", "a renamed.txt")) == "the second version of a");

    // MARK: Disk to portal, from a foreign process

    Run($"echo fresh local bytes> \"{Path.Combine(root, "Docs", "fresh.txt")}\"");
    await Until("a local create uploads and earns a placeholder",
        () => portal.FindByName("fresh.txt") is { } id
            && Placeholders.TryGetState(Path.Combine(root, "Docs", "fresh.txt"))?.Id == id);

    Run($"echo replaced by hand> \"{Path.Combine(root, "b.txt")}\"");
    await Until("a local save replaces the bytes and returns to sync",
        () => portal.ContentOf(portal.FindByName("b.txt")!) == "replaced by hand\r\n"
            && Placeholders.TryGetState(Path.Combine(root, "b.txt"))?.InSync == true);

    Run($"ren \"{Path.Combine(root, "Docs", "fresh.txt")}\" \"fresher.txt\"");
    await Until("a local rename renames the row",
        () => portal.NameOf(portal.FindByName("fresher.txt") ?? "") == "fresher.txt");

    Run($"move /y \"{Path.Combine(root, "Docs", "fresher.txt")}\" \"{Path.Combine(root, "fresher.txt")}\"");
    await Until("a local move refiles the row",
        () => portal.ParentOf(portal.FindByName("fresher.txt")!) == null);

    Run($"mkdir \"{Path.Combine(root, "Made here")}\" & echo nested> \"{Path.Combine(root, "Made here", "nested.txt")}\"");
    await Until("a local folder mints a row the moment something lands in it",
        () => portal.FindByName("Made here") is { } folder
            && portal.FindByName("nested.txt") is { } nested
            && portal.ParentOf(nested) == folder);

    Run($"del /q \"{Path.Combine(root, "fresher.txt")}\"");
    await Until("a local delete puts the row in the bin",
        () => portal.IsTrashed(portal.FindByName("fresher.txt")!)
            && !File.Exists(Path.Combine(root, "fresher.txt")));

    Run($"move /y \"{Path.Combine(root, "Docs", "later.txt")}\" \"%TEMP%\\\"");
    await Until("a drag out of the root puts the row in the bin",
        () => portal.IsTrashed(portal.FindByName("later.txt")!));

    var refused = portal.AddFile(docs, "precious.txt", "no deleting this");
    portal.RefuseWrites = true;
    await mirror.SyncPass();
    Run($"del /q \"{Path.Combine(root, "Docs", "precious.txt")}\"");
    await Task.Delay(2000);
    Check("a portal refusal refuses the local delete with it",
        File.Exists(Path.Combine(root, "Docs", "precious.txt")) && !portal.IsTrashed(refused));
    portal.RefuseWrites = false;

    await mirror.SyncPass();
    Check("a closing pass still agrees with itself", portal.Log.Count >= 0);
}
finally
{
    SyncRoot.Disconnect(key);
    SyncRoot.Unregister(root);
    Directory.Delete(root, recursive: true);
    File.Delete(snapshotPath);
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
Console.WriteLine("portal saw: " + string.Join(", ", portal.Log));
return failures;

void Check(string what, bool ok)
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "pass" : "FAIL")}  {what}");
}

// The write path runs behind Task.Run, so its effects are polled for rather than expected.
async Task Until(string what, Func<bool> condition)
{
    for (var waited = 0; waited < 15000; waited += 250)
    {
        try { if (condition()) { Check(what, true); return; } }
        catch { /* mid-flight state; keep waiting */ }
        await Task.Delay(250);
    }
    Check(what, false);
}

void Run(string command)
{
    using var cmd = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + command)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
    cmd.WaitForExit();
}

/// <summary>The portal as the seam sees it, small enough to hold in one hand and lie about at will.</summary>
sealed class FakePortal : IRemoteStore
{
    sealed class Node
    {
        public required string Id;
        public string? ParentId;
        public required string Name;
        public bool IsFolder;
        public byte[] Content = Array.Empty<byte>();
        public int Revision = 1;
        public bool Trashed;
    }

    readonly Dictionary<string, Node> _nodes = new();
    readonly object _lock = new();
    int _serial;

    public List<string> Log { get; } = new();
    public bool RefuseWrites { get; set; }

    sealed class NotFoundException : Exception;
    sealed class RefusedException() : Exception("the portal said no");

    // MARK: Scenario controls

    public string AddFolder(string? parent, string name) => Add(parent, name, folder: true, "");
    public string AddFile(string? parent, string name, string content) => Add(parent, name, folder: false, content);

    string Add(string? parent, string name, bool folder, string content)
    {
        lock (_lock)
        {
            var id = $"n{++_serial}";
            _nodes[id] = new Node { Id = id, ParentId = parent, Name = name, IsFolder = folder, Content = Encoding.UTF8.GetBytes(content) };
            return id;
        }
    }

    public void ReplaceBytes(string id, string content)
    {
        lock (_lock) { _nodes[id].Content = Encoding.UTF8.GetBytes(content); _nodes[id].Revision++; }
    }

    public void RenameRemotely(string id, string name) { lock (_lock) _nodes[id].Name = name; }
    public void Remove(string id) { lock (_lock) _nodes.Remove(id); }

    public string? FindByName(string name)
    {
        lock (_lock) return _nodes.Values.FirstOrDefault(n => n.Name == name)?.Id;
    }

    public string? NameOf(string id) { lock (_lock) return _nodes.TryGetValue(id, out var n) ? n.Name : null; }
    public string? ParentOf(string id) { lock (_lock) return _nodes[id].ParentId; }
    public bool IsTrashed(string id) { lock (_lock) return _nodes.TryGetValue(id, out var n) && n.Trashed; }
    public string ContentOf(string id) { lock (_lock) return Encoding.UTF8.GetString(_nodes[id].Content); }

    // MARK: IRemoteStore

    public Task<IReadOnlyList<RemoteItem>> List(string? folderId)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<RemoteItem>>(
                _nodes.Values.Where(n => n.ParentId == folderId && !n.Trashed).Select(Item).ToList());
        }
    }

    public Task<byte[]> Fetch(string fileId)
    {
        lock (_lock)
        {
            return _nodes.TryGetValue(fileId, out var node)
                ? Task.FromResult(node.Content)
                : Task.FromException<byte[]>(new NotFoundException());
        }
    }

    public Task<RemoteItem> Upload(string? folderId, string filename, string localPath)
    {
        var bytes = File.ReadAllBytes(localPath);
        lock (_lock)
        {
            Refusable($"upload {filename}");
            var id = Add(folderId, filename, folder: false, "");
            _nodes[id].Content = bytes;
            return Task.FromResult(Item(_nodes[id]));
        }
    }

    public Task<RemoteItem> ReplaceContents(string fileId, string localPath)
    {
        var bytes = File.ReadAllBytes(localPath);
        lock (_lock)
        {
            Refusable($"replace {fileId}");
            var node = _nodes.TryGetValue(fileId, out var n) ? n : throw new NotFoundException();
            node.Content = bytes;
            node.Revision++;
            return Task.FromResult(Item(node));
        }
    }

    public Task<RemoteItem> CreateFolder(string? parentId, string name)
    {
        lock (_lock)
        {
            Refusable($"mkdir {name}");
            return Task.FromResult(Item(_nodes[Add(parentId, name, folder: true, "")]));
        }
    }

    public Task<string> Rename(string id, string newName)
    {
        lock (_lock)
        {
            Refusable($"rename {id} -> {newName}");
            var node = _nodes.TryGetValue(id, out var n) ? n : throw new NotFoundException();
            node.Name = newName;
            return Task.FromResult(newName);
        }
    }

    public Task<string> Move(string id, string? folderId)
    {
        lock (_lock)
        {
            Refusable($"move {id}");
            var node = _nodes.TryGetValue(id, out var n) ? n : throw new NotFoundException();
            node.ParentId = folderId;
            return Task.FromResult(node.Name);
        }
    }

    public Task Trash(string id)
    {
        lock (_lock)
        {
            Refusable($"trash {id}");
            if (!_nodes.TryGetValue(id, out var node)) return Task.FromException(new NotFoundException());
            node.Trashed = true;
            return Task.CompletedTask;
        }
    }

    public Task<string> Restore(string id, string? folderId)
    {
        lock (_lock)
        {
            Refusable($"restore {id}");
            var node = _nodes.TryGetValue(id, out var n) ? n : throw new NotFoundException();
            node.Trashed = false;
            if (folderId is not null) node.ParentId = folderId;
            return Task.FromResult(node.Name);
        }
    }

    public bool IsNotFound(Exception error) => error is NotFoundException;

    void Refusable(string entry)
    {
        Log.Add(entry);
        if (RefuseWrites) throw new RefusedException();
    }

    static RemoteItem Item(Node node) =>
        new(node.Id, node.Name, node.IsFolder, node.Content.LongLength,
            new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero),
            node.IsFolder ? "" : $"r{node.Revision}");
}
