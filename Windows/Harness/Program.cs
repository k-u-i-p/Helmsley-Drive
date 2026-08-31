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
mirror.StartWatching();

try
{
    // MARK: Portal to disk

    await mirror.SyncPass();
    Check("startup asks the portal for nothing", portal.Log.Count == 0);

    // Enumerating is what Explorer does when a folder is opened, and enumeration of an
    // unpopulated directory is what triggers FETCH_PLACEHOLDERS — a bare path lookup may not.
    Look(root);
    Look(Path.Combine(root, "Docs"));
    Check("the tree appears as it is looked at",
        Directory.Exists(Path.Combine(root, "Docs"))
        && File.Exists(Path.Combine(root, "Docs", "a.txt"))
        && new FileInfo(Path.Combine(root, "b.txt")).Length == "top-level bytes".Length);
    Check("it was the looking that fetched the listings",
        portal.Log.Contains("list /") && portal.Log.Contains($"list {docs}"));

    var listedOnce = portal.Log.Count;
    await mirror.SyncPass();
    Check("a second pass re-lists what is materialised and changes nothing",
        portal.Log.Count > listedOnce
        && portal.Log.Skip(listedOnce).All(entry => entry.StartsWith("list "))
        && File.Exists(Path.Combine(root, "Docs", "a.txt"))
        && File.Exists(Path.Combine(root, "b.txt")));

    Check("hydration serves the fake bytes through FETCH_DATA",
        TryRead(Path.Combine(root, "Docs", "a.txt")) == "the first version of a");

    portal.AddFile(docs, "later.txt", "arrived later");
    portal.AddFile(docs, "Report: draft*.txt", "illegally named bytes");
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
    Probe(Path.Combine(root, "Docs", "a renamed.txt"));
    Check("a changed version re-hydrates to the new bytes",
        TryRead(Path.Combine(root, "Docs", "a renamed.txt")) == "the second version of a");
    Check("an illegal portal name lands under a legal one",
        TryRead(Path.Combine(root, "Docs", "Report_ draft_.txt")) == "illegally named bytes");

    // MARK: Disk to portal, from a foreign process

    Run($"echo fresh local bytes> \"{Path.Combine(root, "Docs", "fresh.txt")}\"");
    Check("the foreign process could write at all",
        File.Exists(Path.Combine(root, "Docs", "fresh.txt")));
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

    // MARK: Names, identity and cold starts

    portal.AddFile(docs, "Quote A*.txt", "the first");
    portal.AddFile(docs, "Quote A?.txt", "the second");
    await mirror.SyncPass();
    Check("two portal names that collapse to one legal name yield one entry, not a flicker",
        File.Exists(Path.Combine(root, "Docs", "Quote A_.txt"))
        && Directory.GetFiles(Path.Combine(root, "Docs"), "Quote A*.txt").Length == 1);

    // A folder the portal has only declared — no row of its own — that something is finally filed
    // into. Its id changes under a name that does not, and the entry Explorer is holding has to go
    // on resolving: the filter has already marked the directory fully populated and will never
    // offer to list it again, so the snapshot has to follow the identity rather than be dropped.
    var declared = portal.AddFolder(null, "Compliance");
    await mirror.SyncPass();
    Look(Path.Combine(root, "Compliance"));
    var written = portal.Materialise(declared);
    portal.AddFile(written, "filed at last.txt", "the first thing in it");
    await mirror.SyncPass();
    Check("a declared folder that materialises keeps its contents coming",
        File.Exists(Path.Combine(root, "Compliance", "filed at last.txt")));

    // A snapshot that does not survive to the next run — a sign-out that left the tree, an
    // unreadable file — must not freeze a tree the filter considers fully populated. Nothing can
    // recover what was *removed* while no snapshot existed; what must not happen is the pass
    // deciding it has nothing to walk and never looking again.
    await mirror.Quiesce(TimeSpan.FromSeconds(5));
    mirror.Dispose();
    SyncRoot.Disconnect(key);
    File.Delete(snapshotPath);

    portal.AddFile(null, "after the cold start.txt", "arrived while nothing was running");
    mirror = new Mirror(portal, root, snapshotPath);
    key = SyncRoot.Connect(root, portal, mirror);
    mirror.StartWatching();
    await mirror.SyncPass();
    Check("a lost snapshot re-mirrors what is on disk rather than freezing it",
        File.Exists(Path.Combine(root, "after the cold start.txt")));
}
finally
{
    mirror.Dispose();
    SyncRoot.Disconnect(key);
    SyncRoot.Unregister(root);
    Directory.Delete(root, recursive: true);
    File.Delete(snapshotPath);
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
Console.WriteLine("portal saw: " + string.Join(", ", portal.Log));
return failures;

// Declared before the try so the cold-start section can replace them mid-run.
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
    var output = (cmd.StandardOutput.ReadToEnd() + cmd.StandardError.ReadToEnd()).Trim();
    cmd.WaitForExit();
    Console.WriteLine($"  $ {command}" + (cmd.ExitCode == 0 ? "" : $"  => exit {cmd.ExitCode}"));
    if (output.Length > 0) Console.WriteLine($"    {output.ReplaceLineEndings("\n    ")}");
}

string? TryRead(string path)
{
    try { return File.ReadAllText(path); }
    catch (Exception e) { Console.WriteLine($"  read {path}: {e.Message}"); return null; }
}

// Opening a folder, as Explorer does it: an enumeration — and from a foreign process, because
// the filter never asks the provider to populate for the provider's own accesses.
void Look(string directory) => Run($"dir \"{directory}\" >nul");

void Probe(string path)
{
    try
    {
        using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Console.WriteLine($"  probe: {path} is free");
    }
    catch (Exception e)
    {
        Console.WriteLine($"  probe: {path}: {e.Message}");
    }
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

    readonly List<string> _log = new();

    /// <summary>
    /// What the portal was asked for, safe to read from the test while a filter thread is still
    /// appending: population runs on the filter's own threads, and enumerating a List while
    /// another thread adds to it is a spurious failure in an otherwise green suite.
    /// </summary>
    public IReadOnlyList<string> Log { get { lock (_lock) return _log.ToArray(); } }

    volatile bool _refuseWrites;
    public bool RefuseWrites { get => _refuseWrites; set => _refuseWrites = value; }

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

    /// <summary>
    /// The portal writing a folder it had only declared: the same folder under the same name, but
    /// a real row id where there was a minted reference. Its children come with it.
    /// </summary>
    public string Materialise(string id)
    {
        lock (_lock)
        {
            var was = _nodes[id];
            var now = $"n{++_serial}";
            _nodes[now] = new Node { Id = now, ParentId = was.ParentId, Name = was.Name, IsFolder = true };
            foreach (var child in _nodes.Values.Where(n => n.ParentId == id).ToList()) child.ParentId = now;
            _nodes.Remove(id);
            return now;
        }
    }
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
            _log.Add($"list {folderId ?? "/"}");
            return Task.FromResult<IReadOnlyList<RemoteItem>>(
                _nodes.Values.Where(n => n.ParentId == folderId && !n.Trashed).Select(Item).ToList());
        }
    }

    public Task<Stream> Fetch(string fileId)
    {
        lock (_lock)
        {
            return _nodes.TryGetValue(fileId, out var node)
                ? Task.FromResult<Stream>(new MemoryStream(node.Content, writable: false))
                : Task.FromException<Stream>(new NotFoundException());
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

    /// <summary>Into the folder named — null being the root, which is a destination like any other.</summary>
    public Task<string> Restore(string id, string? folderId)
    {
        lock (_lock)
        {
            Refusable($"restore {id} -> {folderId ?? "/"}");
            var node = _nodes.TryGetValue(id, out var n) ? n : throw new NotFoundException();
            node.Trashed = false;
            node.ParentId = folderId;
            return Task.FromResult(node.Name);
        }
    }

    /// <summary>Back where it was, which is the portal's answer to being told no destination at all.</summary>
    public Task<string> RestoreWhereItWas(string id)
    {
        lock (_lock)
        {
            Refusable($"restore {id} in place");
            var node = _nodes.TryGetValue(id, out var n) ? n : throw new NotFoundException();
            node.Trashed = false;
            return Task.FromResult(node.Name);
        }
    }

    public bool IsNotFound(Exception error) => error is NotFoundException;

    void Refusable(string entry)
    {
        _log.Add(entry);
        if (_refuseWrites) throw new RefusedException();
    }

    static readonly DateTimeOffset Stamp = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    static RemoteItem Item(Node node) =>
        new(node.Id, node.Name, node.IsFolder, node.Content.LongLength, Stamp, Stamp,
            node.IsFolder ? "" : $"r{node.Revision}");
}
