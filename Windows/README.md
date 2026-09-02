# Helmsley Drive for Windows

The same idea as the Mac volume — the portal's tree, nothing copied until it is opened — built on
the Cloud Filter API, the machinery behind OneDrive's files-on-demand. What `NSFileProviderReplicatedExtension`
is to Finder, a registered sync root plus a connected callback table is to Explorer, with one
structural difference: there is no extension process. The engine lives in the app, and Explorer's
opens reach it through the filter driver while it runs.

```
CloudFilter/            the engine, as a library
  SyncRoot.cs           register/connect: what makes the folder cloud-backed, and whose callbacks fire
  Placeholders.cs       remote listings become dehydrated directory entries
  Populator.cs          FETCH_PLACEHOLDERS — a folder first looked inside gets its entries
  Hydrator.cs           FETCH_DATA — an opened placeholder gets its bytes, streamed in chunks
  LocalChanges.cs       the filter's closes, renames and deletes, held for an answer
  Callbacks.cs          the boundary all four sit behind: nothing thrown may reach native frames
  Mirror.cs             both directions — the sync pass down, the local writes up
  SnapshotStore.cs      what each folder held last time, which is what "changed" is diffed against
  LocalNames.cs         portal names Windows will not hold, and will not treat as paths
  LocalTree.cs          taking the tree away on sign-out, and what must not go with it
  RemoteStore.cs        the slice of the portal the engine needs, answered by App/HelmsleyRemoteStore.cs
  NativeMethods.txt     the cldapi surface; CsWin32 generates the bindings at build time
App/                    the windowed host — sign in, mount, and the engine in-process; tray app later
  HelmsleyApi.cs        HelmsleyAPI.swift's port: every call to /api/files
  OAuth.cs, SignIn.cs   PKCE, the custom-scheme redirect, and the token that comes back
Harness/                the engine against an in-memory portal, on a scratch sync root
```

Identity carries over from the Mac side unchanged: an item is its portal row id, stamped on each
placeholder as the file-identity blob, which is what a hydration request hands back.

## Build and run

Needs the .NET 8 SDK on Windows 10 1809 or later — the Cloud Filter API arrived in 1709, but the
projects target `net8.0-windows10.0.17763.0` so the calls do not all raise CA1416 (the dev VM has
it). From `Windows/`:

```
dotnet build
dotnet run --project App
dotnet run --project Harness
```

The app is a small status window like the Mac one — no console. With a credential stored it mounts
on its own; without one it waits on its Sign In button, which opens the portal's pages in a sign-in
sheet the app owns and closes itself on the redirect (`SignInWindow.cs`; the default browser is the
fallback, and what `--console` uses). Either way
it registers `%USERPROFILE%\Helmsley Drive` as a sync root and mirrors the portal's tree for as long
as the app is running. The engine's narration goes to `%LOCALAPPDATA%\Helmsley Drive\app.log`, one
log per run.

Closing the window does not stop it. There is no extension process on Windows — the engine is in
this process — so the window closes to a notification-area icon, and Quit, from that icon's menu,
is the one thing that takes the drive down. It takes all of it: the registration goes and the
mirrored tree with it, because a sync root nobody answers for is an entry in Explorer that opens
nothing. The credential stays, so the next launch mounts again without asking. A session the portal
has ended does the same thing on its own. Logging off or restarting is the exception — Windows
allows about five seconds there, which is not enough to remove a tree safely, so the registration
outlives the session and the Startup shortcut reconciles it at the next login.

The icon carries the mount state in its tooltip and opens the window again on a click; a second
launch of the app, which is what the Start Menu shortcut is once the window is away, asks the
running instance for its window rather than announcing that it is already running. `--background`
starts straight into the tray without showing the window at all, which is what the installer's
Startup shortcut passes.

Windows 11 puts a tray icon it has not seen before into the hidden-icon overflow behind the
chevron, and there is no supported way for an app to promote itself out of it. The first close
therefore raises a notification saying where the app has gone; dragging the icon out of the
overflow, once, is the user's own move to make.

`dotnet run --project Harness` is the engine driven against a fake portal on a scratch root it
registers and tears down — the real filter on the near side of the seam, which is where PORTING.md
says the surprises live. It prints one line per check and `ALL PASSED` or a count.

Four flags. `--background` is the only one meant for daily use — the login start above.
The other three are console-facing and for development. `--console` runs the old headless host (the
right shape over SSH, where a window has no desktop); Ctrl+C unmounts it exactly as Quit does, so a
headless run leaves nothing standing behind it. `--unregister` removes the sync-root registration
and nothing else — still the way to clear a root this process did not mount, one a killed run left
standing or one from an older build. The folder and its subfolders stay; the file placeholders
under them do not, because Windows drops the entries it can no longer fetch for, and on a tree
nobody has opened that is every file in it. Nothing is lost that the portal does not still hold —
but it is emptier afterwards than "removes the registration" sounds. `--sign-out` is the whole
sign-out the window's button does: it clears the credential and the snapshots, unregisters, and
deletes the mirrored tree — keeping back anything the portal never took, at `<root> (not
uploaded)`, and saying where.

One process per root, and every one of those flags takes that lock before it touches anything: the
window and `--console` on one root would fight over the sync root and the snapshot, and
`--sign-out` under a running drive would have that drive bin the portal's whole tree. The second
one launched says so instead.

## Packaging for testers

`Packaging/package.ps1` takes a checkout to an installer:

```
.\Packaging\package.ps1 -Version 0.4.1 -Runtime win-x64, win-arm64
```

It publishes through `App/Properties/PublishProfiles/`, then compiles `Packaging/HelmsleyDrive.iss`
with Inno Setup 6.3 or later, leaving `dist\HelmsleyDrive-<version>-<arch>.exe`. Pass
`-CertThumbprint` to sign the executable and the installer with a certificate from the current
user's store; pass `-SkipInstaller` to publish and stop.

The published app is self-contained and single-file — around 260 MB, because a tester who has to
find and install the .NET 8 *Desktop* Runtime before anything starts is a tester who writes to ask
why nothing started. Beside it goes `HelmsleyDrive.ico`, which the sync-root registration hands the
shell as a path; it is excluded from the bundle for exactly that reason.

The install is per user throughout, and needs no administrator: into `%LOCALAPPDATA%\Programs`,
with the URI scheme in HKCU and a Startup shortcut that passes `--background`. That shortcut is not
a nicety — the drive answers Explorer only while the process runs, so without it the first reboot
leaves a registered root of placeholders that never hydrate. Uninstalling runs `--unregister` before the files go, and
leaves both `%USERPROFILE%\Helmsley Drive` and the log
directory in place — with the caveat above about what unregistering takes out of that folder.

### Making one, from the Mac

The toolchain is Windows-only, so the build happens on the dev VM (PORTING.md has the reach-it
notes) and the Mac stays the source of truth. It needs the .NET 8 SDK and Inno Setup 6.3 or later
on the VM; `package.ps1` looks for `ISCC.exe` on the PATH and in the three places Inno Setup's own
installer puts itself, per-user included.

Quit any Helmsley Drive running on the VM's desktop first. It holds its own `bin\`, and a publish
into it fails with MSB3021 — which reads as a broken build and is not one.

```
git push vm main
```

Then, in an SSH session held open for the duration — Windows kills SSH child processes on
disconnect, and two self-contained publishes are minutes, not seconds:

```
ssh helmsley-vm 'cd C:\Users\Ben\Helmsley-Drive\Windows; dotnet build'
ssh helmsley-vm 'cd C:\Users\Ben\Helmsley-Drive\Windows; dotnet run --project Harness'
```

Build and harness first, always: the harness is the only thing that drives the real filter, and a
packaged installer is an expensive way to discover the engine is broken. Then the packaging, which
needs two things said exactly right:

```
ssh helmsley-vm 'powershell -NoProfile -ExecutionPolicy Bypass -Command "& C:\Users\Ben\Helmsley-Drive\Windows\Packaging\package.ps1 -Version 0.4.1 -Runtime win-x64,win-arm64"'
```

The VM refuses to run an unsigned `.ps1` — calling the script directly fails with "running scripts
is disabled on this system". `-ExecutionPolicy Bypass` on the one invocation is the answer; it
lasts for that process and changes nothing about the machine, which is the point. And it has to be
`-Command`, not `-File`: under `-File` the runtime list arrives as the single string
`win-x64,win-arm64` and `ValidateSet` rejects it against a set it visibly belongs to, which is a
confusing way to spend ten minutes.

Both installers land in `dist\` at the repo root — gitignored, because a tester gets one from a
release and never from a checkout — and come back with:

```
scp 'helmsley-vm:C:/Users/Ben/Helmsley-Drive/dist/HelmsleyDrive-0.4.1-*.exe' Windows/dist/
```

Worth a `shasum -a 256` against the VM's `Get-FileHash` afterwards; a truncated 260 MB copy looks
exactly like a whole one in a directory listing.

Give every build handed to anybody a distinct `-Version`. Three different 0.1.0s in the wild make a
bug report against "0.1.0" a bug report about nothing. Nothing in the repository pins the number —
it is stamped into the executable, the uninstall entry and the filename from that one argument — so
bumping it is the argument and the release note, not an edit.

Two things the installer cannot do for you. It is unsigned unless you pass a certificate, so every
tester meets SmartScreen once and has to choose *More info* → *Run anyway*. And `longPathAware` in
the manifest only takes effect where the machine-wide `LongPathsEnabled` policy is on, which is an
administrator's setting on a machine this installer deliberately never elevates on — worth checking
against a real client tree before deciding it does not matter.
