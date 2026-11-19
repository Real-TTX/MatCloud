# Architecture

```
 ThixClip (PC, Mac) ──┐   /api/v1/devices/*, /api/v1/clip/*                      ┌── Browser / PWA: Übersicht, Zwischenablage, Apps, Konto, Administration
 ThixNote (PC, Mac) ──┤   /api/v1/devices/*, /api/v1/sync/*      MatCloud        │
 ThixWrite/Calc/Show ─┤   /api/v1/drive/* (the grant of the Drive)               │
 Thix… (later)      ──┘   bearer token of one device (one app)  ─────────────────┤── Mattiverse (broker, shell): /api (MatPlatform contract 2), MAT_API_KEY
                                                                  │               └── MatIdentity (optional): OpenID Connect, back-channel logout
                                                            PostgreSQL 17
```

## Layers

- **MatCloud.Contract** – what the apps copy: routes, DTOs, limits, `MatCloudClient`. No server logic.
- **MatCloud.Core** – the platform: accounts (local and MatIdentity), sessions, devices per app, the sign-in in the browser, which apps are on, the general
  sync, the device API, the module contract, the shared web parts (layout, menus, controls), MatPlatform.
- **Modules** (`src/Apps/*`) – an app with its own tables, API and pages (ThixClip), or pages on the items of the general sync (ThixNote, `UsesGeneralSync`).
- **MatCloud** (host) – composition, the platform description (`/api`), the pages of the platform, the migrations, the files of the browser.

## Accounts and sign-in

- Local accounts (user name + password, PBKDF2) with the setup assistant for the first administrator; administrators create accounts.
- **MatIdentity** (when `MATIDENTITY_AUTHORITY` is set): the sign-in goes there; the person is found by `sub`, else a new account is made (the very first
  one is an administrator); a local account with the same user name is linked only after its own password (`MatIdentityPendingLink`). The claim `role`
  (`admin`/`user`) is followed, never demoting the last administrator. The back-channel logout ends the sessions (by `sid`, or every session of the `sub`).
  The local sign-in stays at `/Account/Login?local=1`.
- **Devices**: an account of MatIdentity has no password here, so a device signs in **in the browser** (like the device flow of OAuth): the app gets a code,
  the person confirms it on `/Connect` while signed in, the app fetches its token once. A local account can also sign in a device with its password.

## Data and sync

| Table | What |
|---|---|
| `User`, `UserSession` | accounts (`ExternalSubject` for MatIdentity), browser sessions (token in the cookie; MatIdentity `sid` and ID token) |
| `Device`, `DeviceLink` | one app on one computer (`App`, bearer `Token`), the sign-ins in the browser (code, poll token, 10 minutes) |
| `AppSetting`, `SystemSetting` | apps switched off; the public address |
| `SyncItem` + sequence `SyncRevision` | the general sync: `UserId`, `App`, `Collection`, `Key`, `Data`, `ContentHash`, `Size`, `ClientUpdateDate`, `Source`, `Revision`, `UpdateState` |
| `ClipEntry`, `ClipImage`, `ClipAccount` + sequence `ClipRevision` | the module ThixClip |
| `DriveSpace`, `DriveNode`, `DriveNodeVersion`, `DriveBlob`, `DriveStar`, `DriveVisit`, `DriveUpload` + sequence `DriveRevision` | the module ThixDrive (below) |

A **stream** is the data of one app of one account. Every change of a stream takes its revisions inside a transaction that holds the stream's advisory
lock, so the revisions of a stream are committed in order and "everything above N" never misses a change; after the commit the long polls of the stream
are woken (`RevisionSignals`, in memory: one server instance). Devices of an app only ever see their app's stream – unless a module grants its stream
to other apps (below).

## The Drive (module `drive`) and its grant to the office apps

- **One device token = one app** stays the rule. A module may name other apps whose devices may use its device API (`ICloudModule.GuestApps`); only
  the Drive does: `write`, `calc`, `show` (`DriveContract.GuestApps`, so the apps can read it too). The grant is checked twice: `DeviceEndpoints.ForApp`
  lets a guest device through only while the module's app is on (switching the Drive off stops ThixWrite there too, 403), and the business layer asks
  `Actor.MayUse(CloudApps.Drive)`, which honours the grant (`DriveAccess.RequireAsync`, the first line of every function). A guest reaches exactly the
  same data as a device of the Drive: the Drive of its own account.
- **Data**: `DriveSpace` is the drive of one account (`Kind` Personal; Shared is reserved for team drives, shares and links in v2), `DriveNode` a file
  or folder with a materialized path (`/12/40/41/`: the folders above and everything below with one query), `NameKey` (the name in lower case; a unique
  index per folder outside the trash, `NULLS NOT DISTINCT` for the top), `Category` (type filter), the current version (`CurrentVersionId` = the ETag,
  `VersionNumber`, `Sha256`, `Size`), `InTrash`/`TrashedDate`, `Revision`; deleted for good it stays a tombstone (`UpdateState` 0) for a year.
  `DriveNodeVersion` (number, SHA-256, size, the device and app that saved it), `DriveStar`, `DriveVisit` (recent), `DriveUpload` (uploads in pieces).
- **Bytes**: `/data/drive/blobs/ab/cd/<sha256>`, one file per content for the whole installation (`DriveBlob`): the same document in two places or saved
  twice is stored once, a copy names the same blob. Bytes arrive in `/data/drive/temp` (hashed while they are written, never held in memory), are moved
  into place before the lock, and become a version under the lock. A blob no version names is removed an hour after its last use (`DriveWorker`): the
  row and the file go in one transaction, and an upload of the same content touches the row first, so it either keeps the blob or puts its own copy back.
- **Consistency**: every change of a drive runs under the advisory lock of the account's stream (`Revisions.ChangeAsync`), so names, paths and the quota
  stay right when two devices change something at once. A new version needs `If-Match` (the ETag it was opened with, or `*`); it is checked before the
  bytes are read and again under the lock (412 with the current item).
- **Limits**: `MATCLOUD_DRIVE_QUOTA_GB` (default 10, 0 = none; `DriveSpace.QuotaBytes` per account), `MATCLOUD_DRIVE_MAX_FILE_MB` (default 2048);
  64 MB per single request, pieces of 8 MB (at most 16 MB). The quota counts every version, the trash included.

## The platform contract (MatDesign docs/PLATFORM.md)

`/api` (MatApiDefinition: tiles users, devices, active today, apps on, sync items/size, last contact; setting `publicUrl`, fixed while `MAT_PUBLIC_URL` is
set; actions open, users, apps, connections, clean up; launcher MatCloud with quick actions Zwischenablage, Apps und Daten, Gerät verbinden; widgets
"Geräte" and "Apps an" for administrators; roles admin, user; consumes `identity.oidc`), `/api/health` (database, administrators), `/api/state`,
`/api/configure`, `/api/actions/cleanUp`, `/api/connections` and the Connections area (`/Admin/Connections`). `MAT_FRAME_ANCESTORS` lets Mattiverse show it.

## Housekeeping (every hour, and the action "Aufräumen")

Ended sessions, sign-ins in the browser older than a day, tombstones of the general sync older than a year, and each module's own (the retention of the
clips: older clips become tombstones, favourites and pinned ones stay; the Drive: the trash after 30 days, its tombstones after a year, expired uploads
in pieces – and every hour on its own, `DriveWorker`: blobs nobody names and temporary files of broken uploads).
