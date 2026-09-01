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
this process — so the window closes to a notification-area icon and only Quit, from that icon's
menu, stops the drive answering. The icon carries the mount state in its tooltip and opens the
window again on a click; a second launch of the app, which is what the Start Menu shortcut is once
the window is away, asks the running instance for its window rather than announcing that it is
already running. `--background` starts straight into the tray without showing the window at all,
which is what the installer's Startup shortcut passes.

Windows 11 puts a tray icon it has not seen before into the hidden-icon overflow behind the
chevron, and there is no supported way for an app to promote itself out of it. The first close
therefore raises a notification saying where the app has gone; dragging the icon out of the
overflow, once, is the user's own move to make.

`dotnet run --project Harness` is the engine driven against a fake portal on a scratch root it
registers and tears down — the real filter on the near side of the seam, which is where PORTING.md
says the surprises live. It prints one line per check and `ALL PASSED` or a count.

Four flags. `--background` is the only one meant for daily use — the login start above.
The other three are console-facing and for development. `--console` runs the old headless host (the right
shape over SSH, where a window has no desktop). `--unregister` removes the sync-root registration
and leaves the folder and its files alone. `--sign-out` is the whole sign-out the window's button
does: it clears the credential and the snapshots, unregisters, and deletes the mirrored tree —
keeping back anything the portal never took, at `<root> (not uploaded)`, and saying where.

One process per root, and every one of those flags takes that lock before it touches anything: the
window and `--console` on one root would fight over the sync root and the snapshot, and
`--sign-out` under a running drive would have that drive bin the portal's whole tree. The second
one launched says so instead.

