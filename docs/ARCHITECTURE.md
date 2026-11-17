# Architecture

```
 ThixClip (PC, Mac) ──┐   /api/v1/devices/*, /api/v1/clip/*                      ┌── Browser / PWA: Übersicht, Zwischenablage, Apps, Konto, Administration
 ThixNote (PC, Mac) ──┤   /api/v1/devices/*, /api/v1/sync/*      MatCloud        │
 Thix… (later)      ──┘   bearer token of one device (one app)  ─────────────────┤── Mattiverse (broker, shell): /api (MatPlatform contract 2), MAT_API_KEY
                                                                  │               └── MatIdentity (optional): OpenID Connect, back-channel logout
                                                            PostgreSQL 17
```

## Layers

- **MatCloud.Contract** – what the apps copy: routes, DTOs, limits, `MatCloudClient`. No server logic.
- **MatCloud.Core** – the platform: accounts (local and MatIdentity), sessions, devices per app, the sign-in in the browser, which apps are on, the general
  sync, the device API, the module contract, the shared web parts (layout, menus, controls), MatPlatform.
- **Modules** (`src/Apps/*`) – an app with its own tables, API and pages (ThixClip).
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

A **stream** is the data of one app of one account. Every change of a stream takes its revisions inside a transaction that holds the stream's advisory
lock, so the revisions of a stream are committed in order and "everything above N" never misses a change; after the commit the long polls of the stream
are woken (`RevisionSignals`, in memory: one server instance). Devices of an app only ever see their app's stream.

## The platform contract (MatDesign docs/PLATFORM.md)

`/api` (MatApiDefinition: tiles users, devices, active today, apps on, sync items/size, last contact; setting `publicUrl`, fixed while `MAT_PUBLIC_URL` is
set; actions open, users, apps, connections, clean up; launcher MatCloud with quick actions Zwischenablage, Apps und Daten, Gerät verbinden; widgets
"Geräte" and "Apps an" for administrators; roles admin, user; consumes `identity.oidc`), `/api/health` (database, administrators), `/api/state`,
`/api/configure`, `/api/actions/cleanUp`, `/api/connections` and the Connections area (`/Admin/Connections`). `MAT_FRAME_ANCESTORS` lets Mattiverse show it.

## Housekeeping (every hour, and the action "Aufräumen")

Ended sessions, sign-ins in the browser older than a day, tombstones of the general sync older than a year, and each module's own (the retention of the
clips: older clips become tombstones, favourites and pinned ones stay).
