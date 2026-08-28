using HelmsleyDrive.App;
using HelmsleyDrive.CloudFilter;

// A console host for now: register, connect, mirror the stub tree, serve hydration until Ctrl+C.
// The tray app this becomes will do the same four things behind an icon.

var root = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Helmsley Drive");

if (args.Contains("--unregister"))
{
    SyncRoot.Unregister(root);
    Console.WriteLine($"Unregistered sync root at {root}. The folder and its files remain.");
    return;
}

Directory.CreateDirectory(root);
SyncRoot.Register(root);

var store = new StubRemoteStore();
var key = SyncRoot.Connect(root, store);
Console.WriteLine($"Connected: {root}");

await Mirror(store, null, root);
Console.WriteLine("Stub tree mirrored. Open it in Explorer; Ctrl+C disconnects (and leaves the root registered — run with --unregister to remove it).");

var quit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
await quit.Task;

SyncRoot.Disconnect(key);

async Task Mirror(IRemoteStore remote, string? folderId, string directory)
{
    var items = await remote.List(folderId);
    // Mirroring over an existing tree fails on the already-created entries; first run only, for now.
    // The port of the Mac side's snapshot diffing (FileProvider/SnapshotStore.swift) replaces this.
    Placeholders.Create(directory, items);
    foreach (var item in items.Where(i => i.IsFolder))
        await Mirror(remote, item.Id, Path.Combine(directory, item.Name));
}
