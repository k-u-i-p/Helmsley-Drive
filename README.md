# Helmsley Drive

The Helmsley client portal's document tree, mounted in Finder as a volume.

Not a sync folder: nothing is copied to the Mac until it is opened, and the structure in Finder is
the portal's own documents explorer — the same folders, showing the same documents, filing an upload
exactly where the dashboard would have filed it.

```
Helmsley Documents/
├── News/                          ← accepts uploads
├── Properties/
│   ├── Arabesque Syndicate/
│   │   ├── News/  Valuation/  Annual Report/  Brochure/
│   └── Inactive/…
├── Clients/
│   └── A J Bell Platinum SSAS/
│       ├── Remittance/  Properties/  Compliance/
└── Orphaned/                      ← only when something is in it
```

## How it fits together

Two halves, in two repositories.

**`../Helmsley` — the portal.** Gained a file-provider API and a way for a native app to
authenticate. See *Changes to the portal* below.

**This repository — the Mac app.** An Xcode project with two targets:

| Target | What it is | What it does |
| --- | --- | --- |
| `HelmsleyDrive` | the app | Signs in, and registers the file provider domain. Nothing else. |
| `HelmsleyFileProvider` | an `NSFileProviderReplicatedExtension` | Everything Finder actually talks to: enumerating folders, fetching bytes, uploading, deleting. |

They are separate processes and share three things — the portal address (app group defaults), the
OAuth token set (keychain access group), and the enumeration snapshots the change diff is computed
against (app group container). `Shared/` is compiled into both.

```
Shared/
  Configuration.swift   identifiers, portal address, and the one hard-coded team prefix
  OAuth.swift           PKCE flow, token exchange/refresh, and the actor that hands out a live token
  TokenStore.swift      the token set, in the shared keychain
  HelmsleyAPI.swift     every call to /api/files
  ItemIdentity.swift    what an NSFileProviderItemIdentifier means here
HelmsleyDrive/          the app: sign-in sheet, window, domain registration
FileProvider/           the extension: items, enumerators, and the extension class itself
Tools/generate-xcodeproj.py   regenerates HelmsleyDrive.xcodeproj (see below)
Tools/generate-icon.py        builds the app icon from the portal's H mark
```

### Identity

The portal's tree is a set of database views, not stored paths: a document row has no path, and it
appears in every folder whose filter matches it. A filesystem insists on one item having one parent,
so a file's identity here is *the document as seen from a particular folder*. A document listed in
two folders is two items over the same bytes — each is exactly what that folder shows, and deleting
either deletes the document.

A file's version is its content hash, which is also its key in the storage bucket. It changes when
and only when the bytes do, so a downloaded copy stays valid until the document is genuinely
replaced.

### Keeping up to date

The portal has no change feed — `documents` records an upload date and nothing else — so "what
changed" is computed in the extension, by listing a folder and diffing it against what that folder
held last time (`FileProvider/SnapshotStore.swift`, persisted so a cold start still knows what has
been removed). Changes made in the dashboard therefore appear when the system next asks: on refresh,
on navigating in, and immediately after any upload or delete made from Finder.

The working set is deliberately empty. Filling it would mean enumerating every document of every
client of every syndicate on a schedule nobody asked for; folders enumerate on demand instead.

## What Finder can and cannot do

| | |
| --- | --- |
| Browse the whole admin tree | yes |
| Open, preview, Quick Look, copy out | yes — downloaded on first open, evictable afterwards |
| Drag a file **into** a folder that accepts uploads | yes — filed via the portal's own ticket → bucket → finalise path |
| Delete a document | yes — deletes the row and its bytes, exactly as the dashboard's delete does |
| Rename, move, or save changes back | **no** — refused with an explanation |

Rename and in-place editing are refused because the portal has no endpoint behind them: a
document's title, type and links are the filing an admin chose, and some filings it refuses to
change at all (a compliance document cannot be refiled). Offering them would mean accepting an edit
in Finder that quietly never reached the server. Files show as locked, which is the truth.

Which folders accept uploads comes from the portal's `directoryStructure.js`, resolved server-side —
the app never sends a document type or a client id, so it cannot file anything anywhere the
dashboard would not have.

## Changes to the portal

All in `../Helmsley`:

| File | Change |
| --- | --- |
| `backend/routes/files.js` | **new** — the `/api/files` API this app talks to |
| `backend/utils/http/bearerAdmin.js` | **new** — authenticates a request by OAuth access token instead of by session cookie |
| `backend/utils/domain/documents/deleteDocument.js` | **new** — the delete (row + bytes), extracted so Finder and the dashboard cannot come to disagree about it |
| `backend/routes/admin/documents.js` | delete now calls the above |
| `backend/utils/domain/documents/directoryCompiler.js` | admin file listings also carry `file_mime`, `byte_size`, `content_hash` |
| `backend/routes/admin/mcp/oauthProvider.js` | resolves additional statically registered OAuth clients |
| `backend/utils/http/urls.js` | CSP `form-action` covers every registered client's callback, private-use schemes by scheme alone |
| `backend/config.js` | `mcp.clients[]` — the schema for those |
| `backend/server.js` | mounts `/api/files` with its own rate-limit bucket |
| `config.json` | registers the `helmsley-drive` client |

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

## Building

Requires Xcode and a signing team. The project is set to team `RW34QL2584` (Ben Reeves).

```bash
xcodebuild -project HelmsleyDrive.xcodeproj -scheme HelmsleyDrive -configuration Debug -allowProvisioningUpdates build
```

The first build has to register three things with the developer portal, which `-allowProvisioningUpdates`
does automatically — or open the project in Xcode once and let it:

- App IDs `uk.co.helmsley.HelmsleyDrive` and `uk.co.helmsley.HelmsleyDrive.FileProvider`
- App Group `group.uk.co.helmsley.HelmsleyDrive`, enabled on both
- Keychain sharing on both, group `uk.co.helmsley.HelmsleyDrive`

To check that it compiles without touching the developer portal at all:

```bash
xcodebuild -project HelmsleyDrive.xcodeproj -scheme HelmsleyDrive -configuration Debug CODE_SIGNING_ALLOWED=NO build
```

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

The *Server…* button repoints both processes at another portal — a local backend, usually. Changing
it signs you out, since a token minted by one server means nothing to another. A local backend needs
`helmsley-drive://oauth/callback` registered in its own `config.json` too.

The app has to stay installed: an extension is loaded out of its host app's bundle, so deleting the
app unmounts the volume.

### When something is wrong

Start here — the extension logs every failure, and Finder shows none of them:

```bash
log stream --predicate 'subsystem == "uk.co.helmsley.HelmsleyDrive"' --info
```

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
- **Uploads refused** — check the folder takes them. Only leaves with an `upload` block in the
  portal's `directoryStructure.js` do, and the portal refuses byte-identical duplicates of a
  document already filed there.

## Verified

- Both targets build clean, extension embedded at `Contents/PlugIns/HelmsleyFileProvider.appex`.
- `/api/files` rejects a missing or invalid bearer token with a 401 and a `WWW-Authenticate` header.
- The listing path resolves against the live database: root folders, property and client fan-outs,
  real filenames with extensions, byte sizes and content hashes, upload descriptors binding
  `client_id` from the path, and a 404 for a client id that does not exist.

Working against the live portal, through the real mount at
`~/Library/CloudStorage/HelmsleyDrive-HelmsleyDocuments`:

- the OAuth sign-in round trip, and the credential shared from the app to the extension
- the root and both entity fan-outs (`Properties`, `Clients`) enumerating with real names
- files listed with their true sizes, and permissions matching the tree — `News` writable,
  `Properties` and `Clients` read-only, documents read-only
- fetching a document's bytes end to end: 302 into Cloud Storage, `Authorization` stripped at the
  redirect, all 2,019,970 bytes of a PDF arriving intact

Not yet exercised: upload and delete from Finder.
