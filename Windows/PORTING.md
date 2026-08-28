# Porting the engine to Windows

What exists is a skeleton with a stub portal behind it: the Cloud Filter side is real and proven,
and everything that talks to Helmsley is not written yet. This is the note for whoever writes it.

## Where the line sits

`CloudFilter/` is done enough to build on. It registers a sync root, mirrors a listing as
placeholders, and answers `FETCH_DATA` by handing bytes back through `CfExecute` — verified end to
end in the dev VM, with Explorer showing dehydrated entries that hydrate on open.

`RemoteStore.cs` is the seam. It declares the two calls the engine currently makes — `List` and
`Fetch` — and `App/StubRemoteStore.cs` answers them from a hand-drawn tree so the filter could be
exercised before the portal existed. The port replaces that stub; it should not need to change the
interface to do it, though the interface will grow as writes arrive.

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

**5. `Mac/FileProvider/SnapshotStore.swift` → change tracking.** The portal has no change feed, so
"what changed" is a listing diffed against what that folder held last time, persisted so a cold start
still knows what was removed. On Windows this also fixes a live limitation: `Placeholders.Create`
currently fails on a second run, because it tries to create entries that already exist. Diffing is
what makes the mirror re-runnable — create what appeared, `CfUpdatePlaceholder` what changed version,
delete what went.

An item's version is its content hash, so a placeholder whose version still matches needs no work at
all.

**6. Writes.** `CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION` and the rename/delete notifications,
each mapping to the corresponding `HelmsleyAPI` call. Nothing here is written yet; `NativeMethods.txt`
will need the matching entries adding before the bindings exist.

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

**The OAuth redirect.** The Mac uses `helmsley-drive://oauth/callback`, which the portal already
allows. Windows can honour that by registering the scheme under
`HKCU\Software\Classes\helmsley-drive`, and that is the path that needs no server change. The
alternative most Windows apps take — a loopback listener on `http://127.0.0.1:<port>/` — is the
better fit for a desktop app that may not be installed, but it needs a new `redirect_uri` on the
portal's allowlist. **Ask Ben before touching the portal.** Its repository is a separate checkout at
`../Helmsley`.

**Push.** The Mac registers with APNs for `.fileProvider` pushes and drops its poll to 15 minutes.
None of that exists here. Polling only is the right first cut; whether Windows gets WNS at all is a
question for later, and `PushRegistrar.swift`/`PushTokenStore.swift` should be left unported until
somebody decides it.

**The provider GUID** in `SyncRoot.cs` is arbitrary but permanent — Windows keys the registration on
it. Do not regenerate it.

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

Running it registers `%USERPROFILE%\Helmsley Drive` as a sync root and mirrors the stub tree. It
holds the console until Ctrl+C, which over SSH means running it in the background and killing it
afterwards rather than waiting on it:

```
dotnet run --project App
dotnet run --project App -- --unregister
```

**The sync root is currently registered in the VM from the last test run.** Unregister before
changing placeholder logic, or the stale tree will confuse the results.

Anything long-running and elevated — installers above all — must go through a scheduled task
(`schtasks /RL HIGHEST`) rather than straight over SSH: Windows kills SSH child processes on
disconnect, and an unelevated installer fails with the un-obvious exit code 1602.

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
- Paths want `PCWSTR`; `fixed (char* p = s)` is the shortest way there.
- Target `net8.0-windows10.0.17763.0` or later, or every call raises CA1416.

Add new functions to `NativeMethods.txt` and the bindings appear on the next build.
