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
if (Directory.Exists(LocalTree.KeptAsideFrom(root))) Directory.Delete(LocalTree.KeptAsideFrom(root), recursive: true);
File.Delete(snapshotPath);

var portal = new FakePortal();
var docs = portal.AddFolder(null, "Docs");
portal.AddFile(null, "b.txt", "top-level bytes");
var a = portal.AddFile(docs, "a.txt", "the first version of a");
var docsDir = Path.Combine(root, "Docs");

LocalChanges.Trace = true;
Directory.CreateDirectory(root);
SyncRoot.Register(root);
var mirror = new Mirror(portal, root, snapshotPath);
var key = SyncRoot.Connect(root, portal, mirror);
mirror.StartWatching();

// The sign-out section tears both down early, so the teardown has to be told what is left.
var connected = true;
var registered = true;

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

    // What Explorer is showing is told to the mirror by ExplorerWatch in the app; here the test
    // stands in for it. Both folders were listed by their population a moment ago, so being
    // shown does not list them again.
    var populated = portal.Log.Count;
    mirror.SetOpen(new[] { root, docsDir });
    await Task.Delay(1000);
    Check("a folder shown just after its population is not listed again for it", portal.Log.Count == populated);
    var batches = portal.Batches;
    await mirror.SyncPass();
    Check("the poll re-lists what is showing, parents first, and nothing else",
        portal.Log.Count == populated + 2
        && portal.Log[populated] == "list /" && portal.Log[populated + 1] == $"list {docs}"
        && File.Exists(Path.Combine(root, "Docs", "a.txt"))
        && File.Exists(Path.Combine(root, "b.txt")));
    Check("and in one request", portal.Batches == batches + 1);
    mirror.SetOpen(new[] { root });

    Check("hydration serves the fake bytes through FETCH_DATA",
        TryRead(Path.Combine(root, "Docs", "a.txt")) == "the first version of a");

    portal.AddFile(docs, "later.txt", "arrived later");
    portal.AddFile(docs, "Report: draft*.txt", "illegally named bytes");
    portal.RenameRemotely(a, "a renamed.txt");
    portal.ReplaceBytes(a, "the second version of a");
    var doomed = portal.AddFile(null, "doomed.txt", "soon gone");
    await Sync(root, docsDir);
    portal.Remove(doomed);
    await Sync(root, docsDir);
    Check("a refresh creates what appeared, renames what moved, and removes what went",
        File.Exists(Path.Combine(root, "Docs", "later.txt"))
        && File.Exists(Path.Combine(root, "Docs", "a renamed.txt"))
        && !File.Exists(Path.Combine(root, "Docs", "a.txt"))
        && !File.Exists(Path.Combine(root, "doomed.txt")));
    Probe(Path.Combine(root, "Docs", "a renamed.txt"));
    Check("a changed version re-hydrates to the new bytes",
        TryRead(Path.Combine(root, "Docs", "a renamed.txt")) == "the second version of a");
    Check("an illegal portal name lands under a legal one",
        TryRead(Path.Combine(root, "Docs", "Report_ draft_.txt")) == "illegally named bytes");

    // MARK: What Explorer showing a folder costs, and what the poll covers

    // Navigating into a folder — it joins what Explorer shows — refreshes it once, however many
    // ticks of the watcher say the same thing.
    await Task.Delay(Mirror.RecentlyListed + TimeSpan.FromSeconds(1));
    portal.AddFile(docs, "seen on a look.txt", "arrived while nobody was looking");
    var docsListed = portal.Log.Count(entry => entry == $"list {docs}");
    mirror.SetOpen(new[] { docsDir });
    mirror.SetOpen(new[] { docsDir });
    await Until("a folder Explorer navigates into is refreshed",
        () => File.Exists(Path.Combine(docsDir, "seen on a look.txt")));
    await Task.Delay(1000);
    Check("once, not once per tick", portal.Log.Count(entry => entry == $"list {docs}") == docsListed + 1);

    // Going up and coming back to a folder that never left the set — another window still shows
    // it, or the round trip fell between two ticks — is a navigation the watcher names, and
    // it is listed again for it.
    await Task.Delay(Mirror.RecentlyListed + TimeSpan.FromSeconds(1));
    portal.AddFile(docs, "seen on the way back.txt", "arrived while the user was upstairs");
    mirror.SetOpen(new[] { docsDir }, navigatedInto: new[] { docsDir });
    await Until("navigating back into a folder still showing refreshes it",
        () => File.Exists(Path.Combine(docsDir, "seen on the way back.txt")));

    // Two asks for the same folder in the same breath — the poll and a navigation, two ticks —
    // are one listing: the second joins the first rather than running beside or behind it.
    await Task.Delay(Mirror.RecentlyListed + TimeSpan.FromSeconds(1));
    portal.ListDelayMs = 500;
    var docsListedBefore = portal.Log.Count(entry => entry == $"list {docs}");
    await Task.WhenAll(mirror.Refresh(docsDir), mirror.Refresh(docsDir), mirror.Refresh(docsDir));
    portal.ListDelayMs = 0;
    Check("three refreshes of one folder at once are one listing",
        portal.Log.Count(entry => entry == $"list {docs}") == docsListedBefore + 1);

    // Navigating away drops a folder out of the poll; the poll re-lists what is showing now.
    var rootListed = portal.Log.Count(entry => entry == "list /");
    mirror.SetOpen(new[] { root });
    await Until("navigating up to the root refreshes the root",
        () => portal.Log.Count(entry => entry == "list /") == rootListed + 1);
    portal.AddFile(null, "at the root.txt", "for the poll to find");
    portal.AddFile(docs, "not looked at.txt", "for the poll to leave");
    var beforePoll = portal.Log.Count;
    await mirror.SyncPass();
    Check("the poll re-lists the folder that is showing and no other",
        portal.Log.Count == beforePoll + 1 && portal.Log[beforePoll] == "list /"
        && File.Exists(Path.Combine(root, "at the root.txt"))
        && !File.Exists(Path.Combine(docsDir, "not looked at.txt")));
    await Sync(docsDir);
    Check("a refresh named by path brings the other one true",
        File.Exists(Path.Combine(docsDir, "not looked at.txt")));
    mirror.SetOpen(Array.Empty<string>());

    // MARK: Pruning what nobody is looking at

    // A populated folder nobody is showing or using is emptied and put back on demand, so that
    // its next look — by anyone — is a fresh listing rather than a stale one. Three seconds
    // stands in for the minutes the app waits.
    Mirror.PruneAfter = TimeSpan.FromSeconds(3);
    await Task.Delay(Mirror.PruneAfter + TimeSpan.FromSeconds(1));

    // A file somebody has open will not delete, so its folder will not empty — and is put back
    // whole rather than left half empty: whatever went before the held one is listed straight back.
    var docsEntries = Directory.GetFileSystemEntries(docsDir).Length;
    using (File.Open(Path.Combine(docsDir, "a renamed.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
    {
        mirror.SetOpen(Array.Empty<string>());
        await mirror.SyncPass();
    }
    Check("a folder with a file held open is not pruned, and is put back whole",
        Directory.GetFileSystemEntries(docsDir).Length == docsEntries && File.Exists(Path.Combine(docsDir, "a renamed.txt")));

    await Task.Delay(Mirror.PruneAfter + TimeSpan.FromSeconds(1));
    mirror.SetOpen(new[] { docsDir });
    await mirror.SyncPass();
    Check("a folder Explorer is showing is not pruned", Directory.EnumerateFileSystemEntries(docsDir).Any());

    portal.RefuseWrites = true;
    Run($"echo mine alone> \"{Path.Combine(docsDir, "kept back.txt")}\"");
    await Task.Delay(Mirror.PruneAfter + TimeSpan.FromSeconds(1));
    mirror.SetOpen(Array.Empty<string>());
    await mirror.SyncPass();
    Check("a folder holding bytes the portal does not have is not pruned",
        File.Exists(Path.Combine(docsDir, "kept back.txt")) && File.Exists(Path.Combine(docsDir, "a renamed.txt")));
    portal.RefuseWrites = false;
    await Sync(docsDir);
    await Until("and the refresh sends those bytes up", () => portal.FindByName("kept back.txt") is not null);

    portal.AddFile(docs, "arrived while idle.txt", "what a stale folder would never show");
    await Task.Delay(Mirror.PruneAfter + TimeSpan.FromSeconds(1));
    mirror.SetOpen(Array.Empty<string>());
    await mirror.SyncPass();
    Check("an idle folder is pruned", !Directory.EnumerateFileSystemEntries(docsDir).Any());
    Look(docsDir);
    Check("and its next look lists it afresh",
        File.Exists(Path.Combine(docsDir, "arrived while idle.txt"))
        && File.Exists(Path.Combine(docsDir, "a renamed.txt"))
        && File.Exists(Path.Combine(docsDir, "kept back.txt")));
    Mirror.PruneAfter = TimeSpan.FromDays(1);

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
    await Sync(docsDir);
    Run($"del /q \"{Path.Combine(root, "Docs", "precious.txt")}\"");
    await Task.Delay(2000);
    Check("a portal refusal refuses the local delete with it",
        File.Exists(Path.Combine(root, "Docs", "precious.txt")) && !portal.IsTrashed(refused));
    portal.RefuseWrites = false;

    // MARK: What the close path cannot hear, and what the pass hears for it

    // A folder moved in from outside the drive — same volume, so a rename rather than a copy —
    // reaches the watcher as one event for the directory and none for anything inside it.
    var staged = Path.Combine(Path.GetTempPath(), "Helmsley Harness moved in");
    if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
    Directory.CreateDirectory(Path.Combine(staged, "Deeper"));
    File.WriteAllText(Path.Combine(staged, "inside.txt"), "came along");
    File.WriteAllText(Path.Combine(staged, "Deeper", "further in.txt"), "and so did this");
    Run($"move /y \"{staged}\" \"{Path.Combine(root, "Moved in")}\"");
    await Until("a folder moved in from outside uploads everything inside it",
        () => portal.FindByName("Moved in") is { } movedIn
            && portal.FindByName("inside.txt") is { } inside && portal.ParentOf(inside) == movedIn
            && portal.FindByName("Deeper") is { } deeper && portal.ParentOf(deeper) == movedIn
            && portal.FindByName("further in.txt") is { } furtherIn && portal.ParentOf(furtherIn) == deeper);

    // A local create whose first upload is refused stays an ordinary file the filter never
    // mentions again; the pass has to find it.
    portal.RefuseWrites = true;
    Run($"echo first try refused> \"{Path.Combine(root, "Docs", "stranded.txt")}\"");
    await Task.Delay(3000);
    Check("a refused first upload leaves the file local-only", portal.FindByName("stranded.txt") is null);
    portal.RefuseWrites = false;
    await Task.Delay(1000);
    await Sync(docsDir);
    await Until("and the next refresh uploads it",
        () => portal.FindByName("stranded.txt") is { } stranded
            && Placeholders.TryGetState(Path.Combine(root, "Docs", "stranded.txt"))?.Id == stranded);

    // Word's save: the original set aside under a working name, the new bytes renamed into its
    // place, the working copy deleted. The row has to come through it with its id and its name.
    var saved = portal.AddFile(docs, "letter.txt", "first draft");
    await Sync(docsDir);
    Run($"echo second draft> \"{Path.Combine(root, "Docs", "~WRD0000.tmp")}\""
        + $" & ren \"{Path.Combine(root, "Docs", "letter.txt")}\" \"~WRL0001.tmp\""
        + $" & ren \"{Path.Combine(root, "Docs", "~WRD0000.tmp")}\" \"letter.txt\""
        + $" & del /q \"{Path.Combine(root, "Docs", "~WRL0001.tmp")}\"");
    // Trimmed, because cmd keeps the space before the "&" in what echo writes.
    await Until("a safe save keeps the row, its name and its id",
        () => portal.NameOf(saved) == "letter.txt" && !portal.IsTrashed(saved)
            && portal.ContentOf(saved).Trim() == "second draft"
            && portal.FindByName("~WRL0001.tmp") is null && portal.FindByName("~WRD0000.tmp") is null
            && Placeholders.TryGetState(Path.Combine(root, "Docs", "letter.txt"))?.Id == saved);

    // The other way editors save: the new bytes moved over the original in one rename. The
    // filter says nothing about the placeholder that goes under it — no delete, no rename — so
    // this is the watcher and the name rule alone, and the row has to come through with its id.
    var replaced = portal.AddFile(docs, "replaced.txt", "before");
    await Sync(docsDir);
    Run($"echo after>\"{Path.Combine(root, "Docs", "repl.new")}\""
        + $" & move /y \"{Path.Combine(root, "Docs", "repl.new")}\" \"{Path.Combine(root, "Docs", "replaced.txt")}\"");
    await Until("a replace in one rename is a save over the row",
        () => !portal.IsTrashed(replaced) && portal.ContentOf(replaced).Trim() == "after"
            && portal.FindByName("repl.new") is null
            && Placeholders.TryGetState(Path.Combine(root, "Docs", "replaced.txt"))?.Id == replaced);

    // The row behind a name replaced by another row — trashed and re-uploaded under the same
    // name — must not go on serving the old bytes under the new id.
    var oldRow = portal.AddFile(docs, "swapped.txt", "the old bytes");
    await Sync(docsDir);
    Check("the old row hydrates", TryRead(Path.Combine(root, "Docs", "swapped.txt")) == "the old bytes");
    portal.Remove(oldRow);
    var newRow = portal.AddFile(docs, "swapped.txt", "the new bytes, and longer");
    await Sync(docsDir);
    Check("a row replaced under the same name re-hydrates to the new row's bytes",
        Placeholders.TryGetState(Path.Combine(root, "Docs", "swapped.txt"))?.Id == newRow
        && TryRead(Path.Combine(root, "Docs", "swapped.txt")) == "the new bytes, and longer");

    // A save the portal has not taken, whose row the portal then drops: the pass removes the
    // entry, and the bytes — the only copy anywhere — go aside rather than with it.
    var dropped = portal.AddFile(docs, "edited then dropped.txt", "as the portal had it");
    await Sync(docsDir);
    portal.RefuseWrites = true;
    Run($"echo my edit> \"{Path.Combine(root, "Docs", "edited then dropped.txt")}\"");
    await Task.Delay(3000);
    portal.Remove(dropped);
    await Sync(docsDir);
    portal.RefuseWrites = false;
    Check("a local edit the portal never took survives the portal dropping its row",
        !File.Exists(Path.Combine(root, "Docs", "edited then dropped.txt"))
        && TryRead(Path.Combine(LocalTree.KeptAsideFrom(root), "Docs", "edited then dropped.txt")) == "my edit\r\n");
    Directory.Delete(LocalTree.KeptAsideFrom(root), recursive: true);

    // MARK: Names, identity and cold starts

    portal.AddFile(docs, "Quote A*.txt", "the first");
    portal.AddFile(docs, "Quote A?.txt", "the second");
    await Sync(docsDir);
    Check("two portal names that collapse to one legal name yield one entry, not a flicker",
        File.Exists(Path.Combine(root, "Docs", "Quote A_.txt"))
        && Directory.GetFiles(Path.Combine(root, "Docs"), "Quote A*.txt").Length == 1);

    // A folder the portal has only declared — no row of its own — that something is finally filed
    // into. Its id changes under a name that does not, and the entry Explorer is holding has to go
    // on resolving: the snapshot follows the identity rather than being dropped, so a refresh
    // under the new id diffs against what the directory already holds.
    var declared = portal.AddFolder(null, "Compliance");
    await Sync(root);
    Look(Path.Combine(root, "Compliance"));
    var written = portal.Materialise(declared);
    portal.AddFile(written, "filed at last.txt", "the first thing in it");
    await Sync(root, Path.Combine(root, "Compliance"));
    Check("a declared folder that materialises keeps its contents coming",
        File.Exists(Path.Combine(root, "Compliance", "filed at last.txt")));

    // A snapshot that does not survive to the next run — a sign-out that left the tree, an
    // unreadable file — must not freeze a tree the filter considers fully populated. Nothing can
    // recover what was *removed* while no snapshot existed; what must not happen is a refresh
    // with nothing to diff against doing nothing.
    await mirror.Quiesce(TimeSpan.FromSeconds(5));
    mirror.Dispose();
    SyncRoot.Disconnect(key);
    File.Delete(snapshotPath);

    portal.AddFile(null, "after the cold start.txt", "arrived while nothing was running");
    var beforeRestart = portal.Log.Count;
    mirror = new Mirror(portal, root, snapshotPath);
    key = SyncRoot.Connect(root, portal, mirror);
    mirror.StartWatching();
    await mirror.SyncPass();
    Check("nothing is showing after a restart, so the poll asks for nothing", portal.Log.Count == beforeRestart);
    await Sync(root);
    Check("a lost snapshot re-mirrors what is on disk rather than freezing it",
        File.Exists(Path.Combine(root, "after the cold start.txt")));

    // MARK: A quit that could not take everything

    // A tree removal with a document still open stops at that file, and the registration and
    // the snapshot are gone regardless. The next mount has to take up what was left as the
    // portal's own rows — and a folder that already held an entry must still be populated once,
    // not listed again on every look because one of its entries could not be created.
    await mirror.Quiesce(TimeSpan.FromSeconds(5));
    var leftover = Path.Combine(root, "Docs", "a renamed.txt");
    using (File.Open(leftover, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var nothingLocal = LocalTree.LocalOnly(root);
        mirror.Dispose();
        SyncRoot.Disconnect(key);
        connected = false;
        SyncRoot.Unregister(root);
        registered = false;
        LocalTree.Discard(root, nothingLocal);
        File.Delete(snapshotPath);
    }
    Check("a locked file keeps only itself and its parents from going",
        File.Exists(leftover) && !File.Exists(Path.Combine(root, "b.txt")) && !Directory.Exists(Path.Combine(root, "Made here")));

    // What unregistration makes of a dehydrated placeholder that did not go: an ordinary file of
    // the declared length holding nothing, with no attribute left to say so. Wearing a row's
    // name and length, it must be neither adopted as the row's bytes nor uploaded as a save.
    var shell = Path.Combine(root, "Docs", "precious.txt");
    File.WriteAllText(shell, new string('x', "no deleting this".Length));
    // Aged, or the pass leaves it alone as a file still being written.
    File.SetLastWriteTimeUtc(shell, DateTime.UtcNow.AddMinutes(-1));

    SyncRoot.Register(root);
    registered = true;
    mirror = new Mirror(portal, root, snapshotPath);
    key = SyncRoot.Connect(root, portal, mirror);
    connected = true;
    mirror.StartWatching();
    await Sync(root, docsDir);
    Check("the next mount takes the leftovers up as the portal's rows and fills in around them",
        Placeholders.TryGetState(leftover)?.Id == a
        && Placeholders.TryGetState(Path.Combine(root, "Docs"))?.Id == docs
        && File.Exists(Path.Combine(root, "b.txt"))
        && TryRead(leftover) == "the second version of a");
    Check("a shell of the right length but the wrong bytes is set aside and fetched afresh, not adopted or uploaded",
        Placeholders.TryGetState(shell)?.Id == refused
        && TryRead(shell) == "no deleting this"
        && portal.ContentOf(refused) == "no deleting this"
        && File.Exists(Path.Combine(LocalTree.KeptAsideFrom(root), "Docs", "precious.txt")));
    if (Directory.Exists(LocalTree.KeptAsideFrom(root))) Directory.Delete(LocalTree.KeptAsideFrom(root), recursive: true);

    var rootListings = portal.Log.Count(entry => entry == "list /");
    var docsListings = portal.Log.Count(entry => entry == $"list {docs}");
    Look(root);
    Look(root);
    Look(docsDir);
    Look(docsDir);
    Check("and a folder that already held an entry is populated once, not on every look",
        portal.Log.Count(entry => entry == "list /") <= rootListings + 1
        && portal.Log.Count(entry => entry == $"list {docs}") <= docsListings + 1
        && Directory.GetFiles(docsDir, "a renamed*").Length == 1);

    // MARK: What a sign-out takes away, and what it must not

    // Stopped but still connected, which is the state a sign-out reads the tree in.
    await mirror.Quiesce(TimeSpan.FromSeconds(5));

    // Written with the mirror stopped, so nothing uploads it: this is what a local create whose
    // upload never landed looks like on disk — bytes with no row anywhere.
    File.WriteAllText(Path.Combine(root, "never uploaded.txt"), "bytes the portal never saw");

    // The order a sign-out runs in, and the reason it runs in that order. The tree is classified
    // while a provider is still behind the sync root, because a cloud file will not answer for
    // itself once there is nobody there; and nothing is removed until the registration that would
    // route those deletes back into the engine has gone.
    var keep = LocalTree.LocalOnly(root);

    mirror.Dispose();
    SyncRoot.Disconnect(key);
    connected = false;
    SyncRoot.Unregister(root);
    registered = false;
    var keptAt = LocalTree.Discard(root, keep);

    Check("a sign-out takes the mirrored tree away", !Directory.Exists(root));
    Check("and keeps back what the portal never took, and only that",
        keptAt is not null
        && File.Exists(Path.Combine(keptAt, "never uploaded.txt"))
        && !File.Exists(Path.Combine(keptAt, "b.txt"))
        && !Directory.Exists(Path.Combine(keptAt, "Docs")));
}
finally
{
    if (connected) { mirror.Dispose(); SyncRoot.Disconnect(key); }
    if (registered) SyncRoot.Unregister(root);
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    if (Directory.Exists(LocalTree.KeptAsideFrom(root))) Directory.Delete(LocalTree.KeptAsideFrom(root), recursive: true);
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

// The poll's work, for the folders a test has changed. The poll itself re-lists only the folders
// Explorer is showing, so a test that changed several names them — parents first, since a refresh
// of the parent is what settles the path a child is addressed by.
async Task Sync(params string[] directories)
{
    foreach (var directory in directories) await mirror.Refresh(directory);
}

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

    /// <summary>A slow portal, for tests about what happens while a listing is still in the air.</summary>
    public volatile int ListDelayMs;

    /// <summary>How many batched listings have been asked for, for tests about what goes in one request.</summary>
    public volatile int Batches;

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

    public async Task<IReadOnlyList<RemoteItem>> List(string? folderId)
    {
        if (ListDelayMs > 0) await Task.Delay(ListDelayMs);
        lock (_lock)
        {
            _log.Add($"list {folderId ?? "/"}");
            return _nodes.Values.Where(n => n.ParentId == folderId && !n.Trashed).Select(Item).ToList();
        }
    }

    /// <summary>The batch as the portal does it: the folders in turn, each answered in its own slot — and logged one by one, so the tests count listings the same way whichever route asked.</summary>
    public async Task<IReadOnlyList<FolderListing>> ListMany(IReadOnlyList<string?> folderIds)
    {
        Batches++;
        var answers = new List<FolderListing>();
        foreach (var folderId in folderIds)
        {
            try { answers.Add(new FolderListing(await List(folderId), null)); }
            catch (Exception e) { answers.Add(new FolderListing(null, e)); }
        }
        return answers;
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

    // The version as the portal spells it: the bucket's MD5 of the content, in hex. The engine
    // reads it to tell a row's own bytes from a stranger's, so the fake has to speak it too.
    static RemoteItem Item(Node node) =>
        new(node.Id, node.Name, node.IsFolder, node.Content.LongLength, Stamp, Stamp,
            node.IsFolder ? "" : Convert.ToHexString(System.Security.Cryptography.MD5.HashData(node.Content)).ToLowerInvariant());
}
