# Porting the engine to Windows

What exists is a skeleton with a stub portal behind it: the Cloud Filter side is real and proven,
and everything that talks to Helmsley is not written yet. This is the note for whoever writes it.

## Where the line sits

`CloudFilter/` is done enough to build on. It registers a sync root, mirrors a listing as
placeholders, and answers `FETCH_DATA` by handing bytes back through `CfExecute` — verified end to
end in the dev VM, with Explorer showing dehydrated entries that hydrate on open.

`RemoteStore.cs` is the seam. It declares the two calls the engine currently makes — `List` and
`Fetch` — and `App/HelmsleyRemoteStore.cs` now answers them from the portal (the hand-drawn stub it
replaced is gone). The interface did not change to do it, and will grow as writes arrive.

All six steps below are ported (2026-08-28): `Configuration`, DPAPI `TokenStore`, `OAuth` with the
custom-scheme redirect — Ben's call, see below — the full `HelmsleyApi` surface behind
`HelmsleyRemoteStore`, snapshot-diffed change tracking (`Mirror` + `SnapshotStore`), and the local
write path (`LocalChanges` + the watcher in `Mirror`), each local event mapped onto the portal call
it means. The engine is exercised end to end by `Windows/Harness/` against an in-memory portal —
`dotnet run --project Harness` in the VM, ALL PASSED — which proves everything below the
`IRemoteStore` seam. What remains untested is the seam's far side against the live portal, and that
is gated on the interactive sign-in, which needs a browser on the VM's desktop: run the app there
once, sign in, and the mirror of the real tree is the proof.

## What to port, in order

The Mac sources are the specification. They are not pseudocode to translate line by line — the
concurrency model differs and so does the HTTP client — but every decision in them was made against
this portal and is worth keeping.

**1. `Mac/Shared/Configuration.swift` → a `Configuration` class.** Mostly a shrink. The portal
address (`https://helmsley-clients.co.uk`), the OAuth client id (`helmsley-drive`) and the scope
(`mcp`) carry over unchanged. Everything about keychain access groups and app groups does not: there
is no extension process on Windows, so the two-process sharing problem the Mac side solves simply
does not arise here.

**2. `Mac/Shared/TokenStore.swift` → DPAPI.** `System.Security.Cryptography.ProtectedData` with
`DataProtectionScope.CurrentUser`, written under `%LOCALAPPDATA%\Helmsley Drive\`. One process means
one reader, which is the whole of why this is smaller than its Mac counterpart.

**3. `Mac/Shared/OAuth.swift` → an `OAuth` class.** PKCE with S256, `/authorize` and `/token` at the
site root rather than under `/api`. The flow is the same; only the leg that catches the redirect
differs, and that is a decision — see below.

**4. `Mac/Shared/HelmsleyAPI.swift` → `HelmsleyRemoteStore : IRemoteStore`.** The big one, and the
reason C# was chosen: `HttpClient` plus `System.Text.Json` maps onto `URLSession` plus `Codable`
closely enough that this is mostly transcription. Everything hangs off `api/files`. Reads are
`list`, `item`, `trashed`, `whoami`; writes are `upload`, `replaceContents`, `delete`,
`createFolder`, `rename`, `move`, `trash`, `restore`.

Two things in there are load-bearing and easy to lose:

- **Bytes never go through the portal.** A download is answered with a `302` into Cloud Storage and
  uploads PUT to a signed URL. App Engine caps a request at 32MB against a 500MB file limit, so
  proxying is not a style choice that could be reversed later.
- **`Authorization` must be stripped on any cross-host redirect.** `RedirectSanitiser` exists
  because a client that replays headers across hops hands the portal's bearer token to Google.
  `HttpClient` follows redirects by default and will do exactly that. Turn `AllowAutoRedirect` off
  and follow the hop deliberately, or the token leaks on every download.

Keep `APIError`'s distinction between 404 and everything else. It is what tells the engine to remove
an item rather than retry.

**5. `Mac/FileProvider/SnapshotStore.swift` → change tracking.** Done — `SnapshotStore.cs` and the
pass in `Mirror.cs`. The portal has no change feed, so "what changed" is a listing diffed against
what that folder held last time, persisted so a cold start still knows what was removed. One
snapshot per folder, not the Mac's five: nothing here replays old sync anchors. An item's version
is its content hash, so a placeholder whose version still matches needs no work at all; a create
that finds the name taken refreshes the entry in place, which is also how a folder the portal had
only declared keeps resolving when it materialises under a real row id.

**6. Writes.** Done — `LocalChanges.cs` for the filter's side, the handlers in `Mirror.cs` for the
portal's. Rename and delete are held by the filter for an acknowledgement, so the portal call runs
inside the callback and its refusal refuses the local operation — Explorer shows the failure
instead of letting the trees drift. Every local delete maps to the bin, never the permanent
delete. What the filter will *not* say had to be learned the hard way and is written down in "What
the filter actually tells you" below; it is why a debounced `FileSystemWatcher` in `Mirror` is the
primary detector and the close notification only a helper.

## Identity, which must not drift

An item is its portal row id and nothing else — not its path, not its name. A move or a rename
leaves it alone. `Placeholders.cs` stamps that id on each placeholder as the file-identity blob, and
`Hydrator.cs` reads it straight back; that round trip is the whole identity scheme and it already
works. `Mac/Shared/ItemIdentity.swift` is worth reading in full anyway, for two cases it documents:

- The root has no usable id and is addressed by the empty path.
- A folder the portal has *declared* but never written — a client's Compliance, standing empty until
  something is filed in it — has no row, and the server mints it a `v<parent>_<type>` reference.
  Treat it as opaque. The one Explorer is holding must go on resolving after the folder materialises
  and the server stops minting it.

## Decisions that are not the porting agent's to make

**The OAuth redirect.** Decided (Ben, 2026-08-28): the custom scheme, `helmsley-drive://oauth/callback`,
which the portal already allows — no server change. `SignIn.cs` registers it under
`HKCU\Software\Classes\helmsley-drive` on each sign-in; the browser launches a second instance of
the app with the callback URL, which relays it over a named pipe to the instance that is waiting.
The alternative — a loopback listener on `http://127.0.0.1:<port>/` — would need a new
`redirect_uri` on the portal's allowlist. **Ask Ben before touching the portal.** Its repository is
a separate checkout at `../Helmsley`.

**Push.** The Mac registers with APNs for `.fileProvider` pushes and drops its poll to 15 minutes.
None of that exists here. Polling only is the right first cut; whether Windows gets WNS at all is a
question for later, and `PushRegistrar.swift`/`PushTokenStore.swift` should be left unported until
somebody decides it.

**The registration identity** is permanent — Windows keys the registration on it, and changing it
orphans what the shell already knows. It was once a provider GUID in `SyncRoot.cs`; since the move
to `StorageProviderSyncRootManager` it is the id string `HelmsleyDrive!<SID>!<path-hash>`, whose
path-hash leg is what lets a harness or probe root register beside the real one. Do not change the
`HelmsleyDrive` leg or the hashing; `Register` migrates a root carrying the old GUID-keyed
cldapi-only registration automatically.

## The dev VM

A Windows 11 ARM64 guest under VMware Fusion on Ben's Mac, hostname `Helmsley`. It has the .NET 8
and 9 SDKs, Visual Studio 2022 Community, and Git.

```
ssh helmsley-vm
```

That alias is in `~/.ssh/config` on the Mac and resolves to `ben@192.168.2.128` with the key at
`~/.ssh/helmsley-winvm`. Key auth as an administrator; the default shell is PowerShell, so remote
commands are PowerShell, not cmd. If the address has moved, the current lease is in
`/var/db/vmware/vmnet-dhcpd-vmnet8.leases` on the Mac. `vmrun` is not useful — the VM is encrypted
and every command wants its password.

The Mac checkout is the source of truth and the VM has a clone of it at
`C:\Users\Ben\Helmsley-Drive`, kept up to date by pushing over SSH:

```
git push vm main
```

The `vm` remote is `helmsley-vm:C:/Users/Ben/Helmsley-Drive`, and that clone is configured
`receive.denyCurrentBranch = updateInstead`, so a push updates its working tree in place — there is
no pull step and nothing to run in the VM afterwards. It refuses the push rather than clobbering
anything if the VM's tree has uncommitted changes, which is the right failure: edit on the Mac, and
let the VM be a build host that owns nothing.

It is the whole repository, not just `Windows/`, so `Mac/Shared/*.swift` can be read there too.

Then build:

```
ssh helmsley-vm 'cd C:\Users\Ben\Helmsley-Drive\Windows; dotnet build'
```

`HelmsleyDrive.sln` is committed, so nothing needs generating first.

Running it registers `%USERPROFILE%\Helmsley Drive` as a sync root and mirrors the tree. The app
is now a WPF window (`WinExe`, so no console ever opens); over SSH there is no desktop for that
window, so headless runs use `--console`, which holds the console until Ctrl+C — over SSH that
means running it in the background and killing it afterwards rather than waiting on it:

```
dotnet run --project App -- --console
dotnet run --project App -- --unregister
```

In windowed mode the engine's `Console.*` narration is redirected to
`%LOCALAPPDATA%\Helmsley Drive\app.log`, one log per run, which is where to look when Explorer
misbehaves and there is no console to have watched.

The stub-era sync root is unregistered and its tree set aside at
`%USERPROFILE%\Helmsley Drive.stub-tree` (delete it when done with it), so the first real-portal
run starts clean. In general: unregister before changing placeholder logic, or the stale tree will
confuse the results.

Anything long-running and elevated — installers above all — must go through a scheduled task
(`schtasks /RL HIGHEST`) rather than straight over SSH: Windows kills SSH child processes on
disconnect, and an unelevated installer fails with the un-obvious exit code 1602. The same trick
with `/it` instead runs a program on the logged-in desktop (session 1), which is how to see the
window from an SSH session that has no desktop of its own; pair it with a second `/it` task that
`CopyFromScreen`s to a PNG — after `SetProcessDPIAware()`, or the capture is a crop of the top-left
corner at the VM's 200% scaling.

WPF's hardware rendering draws a blank white client area on VMware's virtual GPU — the visual tree
is sound (UI Automation sees every control in place) and only the paint is missing, so it looks
exactly like a layout bug and is not one. The app forces
`RenderOptions.ProcessRenderMode = SoftwareOnly` for everyone: a status window this size has no
rendering load, and a real machine with a driver in the same mood would otherwise show the same
white sheet.

## What the filter actually tells you

Learned in the harness, each the hard way. The engine already copes with all of it; this is so the
next person believes the code.

- **Notifications are for placeholders only.** An ordinary file — which is what everything a user
  creates begins as — is written, renamed and deleted in silence. And an overwriting save
  (`CREATE_ALWAYS`, which is how `cmd`'s `>` and plenty of editors write) strips a placeholder back
  to an ordinary file, taking its identity with it. Hence the watcher, and hence the rule that
  bytes wearing a name the folder's snapshot already knows are a save over that row, never a new
  upload — minting a second row would fork the file.
- **`TRACK_ALL` clears in-sync for a rename as readily as for a write.** In-sync alone is not
  "locally modified"; treating it as one had the engine push stale bytes over the portal's newer
  version. The verdict that means an upload is `ModifiedDataSize > 0` *and* not in sync, read from
  `CF_PLACEHOLDER_STANDARD_INFO`; a flag cleared by mere metadata motion is quietly set right.
- **Notifications do not exempt the provider's own process — population does.** The mirror's
  renames and deletes come back through the same callbacks as the user's, and even a state query's
  handle close is reported; the process id on the callback is what tells the mirror's work from
  the user's. But FETCH_PLACEHOLDERS is never sent for the provider's own accesses: the engine's
  own enumeration of an unpopulated directory sees it raw and empty. Explorer is a foreign process
  so users never notice, and the harness has to "look" through a `cmd` child for the same reason —
  but nothing in the engine may assume its own `Directory.Enumerate` populates anything.
- **Population is on demand, root included.** The registration is `CF_POPULATION_POLICY_PARTIAL`:
  opening the drive costs nothing, a folder's entries are fetched (`Populator`) the first time a
  foreign process looks inside it, and that listing becomes the folder's snapshot — "materialised"
  — which is what admits it to the poll. The sync pass walks materialised folders only, so the
  portal answers for what is being watched, never for everything it holds.
- **Do not demand exclusivity to write.** The search indexer and the antimalware scan keep shared
  handles on anything fresh; `CF_OPEN_FILE_FLAG_WRITE_ACCESS` coexists with them where
  `CF_OPEN_FILE_FLAG_EXCLUSIVE` loses, and a short sharing-violation retry covers the rest.
- **`CfRegisterSyncRoot` gives you the filter, not the shell.** A root registered that way works —
  and mounts as a plain folder in the user's profile, with no entry in Explorer's navigation pane.
  `StorageProviderSyncRootManager.Register` is the same plumbing plus the shell: the sidebar
  entry, the status column, the registration under `SyncRootManager` in the registry.
- **The portal names things Windows cannot.** A real client's folder ends in an asterisk; another
  carries a colon. A placeholder create for either fails as a silent gap in the tree, so every
  name from the portal passes through `LocalNames` before it becomes a filename — and a pass
  recreates any entry the snapshot believes in that the disk does not hold, which is also what
  heals a folder stuck populated-but-empty by an enumeration nothing answered.

## What CsWin32 actually generates

Worth knowing before writing against the API from memory, because the generated shape is not the C
one and the differences cost an afternoon:

- Pointer parameters mostly surface as `in`/`ref`/`out`. `CfRegisterSyncRoot` takes `in`, and
  `CfExecute` takes `(in CF_OPERATION_INFO, ref CF_OPERATION_PARAMETERS)`.
- `LARGE_INTEGER` fields are plain `long`. There is no `.QuadPart`.
- The callback table takes **delegates**, not function pointers, so `[UnmanagedCallersOnly]` will not
  compile there. The delegates must be kept alive for as long as the root is connected — `SyncRoot`
  holds a static reference for exactly that reason, and dropping it is a use-after-free the GC will
  hand you at the worst moment.
- `CF_CONNECTION_KEY` is internal to the generated assembly, which is why `SyncConnection` wraps it.
- `CfOpenFileWithOplock` hands its protected handle out as a plain `SafeFileHandle`, whose disposal
  runs `CloseHandle` — a silent no-op on the structure a protected handle actually is, leaking the
  real handle inside and leaving the file locked until the process dies. Only `CfCloseHandle`
  closes one; `Placeholders.ProtectedHandle` does exactly that and tells the `SafeFileHandle` it
  already happened. This was an afternoon.
- Paths want `PCWSTR`; `fixed (char* p = s)` is the shortest way there.
- Target `net8.0-windows10.0.17763.0` or later, or every call raises CA1416.

Add new functions to `NativeMethods.txt` and the bindings appear on the next build.
