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
  Hydrator.cs           FETCH_DATA — an opened placeholder gets its bytes
  RemoteStore.cs        the slice of the portal the engine needs; HelmsleyAPI.swift's port goes here
  NativeMethods.txt     the cldapi surface; CsWin32 generates the bindings at build time
App/                    the windowed host — sign in, mount, and the engine in-process; tray app later
```

Identity carries over from the Mac side unchanged: an item is its portal row id, stamped on each
placeholder as the file-identity blob, which is what a hydration request hands back.

## Build and run

Needs the .NET 8 SDK on Windows 10 1709 or later (the dev VM has it). From `Windows/`:

```
dotnet build
dotnet run --project App
```

The app is a small status window like the Mac one — no console. It signs in through the browser if
it must, registers `%USERPROFILE%\Helmsley Drive` as a sync root, and mirrors the portal's tree for
as long as the window is open; there is no extension process on Windows, so closing the window is
what stops the drive answering. The engine's narration goes to
`%LOCALAPPDATA%\Helmsley Drive\app.log`, one log per run.

Three flags for development, all console-facing: `--console` runs the old headless host (the right
shape over SSH, where a window has no desktop), `--unregister` removes the sync-root registration,
and `--sign-out` clears the credential and the snapshots.
