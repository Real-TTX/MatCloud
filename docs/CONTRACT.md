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
| `POST /api/v1/devices/logout` | ends the token (204) |
| `GET /api/v1/account` | `AccountInfo { userName, displayName, deviceName, app, serverVersion }` |

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

## ThixClip (the module `clip`)

Only a device of `clip`: `GET/PUT /api/v1/clip/settings` (`ClipSettings { retentionDays }`: 1, 7, 30, 90, 365 or 0 = forever, for the whole account),
`GET /api/v1/clip/changes?since=&limit=` (`ClipChangeSet { clips, latest, hasMore, retentionDays }`), `GET /api/v1/clip/wait`, `POST /api/v1/clip/push`
(`ClipPushRequest`/`ClipPushResponse` with `mergedInto`, `isDeleted`, `needsImage`), `PUT/GET /api/v1/clip/{syncId}/image` (PNG ≤ 25 MB),
`GET /api/v1/clip/{syncId}/thumbnail`. Rules: deleting wins; the same content (SHA-256 of the UTF-8 text / the PNG) under another SyncId is merged;
images go up after their entry; the retention is an account setting (favourites and pinned clips stay).
