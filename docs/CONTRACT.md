# The contract of MatCloud with the Thix apps

What a Thix app (ThixClip, ThixNote, …) needs to talk to MatCloud. The source of truth is `src/MatCloud.Contract` (C#): routes, types, limits, JSON
options and the client `MatCloudClient`. An app **copies** it:

```
node tools/contract-copy.js <folder of the app's project that compiles it>     # e.g. ../ThixNote/src/ThixNote.Core
```

It writes `<folder>/MatCloud/**.cs` (first line `// MatCloud.Contract <version> – a copy from MatCloud, do not edit here`) and `matcloud.version` (the
version, then the SHA-256 of each copied file with LF line ends). The app's tests hold the copy to `matcloud.version` (a test like ThixClip's
`The_copy_of_MatCloud_is_unchanged`). Never edit the copy: change the contract here, then copy it into every app again.

## Rules

- JSON, camelCase, enums as text, nulls left out (`CloudContract.Json`). Errors: `ApiError { message, field }` with a German message.
- Every answer carries `X-MatCloud-Protocol: 1`; an app that sends a higher version gets 400 ("Diese App ist neuer als die MatCloud …").
- A **device** is one app on one computer. It signs in for exactly one app (`app`: an id of `CloudApps`) and its token reads and writes only the data of
  that app of its account. `401`: the token is unknown or the device was removed (the app signs out). `403`: the app is switched off by an
  administrator, or the route belongs to another app.
- The user agent names the app and its version: `ThixNote/1.1.42 (windows)`; the cloud shows the version with the device.

## Sign-in

| Route | What |
|---|---|
| `POST /api/v1/devices/login` | `DeviceLoginRequest { userName, password, deviceName, platform, appVersion, app }` → `DeviceLoginResponse { token, deviceId, account }`. 401 wrong name/password, 403 app off, 400 bad device name / unknown app; 10 attempts a minute per address (429). The same device name + platform + app signing in again replaces its old entry. Only local accounts have a password. |
| `POST /api/v1/devices/link` | the **sign-in in the browser** (every account, also one of MatIdentity): `DeviceLinkRequest { deviceName, platform, appVersion, app }` → `DeviceLinkStart { code: "ABCD-EFGH", verificationUrl, pollToken, expiresInSeconds: 600, intervalSeconds: 2 }`. The app shows the code and opens `verificationUrl` (`<public address>/Connect?code=…`). |
| `POST /api/v1/devices/link/poll` | `{ pollToken }` → `DeviceLinkPollResponse { state: Pending \| Approved \| Denied \| Expired, login? }`. `Approved` carries the `DeviceLoginResponse` exactly once. `MatCloudClient.WaitForLinkAsync` polls until it is decided. |
| `POST /api/v1/devices/handoff` | only a device of `launcher`: `DeviceHandoffRequest { app, appVersion }` → `DeviceHandoffStart { pollToken, expiresInSeconds: 86400 }`, a sign-in for another app on the launcher's computer, already confirmed (see below). 403 for any other device, 400 (`field` = `app`) for the launcher itself, an unknown app or one that is switched off. |
| `POST /api/v1/devices/logout` | ends the token (204) |
| `GET /api/v1/account` | `AccountInfo { userName, displayName, deviceName, app, serverVersion }` |

### The hand-over of ThixLauncher

ThixLauncher signs in once; every app it installs is signed in too, without a browser:

1. Before it runs the app's setup, the launcher asks `POST /api/v1/devices/handoff` and leaves the answer for the app with
   `CloudHandoff.Write(app, new DeviceHandoff(serverUrl, pollToken, userName, expiresAt))`: one file per app in the local application data of the
   user (`%LOCALAPPDATA%\Thix\MatCloud\handoff\<app>.json`, `~/Library/Application Support/Thix/MatCloud/handoff/<app>.json`, only the user may read
   it). When the cloud cannot be reached, the app is installed anyway, just without the hand-over.
2. At its start, while it is not connected, the app looks for it (`CloudHandoff.Find(app)`: null when there is none, an expired or unreadable one is
   removed) and fetches its token with `MatCloudClient.RedeemHandoffAsync(handoff)` (the poll of the browser sign-in; the device is made now, with the
   launcher's device name and platform). It removes the file (`CloudHandoff.Remove`) when it got the token or null (used or expired), and keeps it
   when the cloud could not be asked (the next start tries again).
3. The hand-over works once and for a day; an app started later signs in as usual. Tests set `CloudHandoff.Folder` to a folder of their own.

## The general sync (every app without a module)

| Route | What |
|---|---|
| `GET /api/v1/sync/changes?since=&limit=` | `SyncChangeSet { items, latest, hasMore }`: everything of the app above `since` in revision order (tombstones included), at most `limit` (≤ 200) and about 8 million characters; ask again while `hasMore` |
| `GET /api/v1/sync/wait?since=&timeout=` | long poll (≤ 30 s): `{ latest }` as soon as the app's data of the account has a revision above `since` |
| `POST /api/v1/sync/push` | `SyncPushRequest { items }` (≤ 200, each data ≤ 2,000,000 characters, body ≤ 32 MB) → `SyncPushResponse { results, latest }`; a bad item refuses the whole push (400, `field` = `items[i].key` …) |

An item: `collection` (`[a-z0-9][a-z0-9._-]{0,63}`: a kind of thing, "notes"), `key` (1–200 characters, no control characters: one thing, usually a
GUID), `data` (text the app defines: JSON, a note, base64 of encrypted bytes — the cloud never reads it), `contentHash` (optional), `updatedAt` (the
change on the device), `isDeleted`; set by the cloud: `source` (the device), `revision`.

- **Last writer wins** by `updatedAt`: a push older than what the cloud has is not applied; its result has `applied: false` and `current` = the item that
  won (the app takes it). A delete is a write too: it leaves a tombstone (no data); a later write brings the item back.
- Every change takes the next number of one sequence while the app's data of the account is locked, so "everything above N" never misses a change.
- Tombstones are kept a year (every device has seen them by then).
- Collections that begin with `cloud-` are written by MatCloud only (a module that syncs on the server): a device reads them; its push into one is not
  applied (`applied: false`, `current` = the item as it is).

## ThixClip (the module `clip`)

## ThixCalendar (the module `calendar`, contract 1.3.0)

ThixCalendar keeps its data in the general sync (`settings`, `accounts`, `calendars`, `events`, `answers`; types in `Calendar/CalendarContract.cs`).
An online account has a **sync mode** (`CalendarAccountItem.syncMode`): `Computers` (every computer signs in itself, as before 1.3.0 – also when it is
missing) or `Cloud` (MatCloud keeps the sign-in, protected, and syncs on the server, also while no computer runs). The default for new accounts is
`CalendarSettingsItem.defaultSyncMode` (missing = `Cloud`). For a `Cloud` account MatCloud writes `cloud-accounts` (state: `Connecting`, `Ready`,
`SignInRequired`, `Failing`), `cloud-calendars` (the provider's calendars, key = `CalendarKey(account, remoteId)`) and `cloud-events` (one item per
occurrence, −14 … +120 days, key = `EventKey(calendarKey, remoteId)`).

Only a device of `calendar`: `PUT /api/v1/calendar/connections` (`CalendarHandover { account, kind, userName, refreshToken, url }` → `CalendarCloudAccount`:
MatCloud keeps the account in step from now on; the device forgets its own sign-in), `POST /api/v1/calendar/connections/remove` (`{ account }`, 204: back to
the computers, MatCloud forgets the sign-in and removes its items), `POST /api/v1/calendar/sync` (`{ account }`, 202: sync now), `POST /api/v1/calendar/events`
(`CalendarEventChange { event, data, delete }` → `CalendarEventChangeResult { event, data }`: an event of a `Cloud` account is changed at Microsoft /
Google first, then in `cloud-events`). Refresh tokens must be of ThixCalendar's registration (`CalendarContract.MicrosoftClientId`, `GoogleClientId`).

Only a device of `clip`: `GET/PUT /api/v1/clip/settings` (`ClipSettings { retentionDays }`: 1, 7, 30, 90, 365 or 0 = forever, for the whole account),
`GET /api/v1/clip/changes?since=&limit=` (`ClipChangeSet { clips, latest, hasMore, retentionDays }`), `GET /api/v1/clip/wait`, `POST /api/v1/clip/push`
(`ClipPushRequest`/`ClipPushResponse` with `mergedInto`, `isDeleted`, `needsImage`), `PUT/GET /api/v1/clip/{syncId}/image` (PNG ≤ 25 MB),
`GET /api/v1/clip/{syncId}/thumbnail`. Rules: deleting wins; the same content (SHA-256 of the UTF-8 text / the PNG) under another SyncId is merged;
images go up after their entry; the retention is an account setting (favourites and pinned clips stay).
