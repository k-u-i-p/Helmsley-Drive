using HelmsleyDrive.App;
using HelmsleyDrive.CloudFilter;

// A console host for now: sign in if needed, register, connect, mirror the portal tree, serve
// hydration until Ctrl+C. The tray app this becomes will do the same things behind an icon.

// The browser's OAuth redirect launches a second instance of this app with the callback URL as an
// argument; its whole job is to hand that to the instance that is waiting, and exit.
var callback = args.FirstOrDefault(a => a.StartsWith("helmsley-drive:", StringComparison.OrdinalIgnoreCase));
if (callback is not null)
{
    SignIn.RelayCallback(callback);
    return;
}

var root = args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Helmsley Drive");

if (args.Contains("--unregister"))
{
    SyncRoot.Unregister(root);
    Console.WriteLine($"Unregistered sync root at {root}. The folder and its files remain.");
    return;
}

if (args.Contains("--sign-out"))
{
    TokenProvider.Shared.SignOut();
    Console.WriteLine("Signed out. The next run will ask again.");
    return;
}

if (!TokenProvider.Shared.IsSignedIn)
    await SignIn.Run();

var admin = await HelmsleyApi.Shared.Whoami();
Console.WriteLine($"Signed in to {Configuration.BaseUri.Host} as {admin.Name ?? admin.Email ?? $"admin {admin.Id}"}");

Directory.CreateDirectory(root);
SyncRoot.Register(root);

var store = new HelmsleyRemoteStore();
var key = SyncRoot.Connect(root, store);
Console.WriteLine($"Connected: {root}");

await Mirror(store, null, root);
Console.WriteLine("Portal tree mirrored. Open it in Explorer; Ctrl+C disconnects (and leaves the root registered — run with --unregister to remove it).");

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
