using HelmsleyDrive.App;
using HelmsleyDrive.CloudFilter;

// A console host for now: sign in if needed, register, connect, then keep the mirror true — a
// sync pass at start and on a timer, and the write callbacks in between — until Ctrl+C. The tray
// app this becomes will do the same things behind an icon.

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
var snapshotPath = Path.Combine(Configuration.DataDirectory, "snapshot.json");

if (args.Contains("--unregister"))
{
    SyncRoot.Unregister(root);
    Console.WriteLine($"Unregistered sync root at {root}. The folder and its files remain.");
    return;
}

if (args.Contains("--sign-out"))
{
    TokenProvider.Shared.SignOut();
    // The snapshots describe the signed-out account's tree; remembering them into the next
    // account's mirror would have the first pass "deleting" files that were never theirs.
    try { File.Delete(snapshotPath); } catch (DirectoryNotFoundException) { }
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
var mirror = new Mirror(store, root, snapshotPath);
var key = SyncRoot.Connect(root, store, mirror);
mirror.StartWatching();
Console.WriteLine($"Connected: {root}");

// No walk of the tree: a folder's entries are fetched the first time it is looked inside, and
// only folders that have been looked inside are re-checked by the poll.
Console.WriteLine($"Ready. The tree fills in as it is browsed; browsed folders are re-checked every {Configuration.PollInterval.TotalMinutes:0} minutes. " +
    "Ctrl+C disconnects (and leaves the root registered — run with --unregister to remove it).");

var quit = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Cancel(); };

while (!quit.IsCancellationRequested)
{
    try { await mirror.SyncPass(); }
    catch (Exception e)
    {
        // A failed pass changes nothing on disk; the next one starts from the same snapshot.
        Console.Error.WriteLine($"sync pass failed: {e.Message}");
    }

    try { await Task.Delay(Configuration.PollInterval, quit.Token); }
    catch (OperationCanceledException) { break; }
}

mirror.Dispose();
SyncRoot.Disconnect(key);
