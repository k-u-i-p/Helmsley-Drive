# Helmsley Drive

The Helmsley client portal's file tree, mounted as a volume — in Finder on macOS, and under
Locations in the Files app on iPhone and iPad.

Not a sync folder: nothing is copied to the device until it is opened, and the structure shown is
the portal's own file explorer — the same tree, the same folders, the same rows. A file dropped in
Finder is a file the dashboard shows a moment later, in the folder it was dropped on.

```
Helmsley Documents/
├── News/
├── Shared/                        ← the office's, every admin's to change
├── Staff/
│   └── Ben Allright/              ← yours alone; nobody else may write in it
├── Properties/
│   └── Active/
│       └── Arabesque Syndicate/
│           ├── Brochure/  Valuation/  Annual Report/  News/  Deeds & Legals/
├── Clients/
│   └── Active/
│       └── A J Bell Platinum SSAS/
│           ├── Compliance/  Deeds & Legals/  Remittance/
├── Loans/
└── Orphaned/                      ← read-only: what the nightly sweep could not place
```

Most of those leaf folders are *declared* rather than written: the portal knows every client has a
Compliance folder without writing one until something is filed in it. They list, walk and take a drop
exactly like a folder that exists, because the first drop is what makes them exist.

## How it fits together

Two halves, in two repositories.

**`../Helmsley` — the portal.** Gained a file-provider API and a way for a native app to
authenticate. See *Changes to the portal* below.

**This repository — the apps.** One Xcode project, four targets, two platforms:

| Target | Platform | What it is |
| --- | --- | --- |
| `HelmsleyDrive` | macOS | the container app: signs in, registers the domain, nothing else |
| `HelmsleyFileProvider` | macOS | an `NSFileProviderReplicatedExtension` — everything Finder talks to |
| `HelmsleyDrive-iOS` | iOS | the same two jobs, in an iOS shape |
| `HelmsleyFileProvider-iOS` | iOS | the same extension, for the Files app |

An app and its extension are separate processes, sharing two things: the OAuth token set (keychain
access group), and the enumeration snapshots the change diff is computed against (app group
container).

The two platforms share everything but their UI. `FileProvider/` — the whole engine — is compiled
into both extensions unchanged; the platform difference amounts to two `#if os(macOS)` blocks.

```
Shared/                 compiled into all four targets
  Configuration.swift   identifiers, portal address, and the keychain group lookup
  OAuth.swift           PKCE flow, token exchange/refresh, and the actor that hands out a live token
  TokenStore.swift      the token set, in the shared keychain
  HelmsleyAPI.swift     every call to /api/files
  ItemIdentity.swift    what an NSFileProviderItemIdentifier means here
  PushTokenStore.swift  the APNs token the extension registered, where the app can withdraw it
AppShared/              the two container apps, not the extensions
  AppModel.swift        sign in, mount, unmount — the state behind both UIs
  SignIn.swift          the OAuth sheet, anchored to an NSWindow or a UIWindow
FileProvider/           both extensions: items, enumerators, and the extension class itself
HelmsleyDrive/          macOS UI          HelmsleyDrive-iOS/    iOS UI
                                          FileProvider-iOS/     iOS extension plist/entitlements
Tools/generate-xcodeproj.py   regenerates HelmsleyDrive.xcodeproj (see below)
Tools/generate-icon.py        builds the app icons from the portal's H mark
Tools/flatten-png.swift       strips alpha, which App Store icons may not have
```

`AppShared/` is separate from `Shared/` for a reason the compiler enforces: `UIApplication.shared`,
which presents the sign-in sheet, is barred outright inside an iOS app extension. An extension never
signs in interactively, so that code has no business being compiled into one.

### Identity

The portal keeps its files in one table, and that table is a tree: a folder is a row, a file is a
row, and a row's `parent_id` is where it sits. So an item's identity here is its row id and nothing
else — not where it is, which changes the moment somebody drags it, and not what it is called.

That is a change. The volume used to serve two tables through one mount — `documents`, a set of
views where a row had no path at all, and `admin_files`, a real filesystem for staff — and carried
two shapes of identifier to match. One tree means one shape, and it means every write the volume can
make is available everywhere the portal allows it rather than in one branch.

A file's version is its content hash, which is also its key in the storage bucket. It changes when
and only when the bytes do, so a downloaded copy stays valid until the file is genuinely replaced.

### Where the bytes go

Never through the portal. A download is answered with a `302` into Cloud Storage and an upload PUTs
to a signed URL, so both transfers are between the device and the bucket — App Engine only ever
carries the redirect, the upload ticket and the finalise. That is not a nicety: App Engine caps a
request at 32MB against a 500MB file limit, so proxying could not work.

`Transport` (in `HelmsleyAPI.swift`) builds those transfers as `URLSessionTask`s rather than using
the async conveniences, because the framework wants the task's `Progress`: it is the percentage the
user watches, and the system cancels a fetch that stalls and expects the extension to stop promptly.
Attaching the task's own progress as a child of the one handed back gives both — byte-accurate
progress, and cancellation that reaches the transfer.

`RedirectSanitiser` strips `Authorization` on any cross-host hop. `URLSession` replays headers across
redirects by default, which would hand the portal's bearer token to Google; the signed URL is the
only credential the bucket wants. It logs each time it fires, because otherwise there is no way to
tell a stripped redirect from one that quietly carried the token on.

### Keeping up to date

The portal has no change feed — a row records when it was written and nothing else — so "what
changed" is computed in the extension, by listing a folder and diffing it against what that folder
held last time (`FileProvider/SnapshotStore.swift`, persisted so a cold start still knows what has
been removed).

Nothing asks on its own. This is a replicated extension, so the system owns the copy: a folder is
enumerated once and thereafter the system asks only for *changes*, and only when this extension says
there are some. Finder's refresh does not provoke a listing and navigating back into a folder does
not either — that is the old non-replicated behaviour, and assuming it here is what left a file
filed in the dashboard invisible until the volume was removed and re-added.

So something has to say so, and there is exactly one way to say it.

### Everything goes through the working set

`NSFileProviderManager.h`, on `signalEnumeratorForContainerItemIdentifier`:

> When using NSFileProviderReplicatedExtension, only call this method with
> NSFileProviderWorkingSetContainerItemIdentifier. **Other container identifiers are ignored.** The
> system will automatically propagate working set changes to the UI, without explicitly signaling the
> containers currently being viewed in the UI.

This extension named the folder that had changed for a long time, which is what a non-replicated
provider does and what every article about them describes. Every one of those signals was accepted
and discarded: the folder's own enumerator was never asked for changes, and a file filed in the
dashboard sat there unseen no matter how often the extension said otherwise — including after a
Finder write, and including on every poll round. It looked like a portal problem and was never one.

So the working set is signalled, whatever moved, and the working set's `enumerateChanges` is where
the change actually gets reported: it answers with the bin — which is what it holds — plus the
item-level diff of every folder somebody currently has open (`ChangePoller.pendingChanges()`). Each
item carries its own `parentItemIdentifier`, so the system files it back where it belongs, applies it
to its copy of that folder, and Finder redraws. A folder's own enumerator is left to do what it is
actually asked to do: list itself the first time somebody opens it.

That is also why the working set can stay small. It is not being asked to *hold* the tree — it is the
channel changes are reported on, and what goes down that channel is bounded by what somebody is
looking at.

### The push

`FileProvider/PushRegistrar.swift` registers with PushKit for the `.fileProvider` push type and
sends the device token to `/api/files/push-token`. Registration happens in the *extension*, not in
the container app, though Apple allows either: the extension is loaded whenever anything touches the
volume, while the app might not be opened for months — and a token that changed in the meantime
would leave the volume unreachable until somebody thought to open it.

What arrives is less than it looks like. A file provider push is not delivered to any code here:
`pushRegistry(_:didReceiveIncomingPushWith:for:)` is never called for this type. The system takes
the notification, reads the domain out of the payload, and signals that domain's **working set**
itself — and for a replicated extension it ignores any other container the payload names. So the
whole message is "look again". The portal could say which folder moved, now that a row has exactly
one, and it would make no difference: there is one field for it in the payload and the system
discards what it holds.

A push therefore lands as a working set change enumeration and nothing else — the system signals it
on the extension's behalf — and `BinEnumerator` answers it with whatever the open folders have to
say. No code of this app's ever sees the notification.

`FileProvider/ChangePoller.swift` stays, at 15 minutes rather than 30 seconds. A push is a datagram
with no delivery anybody can check — the portal may have no APNs key, the registration may have
failed, the machine may have been asleep — so the timer is what notices that one never came. It
compares each watched folder's signature against what was last reported and signals the working set
when they differ; the interval switches on what registering answered, and drops back to 30 seconds
if push is not established. A folder nobody is looking at costs nothing, and the tree at large is
never walked.

One thing the timer covers that push cannot: a folder that changed while nobody had it open. There
was no enumerator to ask about when the push arrived, and the system does not re-list a folder on
being navigated back into — so a folder that starts being watched is checked once, three seconds in.
Three seconds rather than at once because the first enumeration of a folder lands a moment after the
enumerator is made, and it is the one that records what the folder holds; asking before it would
compare a listing against nothing and read as a change.

Detection costs one listing and the reporting enumeration costs a second, which is the price of a
portal that cannot say what changed — and it is paid only when something has.

### Sync anchors

Sync anchors are checked rather than assumed. `enumerateChanges` diffs against the listing the
incoming anchor was actually issued for, which is why `SnapshotStore` keeps the last few rather than
only the newest: the system samples `currentSyncAnchor` on its own schedule and can come back asking
from a point this process has moved past. An anchor no longer held is answered with
`.syncAnchorExpired`, which costs one full re-listing. The alternative — diffing against the newest
listing whatever was asked for — reports no changes and has the system record itself as up to date
having never been told what it missed, which is a folder that stays wrong until the next remount.

What the working set *lists* is the bin and nothing else — as distinct from what it *reports*, which
is every change above. Filling its listing with the tree would mean enumerating every file of
every client of every syndicate on a schedule nobody asked for; folders enumerate on demand instead.
The bin is the exception that argument never covered — it is bounded by what one admin threw away,
and the framework asks for trashed items there by name. What it buys is Spotlight: the bin is
indexed, and the system stops learning about it only when someone opens the trash.

## What the volume can and cannot do

Identical on both platforms — it is the same extension.

| | |
| --- | --- |
| Browse | anywhere the signed-in admin may read |
| Open, preview, Quick Look, copy out | yes — downloaded on first open, evictable afterwards |
| Drop a file in | anywhere the portal calls writable, by the ticket → bucket → finalise path |
| New folder | the same places |
| Rename | wherever the portal allows it — including the folders it placed itself, since a folder's identity is its type and a renamed `Shared` is still the shared folder |
| Move, delete, trash | anywhere but the folders the tree itself placed, which move only when the thing they belong to changes state |
| Save changes back | **no** — refused with an explanation |

Saving changes back is the one refusal that is the same everywhere: nothing replaces a file's bytes
under the same row. A row's bytes are stored under a key derived from its content hash, so different
bytes are a different row — offering `.allowsWriting` would mean accepting a save in Finder that
quietly never reached the server.

Everything else in that table is a *server* answer, not a rule this app holds. Each listing and each
by-id lookup carries a `permissions` block — `writable`, `renamable`, `movable`, `deletable` — that
the portal computes from the row's whole ancestor chain, and `FileProviderItem` turns straight into
`NSFileProviderItemCapabilities`. So Finder greys out what the portal would refuse *before* the user
commits to it, and the rules stay in the one place that knows them:

- an admin's own folder under `Staff/` is theirs; a super admin may read it and still not write in it
- `Orphaned/` takes nothing from anybody — it is a record of what the nightly sweep could not place,
  and a folder that can be filed into stops being one
- a folder the tree placed — `Shared`, `Clients`, a client's own folder, a declared `Compliance` —
  can be renamed but never moved or thrown away
- a declared folder that has no row yet can be filed into (that is what makes it real) and nothing
  else, since there is nothing there to rename or delete

The one place this app is stricter than the portal is the mount point: the server would take a new
folder at the top of the tree, and the volume does not offer it. The top level is the skeleton —
`Clients`, `Properties`, `Loans`, `Shared`, `Staff`, `Orphaned` — and something dragged in beside
them would be none of it.

### Item identifiers are opaque, and one shape

An identifier is `N.` and the row id, and nothing in this app parses what follows. The id is a
serial for a row and a `v<parent>_<type>` reference for a folder the portal has declared but not yet
written; both are strings the server answers to, and treating either as a number would lose half of
what the tree can name.

A path is deliberately *not* part of it. Rows move — dragged between folders, thrown away and put
back — and if where a thing sat were part of what identified it, every one of those would change
what the system thinks the item *is*. Where an item sits is answered by `/api/files/items/:id`
instead, which is the same lookup that says whether it is in the bin.

The mount point is the exception, and has to be: the framework fixes its identifier as
`.rootContainer`, so the tree's own root row has no say in it. The volume addresses it by the empty
path, and an item sitting directly under it reports **no** parent rather than the root row's id —
two names for one folder would be two folders. `/api/files/items/:id` answers 404 for that row for
the same reason, and because it has no name of its own to answer with: an item with an empty
filename is not an error the file provider framework reports but one it aborts on, so nothing may
ever be in a position to hand it one.

**Identifiers minted before the two trees became one are not decoded at all.** They carried `D.`,
`F.` and `P.` prefixes over ids in `documents` and `admin_files`, and those ids mean something else
in the table this volume now reads — a small serial exists there too, belonging to another file
entirely. Resolving one would hand back the wrong file's bytes under a name somebody trusted. So the
prefix is new and the old ones fail: the system drops what it was holding and enumerates the volume
again from the root, once.

### The trash

One bin over the whole tree, as the volume has one Trash. `deleted_at` on `files` is the whole
mechanism, and it is a flag rather than a move: the row keeps the parent it always had, so putting it
back is clearing the flag, and a folder comes back with its subtree intact. Only the top of what was
thrown away is marked — everything under it is already unreachable, because walking requires every
hop to be live — which is also why the bin lists the folder rather than each file that went along
inside it.

The namespace index is partial on the flag, so a name in the bin is not a name in use. The cost is
that putting something back can collide, which is settled by numbering, the way a move already is.

A folder in the bin can be opened, because Finder will try and because what is inside it is still
there. That is the one read allowed to name something thrown away — `locateQuery` asks for it and
`locate` does not, so no write can — and a listing of one comes back marked, which is what tells the
extension that everything in it is to be read or purged and nothing else. Rename and refile the
portal refuses under a trashed folder; a restore is worse than refused, since the mark it would
clear is on the folder above, so `.allowsReparenting` stops at the top of what was thrown away.
Permission is what authorises any of it, and it is unchanged by being in the bin: each candidate's
own ancestor chain is asked, so the bin is a second way into rows an admin could already reach and
never into anyone else's.

Trashing keeps the bytes. Only a purge removes the row, and the objects behind it are left to the
nightly sweep, which finds orphans from the bucket end rather than from a delete that noticed.

The framework has no separate verb for any of this: an item is trashed by being reparented into
`.trashContainer` and restored by being reparented out, and `isTrashed` is iOS-only, so hanging
under that container is the entire signal.

Finder's **Put Back** is not offered on any of it, and nothing here can make it be. Nothing in
`NSFileProviderItem` carries where an item came from — the header is explicit that "the parents of
trashed items and of the root item are ignored" — so there is no origin to report even in principle.
Finder does not need one reported: it writes the origin itself, as `ptbL`/`ptbN` records in the
domain's own `.Trash/.DS_Store`, correctly and for a folder as readily as for a file, and then does
not read them back for a file-provider trash. That was measured rather than assumed — the records
were decoded off disk while the menu stayed empty — so a future attempt wants a macOS release note
behind it, not another pass at the extension. Restoring is undo or a drag out of the bin; both arrive
as an ordinary reparent, and both work. `mv(1)` does not, on a folder: a binned directory has no `w`
bit, because the bit is `.allowsAddingSubItems` and nothing may be dropped into something thrown
away. Finder's move goes through the daemon and is authorised against `.allowsReparenting` instead.

## Changes to the portal

All in `../Helmsley`:

| File | Change |
| --- | --- |
| `backend/routes/fileProvider.js` | the `/api/files` API this app talks to — one router over the `files` tree, bearer-authenticated |
| `backend/utils/http/bearerAdmin.js` | **new** — authenticates a request by OAuth access token instead of by session cookie |
| `backend/utils/domain/files/fileTree.js` | the tree itself: the skeleton, the walk, declared folders, and `permissionsFor()` — the whole of who may do what, pure and read off the ancestor chain the walk already loaded |
| `backend/utils/domain/files/fileReads.js` | listing a folder's files, one row by id, and the bin. `listTrash` carries each row's permissions out with it, since the chain they were read from is gone by the time the volume asks |
| `backend/utils/domain/files/fileWrites.js` | everything that changes it: new folder, rename, move, trash, restore, purge, finalise — each re-reading permission rather than trusting a caller |
| `backend/utils/domain/documents/stagedUpload.js` | the bucket half of an upload |
| `backend/routes/admin/files.js` | the dashboard explorer's reading half, over the same tree — so the two surfaces cannot come to disagree about it |
| `backend/routes/admin/mcp/oauthProvider.js` | resolves additional statically registered OAuth clients |
| `backend/utils/http/urls.js` | CSP `form-action` covers every registered client's callback, private-use schemes by scheme alone |
| `backend/config.js` | `mcp.clients[]` — the schema for those, and `apns` — the push signing key |
| `backend/server.js` | mounts `/api/files` with its own rate-limit bucket, and resolves the tree's skeleton at boot |
| `config.json` | registers the `helmsley-drive` client, and the APNs key |
| `backend/utils/integrations/apns.js` | **new** — the APNs sender: an ES256 JWT and one HTTP/2 POST per device, no dependency |
| `backend/utils/domain/fileProvider/pushDevices.js` | **new** — the registered devices, keyed by token |
| `backend/utils/domain/fileProvider/changeSignal.js` | `signalPersonalFiles()` / `signalSharedFiles()`, coalesced and fanned out. Which one is a question about the row's chain — under an admin's own folder it is theirs alone, everywhere else it is everybody's |
| `backend/scripts/init-db.js` | `file_provider_devices` |
| everything that writes a row | signals afterwards, through `fileWrites.js`'s own `signalChain()` — and so does the nightly sweep that files what the tree could not place |

The move onto one tree took three more changes, all so a filesystem could be built on it:

| File | Change |
| --- | --- |
| `fileProvider.js` | every listing and by-id lookup carries a `permissions` block — `writable`, `renamable`, `movable`, `deletable`. Without it the volume has to guess what to offer, and a guess is a dialog after the user has committed rather than a greyed-out menu item before |
| `fileProvider.js` | a row directly under the tree's root reports **no** parent, rather than the root row's id. The volume addresses the root as its mount point, and an item that named the row instead would hang off an identifier no listing ever vends |
| `fileTree.js` | a declared folder is not `renamable`. Every write on one is refused — there is no row to change, and the name comes from the declaration — but the other two were already false for a slug, so this was the one that had to be said |

### Why OAuth rather than the session cookie

A mounted volume cannot re-do SMS two-factor every twenty-four hours when the session cookie lapses.
The portal already runs a full OAuth 2.1 server for the MCP connector — PKCE, rotating refresh
tokens, revocation, per-admin audit — and those tokens were never protocol-specific: one says "this
client speaks for this admin". So the app presents one of those rather than being given a parallel
credential to leak, expire and revoke separately. Signing in still goes through the portal's own
login page, SMS code and all; it just happens once.

`bearerAdmin` deliberately does not touch `req.session`: `saveUninitialized` is `false`, so writing
a role onto it would persist a session row per request — thousands a day from a mounted drive, none
of which any cookie would ever present again.

### Config

`config.json` needs the client registered (already added):

```json
"mcp": {
  "clientId": "helmsley-mcp",
  "redirectUris": ["https://claude.ai/api/mcp/auth_callback", "https://claude.com/api/mcp/auth_callback"],
  "issuerUrl": "https://helmsley-clients.co.uk",
  "clients": [
    { "clientId": "helmsley-drive", "redirectUris": ["helmsley-drive://oauth/callback"] }
  ]
}
```

No secret: the app ships to laptops, so PKCE is what binds an authorization code to the process that
asked for it. **The same entry has to exist in whatever `config.json` is deployed**, or sign-in
fails at the authorize step with an unknown-client error.

### Turning push on

Three things, and the volume works without any of them — it falls back to asking every 30 seconds,
which is what it did before push existed.

**1. An APNs auth key.** In the developer portal, Certificates, Identifiers & Profiles → Keys → **+**,
enable *Apple Push Notifications service (APNs)*, download the `.p8`. It can be downloaded once, and
one key signs for every app of the team, so this is not something to do twice. Note the Key ID beside
it and the Team ID (`CR2F6D8AF7`).

**2. Push Notifications on both App IDs.** `uk.co.helmsley.HelmsleyDrive` and
`uk.co.helmsley.HelmsleyDrive.FileProvider` — the entitlement is in all four targets' `.entitlements`
files, and `-allowProvisioningUpdates` enables the capability on the App IDs on the next build.

**3. The key in `config.json`:**

```json
"apns": {
  "keyId": "ABCD123456",
  "teamId": "CR2F6D8AF7",
  "key": "-----BEGIN PRIVATE KEY-----\n…\n-----END PRIVATE KEY-----\n",
  "topic": "uk.co.helmsley.HelmsleyDrive.pushkit.fileprovider",
  "environment": "production"
}
```

The topic is the **app's** bundle identifier with `.pushkit.fileprovider` appended — not the
extension's, though the extension is what registers. `key` is the `.p8` file's contents; `keyFile` is
a path to it instead, which suits a local checkout and not a deploy, since App Engine ships nothing
outside the repo. Omit the whole block and no push is ever sent: `/push-token` still accepts
registrations and answers `push: false`, which is what tells the app to keep its short timer.

`environment` is only the first host to try. A device says which it was signed for when it registers,
and `apns.js` corrects the record from what APNs answers — so a TestFlight install and a locally
built one work side by side without anything being told which is which.

Nothing here is worth alerting on when it fails. A push that does not arrive costs a folder its
freshness for up to fifteen minutes and nothing else, so a refusal is logged (`APNs refused device …`)
and the send moves on.

Two refusals and only two delete a registration: `410 Unregistered`, which is the app having been
uninstalled, and `BadDeviceToken` from both hosts, which is a token no build of ours could have
minted. Both say so in the log as well (`APNs has no such device …`). Everything else is a
misconfiguration to read and correct — `DeviceTokenNotForTopic` above all, which is APNs saying the
topic does not name the app the token was minted for. That answer is the same from both hosts and
the same for every device, so treating it as a dead token would have one mistyped topic empty the
registry on the first file filed after a deploy. `npm run push-test` in `../Helmsley` sends one
by hand and spells out what each refusal means.

Which is why there is a way to ask on purpose. From the portal checkout:

```bash
npm run push-test
```

It sends the real thing to every registered device and prints what APNs said, with the fix beside
each refusal. A push is outbound only, so this works from a laptop with no public address — and
since a locally run portal reads the same database the app registered against, the whole path can be
exercised without deploying anything.

## Building

Requires Xcode and a signing team. The project is set to team `CR2F6D8AF7`.

```bash
xcodebuild -project HelmsleyDrive.xcodeproj -scheme HelmsleyDrive -configuration Debug -allowProvisioningUpdates build
```

```bash
xcodebuild -project HelmsleyDrive.xcodeproj -scheme HelmsleyDrive-iOS -configuration Debug -destination 'platform=iOS Simulator,name=iPhone 17 Pro' build
```

The first build of each has to register three things with the developer portal, which
`-allowProvisioningUpdates` does automatically — or open the project in Xcode once and let it:

- App IDs `uk.co.helmsley.HelmsleyDrive` and `uk.co.helmsley.HelmsleyDrive.FileProvider`
- App Group `group.uk.co.helmsley.HelmsleyDrive`, enabled on both
- Keychain sharing on both, group `uk.co.helmsley.HelmsleyDrive`

Both platforms use those same two App IDs, so they are one app in App Store Connect.

To check that it compiles without touching the developer portal at all:

```bash
xcodebuild -project HelmsleyDrive.xcodeproj -scheme HelmsleyDrive -configuration Debug CODE_SIGNING_ALLOWED=NO build
```

## Shipping the iOS app to TestFlight

Bump the version, which both Info.plists read through `$(...)` — `MARKETING_VERSION` and
`CURRENT_PROJECT_VERSION` in `Tools/generate-xcodeproj.py`, then re-run it. App Store Connect
rejects a build number it has already seen, so `CURRENT_PROJECT_VERSION` has to move every upload.

```bash
xcodebuild -project HelmsleyDrive.xcodeproj -scheme HelmsleyDrive-iOS -configuration Release \
  -destination 'generic/platform=iOS' -archivePath build/HelmsleyDrive-iOS.xcarchive \
  -allowProvisioningUpdates archive
```

Then **Xcode → Window → Organizer → Distribute App → TestFlight & App Store**. Use the Organizer
rather than `xcodebuild -exportArchive` for the first upload: the same flow creates the App Store
distribution certificate, exports the `.ipa` and uploads it, signed in as you throughout.

Before the first upload can succeed, the app record has to exist:
**App Store Connect → Apps → + → New App**, platform iOS, bundle ID `uk.co.helmsley.HelmsleyDrive`.
Then add testers under TestFlight → Internal Testing. Internal testers (up to 100, all on your
team) need no Beta App Review, so a build is installable within minutes of processing.

`ITSAppUsesNonExemptEncryption` is already `false` in the Info.plist — the only cryptography here is
HTTPS and the system keychain, which is exactly what the exemption covers — so no export-compliance
question is asked on any upload.

Once it is set up, later uploads can skip the Organizer with an App Store Connect API key:

```bash
xcodebuild -exportArchive -archivePath build/HelmsleyDrive-iOS.xcarchive \
  -exportOptionsPlist ExportOptions.plist -exportPath build/export -allowProvisioningUpdates
xcrun altool --upload-app -f build/export/HelmsleyDrive-iOS.ipa -t ios \
  --apiKey <KEY_ID> --apiIssuer <ISSUER_ID>
```

### Why TestFlight and not the App Store

The app's whole function is administrator access to one private portal. There is nothing for a
public reviewer to evaluate and no account they could be given, so a public listing invites a
rejection that TestFlight avoids entirely. Internal testing also has no review step at all.

macOS is not distributed this way — it is built and copied to `/Applications` (see above). The two
platforms share App IDs, so if the Mac app ever does need distributing, it belongs to the same App
Store Connect record.

### Changing the development team

One place: `DEVELOPMENT_TEAM` in `Tools/generate-xcodeproj.py` (currently `CR2F6D8AF7`), then re-run
it. The keychain access group is read back out of the running binary's own entitlements, so it
follows the signing team without being written down anywhere.

Note that the team id is **not** the identifier in a signing certificate's common name —
`Apple Development: Someone (RW34QL2584)` is the individual, not the team. Take it from
`codesign -dv` on a built product, or from Xcode's Signing & Capabilities pane. The wrong value
fails at signing with "No Account for Team …".

### The icon

Built from the portal's own mark (`../Helmsley/frontend/public/helmsley-h.svg`), so it is never a
second copy of the artwork that can drift from it:

```bash
python3 Tools/generate-icon.py
```

That writes `AppIcon.appiconset` into **both** targets' asset catalogs — the Dock and the About box
read the app's, and the Finder sidebar entry for the mounted domain reads the extension's.

Two things the script exists to get right. The mark is 825.4 × 1080.8, and every rasteriser to hand
fits an SVG to its output box, so rendering it straight to a square stretches it — the script
composes tile and mark into a square SVG first and renders that once. And the tile is the brand's
teal-to-green gradient with the mark knocked out in white, rather than the two-tone mark on white:
the mark's own colouring is lost that way, but a white tile is invisible in a light Dock or Finder
sidebar, which is where this icon spends its whole life.

Quick Look is the rasteriser, since it is the only one macOS ships. If the icon ever comes out
blank, that is the thing to check first.

### Regenerating the Xcode project

`HelmsleyDrive.xcodeproj` is generated, not hand-maintained:

```bash
python3 Tools/generate-xcodeproj.py
```

Adding a source file means adding it to the list at the top of that script and re-running. Editing
the project in Xcode works too, but the next regeneration overwrites it.

## Running it

**Install to `/Applications` first.** The app cannot be run from `DerivedData`: the file provider
daemon's TCC check fails on that location, and the failure surfaces as "The application cannot be
used right now" when mounting, or as a volume that mounts and stays empty.

```bash
ditto "$(xcodebuild -project HelmsleyDrive.xcodeproj -scheme HelmsleyDrive -configuration Debug \
  -showBuildSettings | awk -F' = ' '/ BUILT_PRODUCTS_DIR/{print $2}')/HelmsleyDrive.app" \
  "/Applications/Helmsley Drive.app"
```

`ditto`, not `cp` — it preserves the code signature. If a copy has previously been registered from
somewhere else, unregister it (`pluginkit -r <path to the .appex>`) so the daemon cannot prefer the
stale one; `pluginkit -m -v -i uk.co.helmsley.HelmsleyDrive.FileProvider` shows which path is live.

1. Build, install as above, and run the app.
2. **Sign In and Mount** — the portal's login opens in a system sheet; password, then the SMS code,
   then a consent screen.
3. *Helmsley Documents* appears in the Finder sidebar under Locations.

**Sign Out and Unmount** removes the domain and discards the credential.

The portal address is fixed at build time, in `Configuration.baseURL`. Pointing the apps at a local
backend is an edit there and a rebuild; that backend needs `helmsley-drive://oauth/callback`
registered in its own `config.json` too.

The app has to stay installed: an extension is loaded out of its host app's bundle, so deleting the
app unmounts the volume.

### On iPhone and iPad

Install from TestFlight, open the app, **Sign In and Add to Files**. The tree then appears in
**Files → Browse → Locations → Helmsley Documents**, and in any document picker — so a remittance
can be attached to an email without leaving Mail.

No `/Applications` equivalent to worry about: an installed iOS app is already where the system
expects it. **Add to Files** on its own re-registers the domain after a reinstall without asking for
another sign-in, exactly as **Mount in Finder** does on the Mac.

### When something is wrong

Start here — the extension logs every failure, and Finder shows none of them:

```bash
/usr/bin/log stream --predicate 'subsystem == "uk.co.helmsley.HelmsleyDrive"' --info
```

The full path matters: zsh has a `log` builtin of its own, and it answers every one of these with
"too many arguments" and nothing to suggest it is not the tool you meant. Swap `stream` for
`show --last 10m` to read what has already happened rather than waiting for more.

For the system's side of the conversation (it says why it refused something):

```bash
log show --last 10m --predicate 'subsystem == "com.apple.FileProvider"' --info | grep -i helmsley
```

`NSFileProviderErrorDomain Code=-1000` there is `notAuthenticated` — the extension could not read
the credential; `TCC access check failed … Cocoa 257` is the app not being in `/Applications`.


- **"Sign in" next to the volume in Finder** — the refresh token has expired (30 days) or was
  revoked. Open the app and sign in again.
- **"Keychain access failed: A required entitlement isn't present"** — the binary is not signed with
  the keychain-sharing entitlement. Check `codesign -d --entitlements - <app>` lists
  `<TEAMID>.uk.co.helmsley.HelmsleyDrive`; a build made with `CODE_SIGNING_ALLOWED=NO` has no
  entitlements at all and will always fail this way.
- **Nothing appears in the sidebar, or "The application cannot be used right now"** — the daemon
  could not load the extension. Almost always the app not being in `/Applications`; check the
  registered path with `pluginkit -m -v -i uk.co.helmsley.HelmsleyDrive.FileProvider`.
- **The volume mounts but every folder is empty** — enumeration is failing, not the portal being
  empty. The log says which; `-1000` means the extension cannot read the keychain.
- **A rebuild to a new location silently unmounts it** — the domain is dropped when the bundle
  backing it moves. The credential survives, so **Mount in Finder** in the app puts it back without
  another sign-in.
- **The consent screen's Connect button appears to do nothing** — the CSP is blocking the redirect
  out to `helmsley-drive://oauth/callback`. `form-action` on the response must include
  `helmsley-drive:`; `consentFormActions()` in `backend/utils/http/urls.js` derives it from every
  registered client, so this means the portal being signed into is running an older build. It fails
  silently by design — a blocked form submission is a console line and nothing else.
- **Uploads refused, or a folder that will not take a new folder** — the portal does not call that
  folder writable for this admin. `Orphaned/` takes nothing from anybody, and another admin's folder
  under `Staff/` takes nothing from you however senior you are. The listing says so in its
  `permissions` block, which is also why Finder usually refuses the drop before a byte moves.

## Verified

Since the move onto the one `files` tree, only the first of these has been re-run: all four targets
build clean, macOS and iOS, with the extension embedded at
`Contents/PlugIns/HelmsleyFileProvider.appex`. Everything below was verified against the portal as it
was, and the mechanisms it exercises — the OAuth round trip, the redirect into the bucket, the
stripped `Authorization` header, the enumeration and diff — are untouched by the change. What is
genuinely new and unexercised is the tree the volume walks and the permissions it now reads off each
row. **Both want re-running against the live portal before this is trusted.**

- `/api/files` rejects a missing or invalid bearer token with a 401 and a `WWW-Authenticate` header.
- The listing path resolves against the live database: root folders, property and client fan-outs,
  real filenames with extensions, byte sizes and content hashes, and a 404 for an id that does not
  exist.

Working against the live portal, through the real mount at
`~/Library/CloudStorage/HelmsleyDrive-HelmsleyDocuments`:

- the OAuth sign-in round trip, and the credential shared from the app to the extension
- the root and both entity fan-outs (`Properties`, `Clients`) enumerating with real names
- files listed with their true sizes, and permissions matching the tree
- fetching a file's bytes end to end, proven by hash: the md5 of a PDF read through the mount
  equals the `content_hash` the database recorded at upload, with the log showing the fetch, the
  redirect to `storage.googleapis.com`, and `Authorization` being stripped on the way

  Verify with anything that reads every byte — `md5`, not `wc -c`, which answers from `fstat`
  without touching the file and so "passes" against a file that was never downloaded at all.

On iOS, verified as far as this machine allows:

- all four targets build; the macOS pair is unchanged by the split
- the iOS app runs in the simulator and renders correctly
- a Release archive for `generic/platform=iOS` signs and validates: both bundle ids, the extension
  embedded with the right principal class, icons compiled in, `ITSAppUsesNonExemptEncryption` set,
  and the signed entitlements carrying the app group and the team-prefixed keychain group

Not yet exercised: upload and delete from Finder; and on iOS, everything past launch — signing in
needs a real SMS code, and the extension needs a device.
