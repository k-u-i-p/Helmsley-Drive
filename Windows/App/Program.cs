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

// Named for the root it describes: two instances on two roots — the real drive and a probe —
// must not share one memory of "what each folder held", or each mistakes the other's listings
// for work already done.
var rootKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
    System.Text.Encoding.Unicode.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())))[..16];
var snapshotPath = Path.Combine(Configuration.DataDirectory, $"snapshot-{rootKey}.json");

if (args.Contains("--unregister"))
{
    SyncRoot.Unregister(root);
    Console.WriteLine($"Unregistered sync root at {root}. The folder and its files remain.");
    return;
}

if (args.Contains("--sign-out"))
{
    TokenProvider.Shared.SignOut();
    // The snapshots describe the signed-out account's tree — every root's of them; remembering
    // them into the next account's mirror would have the first pass "deleting" files that were
    // never theirs.
    if (Directory.Exists(Configuration.DataDirectory))
        foreach (var snapshot in Directory.EnumerateFiles(Configuration.DataDirectory, "snapshot*.json"))
            File.Delete(snapshot);
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

// No walk of the tree: startup costs one listing (the root, which is no placeholder and so can
// never ask for itself), everything below fetches the first time it is looked inside, and only
// folders that have been looked inside are re-checked by the poll.
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
