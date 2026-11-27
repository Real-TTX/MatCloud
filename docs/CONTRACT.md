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
- **The one exception is a grant of a module, explicit and on the server** (`ICloudModule.GuestApps`, read-only `ReadingGuestApps`; see ThixContacts below for ThixMail and ThixCalendar): the Drive lets the devices of ThixWrite,
  ThixCalc and ThixShow (`DriveContract.GuestApps`) call its routes and reach the Drive of THEIR account. Nothing else is loosened: a device of
  ThixClip or ThixNote gets 403 at the Drive, a device of the Drive 403 at the routes of ThixClip. While the Drive is switched off, the office apps
  get 403 there too (their own sign-in, account and general sync keep working).
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
- A module that is a web client of the general sync may keep an item as it is (`ICloudModule.AcceptsSyncChange`, answered as above; the only place the
  cloud looks into `data`). **ThixNote** (`notes` = `{ path, content | bytes, unprotected? }`): a protected note (`bytes`, `.txts`) becomes plain text
  (`content`, or a path not ending `.txts`) only when the push carries `"unprotected": true` – ThixNote sets it only on the one push of a deliberate
  "Schutz aufheben". Without it (an old plain copy of a device that was offline while the note was protected elsewhere) the push is not applied and the
  device gets the protected note. A delete, a change or rename of a protected note, protecting a plain note and plain changes follow the last writer.
  No change of the contract: the field lives in the app's own data. Known and accepted: a protected note that was deleted is a tombstone without data,
  so a later plain write of an old copy brings it back as plain text (the rule "a later write brings the item back"; one push cannot carry the delete
  and the plain text of one key, `Validate` refuses a key twice).
  **Release order:** a ThixNote without the marker cannot unprotect a note the cloud has protected once this rule runs: its push is refused, and
  ThixNote versions before the marker do not take the refusal back (the note stays plain on that device only and its push is refused again every
  round). So MatCloud with this rule is deployed only after the ThixNote release that sends the marker has reached the devices.

## ThixClip (the module `clip`)

## ThixDrive (the module `drive`, contract 1.4.0) – also for ThixWrite, ThixCalc, ThixShow

The files and folders of an account (`Drive/DriveContract.cs`, the calls in `Drive/MatCloudClient.Drive.cs`). Devices of `drive`, `write`, `calc` and
`show` may call these routes (see the rules above); every item has a stable `id` (a number that never changes, also when it is renamed or moved),
a file has versions and an `eTag` (`"<version id>"`) that names the current one.

| Route | What |
|---|---|
| `GET /api/v1/drive/items?parentId=&sort=&descending=&offset=&limit=` | `DriveListing { folder, ancestors, items, total, offset, limit }`: a folder (no `parentId`: the top), folders first, `sort` = `Name` \| `Modified` \| `Size` \| `Type`, `limit` ≤ 500 |
| `GET /api/v1/drive/items/{id}` | `DriveItem` with `ancestors` (the folders above it) |
| `GET /api/v1/drive/search?q=&offset=&limit=`, `GET …/recent?limit=`, `GET …/starred`, `GET …/trash` | `DriveItemPage { items, total, offset, limit }`; recent = what the account opened or saved last, in any app |
| `GET /api/v1/drive/quota` | `DriveQuota { usedBytes, quotaBytes?, trashBytes, olderVersionsBytes, maxFileBytes, maxSimpleUploadBytes, chunkBytes }` |
| `POST /api/v1/drive/folders` | `DriveFolderRequest { parentId?, name }` → 201 `DriveItem` |
| `POST …/items/{id}/rename` · `/move` · `/copy` | `{ name }` · `{ parentId? }` · `{ parentId?, name? }` → `DriveItem` (copy: 201; the bytes are shared, not copied) |
| `POST …/items/{id}/trash` · `/restore`, `DELETE …/items/{id}` | into the trash (30 days) · back (into its folder, else the top; a taken name gets a number) · for good (only from the trash, else 409) |
| `POST` / `DELETE …/items/{id}/star` | star / unstar (204) |
| `GET …/items/{id}/content` | the bytes of the current version, streamed; `ETag`, `Last-Modified`, `Range` (206/416) and `If-None-Match` (304) |
| `PUT …/items/{id}/content` | a new version, the body is the file (≤ 64 MB); **`If-Match` is required**: the ETag the app opened, or `*` |
| `POST /api/v1/drive/files?parentId=&name=&onConflict=Fail\|Rename` | a new file, the body is the file (≤ 64 MB) → 201 `DriveItem` |
| `POST /api/v1/drive/uploads` | `DriveUploadRequest { size, parentId?, name?, onConflict, itemId?, ifMatch? }` → 201 `DriveUploadState { id, size, received, chunkBytes, expiresAt }`: larger files in pieces; every check runs here first |
| `PUT …/uploads/{id}/chunk?offset=`, `GET …/uploads/{id}`, `POST …/uploads/{id}/finish`, `DELETE …/uploads/{id}` | a piece (≤ 16 MB, from `received` on; a piece again is fine) · how much arrived (resume) · done → `DriveItem` · give up |
| `GET …/items/{id}/versions`, `GET …/versions/{v}/content`, `POST …/versions/{v}/restore` | `DriveVersionList` (`number`, `size`, `eTag`, `sha256`, `createdAt`, `source` = the device, `app`) · the bytes of one · it becomes current again as a new version |

Status codes: 400 a bad value (`field`: `name`, `parentId`, `offset` …), 403 another app or the Drive switched off, 404 not found (also an item of
another account: nothing can be probed), **409** the name is taken (case-insensitive) or a folder would go into itself, **412** `If-Match` is not the
current version, 413 too large (`MATCLOUD_DRIVE_MAX_FILE_MB`, default 2 GB; a single request 64 MB), 428 `If-Match` is missing, **507** the quota of
the account is full. 409 and 412 carry a `DriveConflict { message, field, current }` (an `ApiError` plus the item as it is now).

Names are refused, never changed: no `/ \ : * ? " < > |`, no control characters, not `.`/`..`, no space or dot at the end, none of Windows' reserved
names (`CON`, `NUL`, `COM1` …), at most 255 characters. Items are reached by id, never by a path.

**Opening and saving a document** (ThixWrite, ThixCalc, ThixShow with `MatCloudClient`):

```csharp
var recent = await client.GetDriveRecentAsync(ct);                                   // "Zuletzt verwendet"
var listing = await client.ListDriveFolderAsync(folderId, ct);                      // browse; null = the top
var etag = await client.DownloadDriveFileAsync(item.Id, fileStream, ct);             // open: keep the ETag
try
{
    item = await client.SaveDriveFileAsync(item.Id, document, etag, ct);           // save: a new version, the new item.ETag
}
catch (DriveConflictException conflict) when (conflict.IsChangedMeanwhile)         // 412: somebody saved in between
{
    // "Überschreiben":         await client.SaveDriveFileAsync(item.Id, document, conflict.Current!.ETag!, ct);
    // "Als Kopie speichern":   await client.UploadDriveFileAsync(item.ParentId, item.Name, document, DriveNameConflict.Rename, ct);
}
var created = await client.UploadDriveFileAsync(folderId, "Angebot.docx", document, DriveNameConflict.Fail, ct);   // save as (409: name taken)
```

`UploadDriveFileAsync` and `SaveDriveFileAsync` send up to 64 MB in one request and larger files in pieces of 8 MB (a broken piece is sent again after
asking how much arrived); a stream that cannot seek is spooled to a temporary file first. `DownloadDriveFileAsync` streams into the given stream.

## ThixContacts (the module `contacts`, contract 1.5.0) – also for ThixMail and ThixCalendar

The contacts live in the general sync of the app `contacts` (`Contacts/ContactsContract.cs`): collection `contacts` (key = a GUID, data =
`ContactData` as JSON: `prefix`, `givenName`, `middleName`, `familyName`, `suffix`, `nickname`, `displayName`, `company`, `department`,
`jobTitle`, `emails`/`phones`/`urls` = `[{ label, value }]`, `addresses` = `[{ label, street, postalCode, city, region, country }]`,
`birthday`/`anniversary` = `yyyy-MM-dd` or `--MM-dd`, `notes`, `photoId`, `categories`, `isFavorite`; fields an app does not know are kept)
and `contact-groups` (`ContactGroupData { name, members: [keys], notes }`). Labels: `ContactLabels` (`work`, `home`, `other`, `mobile`, `main`,
`work-fax`, `home-fax`, `pager`, `blog`, `profile`) or a label of the user's own. Pictures are not in the items:

| Route | What |
|---|---|
| `POST /api/v1/contacts/photos` | the bytes of a JPEG, PNG, GIF or WebP (≤ 5 MB) → `ContactPhotoInfo { id, contentType, size }`; `id` = SHA-256 hex, the value of `photoId` |
| `GET /api/v1/contacts/photos/{id}` | the bytes (404 when the account has none) |

**The grants (1.5.0).** The general sync takes `?app=<id>` (`SyncContract.AppParameter`; `MatCloudClient.GetChangesAsync(app, …)`, `WaitAsync(app, …)`,
`PushAsync(app, …)`): a device of `mail` reads and writes `contacts` and `calendar` of its account (and uploads pictures), a device of `calendar` reads
`contacts` (changes, wait, pictures – a push or an upload is 403). Every other combination is 403, also while the target app is switched off. The
grant covers the general sync and the picture routes only: the routes of ThixCalendar's own (`Connections`, `Disconnect`, `SyncNow`, `EventChange`)
stay for devices of `calendar` – a device of `mail` gets 403 there.

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
