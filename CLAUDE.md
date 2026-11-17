# CLAUDE.md

Guidance for working in this repository. Keep it in sync with the code: update it whenever architecture, interfaces or conventions change.

## What MatCloud is

**The cloud of the Thix apps** (the desktop apps for Windows and macOS: ThixClip, ThixNote, ThixCalendar, ThixTunnel, ThixLauncher, ThixFred). Every app signs its
devices in here and keeps its data the same on every PC and Mac of an account. Two kinds of apps:

- **Apps with a module** (`src/Apps/*`, an `ICloudModule`): their own tables, their own API for their devices and their own pages in the browser.
  The first is **ThixClip** (`MatCloud.Apps.Clip`): the clipboard history, searchable (also the text recognised in pictures), live in the browser.
  The second is **ThixNote** (`MatCloud.Apps.Note`), a module of another kind: a *web client of the general sync* (`UsesGeneralSync`): ThixNote's
  devices keep syncing through `/api/v1/sync`, and the pages read and write the same items (list, folders, Markdown view, write, rename, delete;
  protected notes are decrypted and encrypted only in the browser with WebCrypto, in ThixNote's format).
- **Apps in the general sync** (everything else, ThixNote first): no code here. They keep items of their own choosing (collection + key + data) and
  MatCloud keeps them in step (last writer wins, tombstones, revisions, long poll).

MatCloud is an app of the **Mat family** (MatDesign docs/PLATFORM.md, contract 2, MatPlatform 2.4): `/api` describes it to a broker (Mattiverse), it
consumes `identity.oidc` (sign-in through **MatIdentity**, optional), contributes the launcher MatCloud and two admin widgets, and works without any of
the platform variables. Port **4950** (pgAdmin 4951, local Postgres 4952). UI language German; code, comments and identifiers English.
Accent sky blue `#0369a1` (white text 5.9:1). It replaced ThixClip.Cloud (the server that lived in the ThixClip repository); there was no data migration.

Stack: ASP.NET Core on **.NET 10**, C#, **Razor Pages** + tag helpers, EF Core + **PostgreSQL 17**, **MatUi 0.11.0** (copied into `wwwroot/matui`,
never edited by hand), **MatPlatform 2.4.0** (copied into `src/MatCloud.Core/MatPlatform`, never edited here), SkiaSharp (pictures of the clipboard),
plain JavaScript, Docker-first.

## Build / run / test

- `dotnet build MatCloud.slnx` – expected: 0 warnings, 0 errors.
- `dotnet test tests/MatCloud.Tests` – unit tests always; the business layer, the API over HTTP (`WebApplicationFactory`) and the platform contract
  run when **`MATCLOUD_TEST_DB`** names a PostgreSQL with the right to create databases (each test makes and drops its own database, migrated like
  the real one). With the local stack: `$env:MATCLOUD_TEST_DB = "Host=localhost;Port=4952;Database=postgres;Username=matcloud;Password=matcloud"`.
- **Run it: always rebuild and redeploy the stack:** `./scripts/redeploy.ps1` (`-Clean` drops the volumes: an empty database, the setup assistant comes
  again). App http://localhost:4950, pgAdmin :4951, Postgres :4952. `dotnet run --project src/MatCloud` (Development) uses the Postgres of the local stack.
- **Browser checks:** `cd tests/e2e && npm install && node run.js http://localhost:4950` (Playwright with the installed Edge): setup, a second account,
  ThixClip devices over the API (push, pictures, merge, live list, OCR search, tombstones), ThixNote through the sign-in in the browser and the general
  sync, the overview, the apps, switching an app off (403) and on, removing a device (401), 390 px light and dark, offline, the forced password change.
  Screenshots in `tests/e2e/shots` – LOOK at them.
- **Migrations:** `dotnet ef migrations add <Name> -p src/MatCloud -o Data/Migrations` (local tool in `dotnet-tools.json`, `dotnet tool restore` once).
  One migration history for the platform and every module: the migrations live in the host (`AppDatabase.MigrationsAssembly`), the design-time factory
  installs the modules first. Applied at start-up.
- **Icons:** `node tools/render-icons.js` (after `npm install` in tests/e2e) renders `branding/matcloud.svg` and `branding/matcloud-maskable.svg` into
  `branding/png` and `src/MatCloud/wwwroot/img` (logo, favicon, PWA icons).
- **The contract for the apps** is `src/MatCloud.Contract`; the apps COPY it with `node tools/contract-copy.js <folder of the app's project>`
  (`<folder>/MatCloud/**` + `matcloud.version`, the apps' tests hold the copy to it). See docs/CONTRACT.md.
- **MatPlatform:** copy again from MatDesign with `node ../MatDesign/tools/matplatform-copy.js src/MatCloud.Core`. **MatUi:** take a release of MatDesign
  (`matui.css` joined as its first line says, `matui.js`, `matui.boot.js`, `matui-icons.svg`) into `src/MatCloud/wwwroot/matui`.

### Local test accounts (local stack only)

| Account | User name | Password | Note |
|---|---|---|---|
| Administrator | `admin` | `MatCloud-Admin-2026` | created by the setup assistant (tests/e2e) |
| User | `anna` | `Anna-Test-2026` | created by the administrator; must choose a new password at the first sign-in |

## Map

```
src/MatCloud.Contract       THE contract with the apps (copied into them, no dependencies): CloudContract (protocol 1, header X-MatCloud-Protocol, routes of
                            the sign-in and the account, JSON options), CloudApps (the known app ids: clip, note, calendar, tunnel, launcher, fred), Devices.cs
                            (password sign-in, sign-in in the browser: DeviceLinkRequest/Start/PollResponse, AccountInfo), Sync.cs (the general sync:
                            SyncContract, SyncItemDto, SyncChangeSet, SyncPushRequest/Result), Clip/ (ClipContract, ClipDto ..., ClearRange), MatCloudClient
                            (HttpClient of all of it, CloudException)
src/MatCloud.Core           (Razor class library) what everything stands on:
  Data/                     Entities (User, UserSession, Device (per app), DeviceLink, AppSetting, SystemSetting, SyncItem), AppDbContext (audit columns,
                            soft delete, the model of every module), AppDatabase (Npgsql, migrations in the host)
  Modules/                  ICloudModule (what a module is), CloudModules (the installed ones), AppUsage
  Business/                 THE business layer of the platform, static classes taking the AppDbContext and the Actor (person or device of one app):
                            UserService (setup, sign-in, administration), SessionService (+ MatIdentity sid / ID token), ExternalSignInService (MatIdentity),
                            AccountService, DeviceService (devices per app, the token), DeviceLinkService (the sign-in in the browser), AppService (which
                            apps are on, usage, delete an app's data), SyncService (the general sync), Revisions (+ RevisionSignals: streams per app and
                            account, advisory locks, long polls), SystemService (public address, numbers, housekeeping), Passwords, Actor/Outcome
  Api/DeviceEndpoints       /api/v1: sign-in, link, account, the general sync; ForApp(app, id) = the guarded group for a module's device routes
  Web/                      CurrentUser, CookieSignIn, AppPageModel, Menu (menus of the platform and the modules), Ui (icons, logo, bytes), ListModels
                            (toolbar, pager), Dates, Assets (sprites), AppInfo (version), AppBrands (name, word mark, logo, favicon, data-app of every
                            Thix app: the pages of a module wear the brand of their app), AppManifests (/<Home>/manifest.webmanifest per module)
  Controls/                 one code base per control: <mc-field>, <mc-buttons>, <mc-table>, <mc-tabs>/<mc-tab>
  Pages/Shared/             the layout (menu switcher with one menu per module), _LayoutAuth, _Head, _Menu{Home,Account,Admin}, _Toolbar, _Pager,
                            _FormSummary, _Empty – used by the host's and the modules' pages
  MatPlatform/              copy of MatDesign's MatPlatform 2.4.0 (never edit here)
src/Apps/MatCloud.Apps.Clip (Razor class library) the module ThixClip: ClipModule; Data/ClipModel (ClipEntry, ClipImage, ClipAccount (retention),
                            sequence ClipRevision; db.Clips etc. as extension members); Business/ (ClipService: browser, ClipSyncService: devices,
                            ClipSettingsService: retention, ClipContent: hash/links, ImageTools: SkiaSharp); Api/ClipEndpoints (/api/v1/clip for ThixClip
                            devices, /live/clip for the pages); Pages/Clip (Verlauf, Eintrag, Einstellungen), Pages/Shared/_ClipMenu; wwwroot/js/clip-live.js
src/Apps/MatCloud.Apps.Note (Razor class library) the module ThixNote, no tables: NoteModule (UsesGeneralSync); Business/NoteService (the notes on the
                            items of the general sync in ThixNote's format: collection notes {path, content | bytes}, hash = SHA-256 hex of the text or
                            the bytes, collection folders; stale texts refused by the hash the page opened with); Api/NoteEndpoints (/live/note);
                            Business/NoteMarkdown (Markdig without HTML, the first heading that repeats the title left out; plain lines for cards);
                            Pages/Note (Index: cards, View: the note as a page, Edit: the editor), Pages/Shared/_NoteMenu (folders); wwwroot/js
                            note-live.js, note-editor.js (toolbar, keys, lists that continue, preview from POST /live/note/preview, Strg+S, a warning
                            before unsaved text is left), note-view.js (E edits), note-crypt.js ("QNSEC1" | iterations | salt | nonce | tag | ciphertext, PBKDF2-SHA256 + AES-256-GCM, checked before it is sent)
src/MatCloud                the host: Program (composition: modules, MatPlatform, MatIdentity, cookie, rate limit, body limit), Modules.cs (the list of
                            modules), Infrastructure (health check, migrations, design-time factory, setup redirect, forced password change, security
                            headers, housekeeping every hour, SeedAdminAsync: MATCLOUD_ADMIN_PASSWORD makes the first administrator), Platform/ (MatCloudPlatform: the MatApiDefinition, state, configure, actions, health;
                            MatCloudIdentityUsers), Data/Migrations, Pages (Übersicht, Apps und Daten (+ Detail), Gerät verbinden, Account/*, Admin/
                            (System, Benutzer, Apps, Verbindungen), About, Error), wwwroot (matui, css/app.css, js, sw.js, manifest, img)
tests/MatCloud.Tests        unit tests; business layer, API and platform against PostgreSQL (MATCLOUD_TEST_DB)
tests/e2e                   browser checks (see above)
tools/                      contract-copy.js, render-icons.js
docs/                       ARCHITECTURE.md (platform, modules, sync), CONTRACT.md (the protocol for the apps), MODULES.md (how to write a module)
```

## Conventions

- **Microsoft C# coding conventions**, file-scoped namespaces, `_camelCase` fields, human-readable names, early returns.
- **Business layer = static classes** (platform and modules alike): they take the `AppDbContext` and the `Actor` and check what the actor may do (an
  administrator has NO access to the data of others; a device only to the data of its app: `Actor.MayUse(app)`). Pages call the business layer only,
  never the database. Errors are `Outcome.Fail(message, field)`; pages show them at the field.
- **Relational rules** (PostgreSQL): tables PascalCase and singular, key `Id` BIGINT, `CreateDate`, `CreateUserId`, `UpdateDate`, `UpdateUserId`
  (filled by AppDbContext), `UpdateState` (0 deleted, 1 created, 2 updated) for soft deletes and tombstones, a `Token` uuid where a key is a secret
  (UserSession, Device, DeviceLink).
- **Sync rules:** one revision sequence per kind of data (`SyncRevision` for the general sync, `ClipRevision` for the clips); every change of a stream
  (an app of an account) runs under its advisory lock (`Revisions.ChangeAsync`), so "everything above N" never misses a change; the long polls are woken
  after the commit (in memory: one server instance). General sync: last writer wins by `UpdatedAt`, a delete too, a later write revives. Clips:
  deleting wins, the same content is merged. Never log clipboard contents, note contents, passwords or tokens.
- **Modules:** everything of an app lives in its project under `src/Apps` (tables, business, routes, pages, scripts, its menu partial); the platform
  knows no module by name (only `Modules.cs` lists them). Device routes go through `DeviceEndpoints.ForApp`. See docs/MODULES.md.
- **The contract** (`src/MatCloud.Contract`) changes only backwards-compatibly (new optional fields) or raises `CloudContract.ProtocolVersion`; after
  a change the apps copy it again. A new app id goes into `CloudApps` first.
- **UI rules (MatUi):** toolbar above lists, actions below, left-aligned, positive to negative, delete with a gap; tabs for areas of one item; colours
  only from the tokens (`app.css` sets only the accents: MatCloud `#0369a1`, ThixClip `#2f6fed`, ThixNote `#c53d30`); icons of the sprite (MatCloud's
  own, for example the marks of the editor, in `wwwroot/img/app-icons.svg`); one `h1`; 390 px without sideways scrolling; texts German.
- **Every app looks like itself:** the pages of a module (`/Clip`, `/Note`) wear the brand of their app (`AppBrands`): logo and word mark above the
  menu with "MatCloud" below it, `<html data-app="thixnote">` (the accent), the tab title "Seite · ThixNote", favicon and manifest. The logos are
  copies from the apps (`wwwroot/img/apps`, from `ThixClip/branding`, `ThixNote/assets`, …); a new app gets its entry in `AppBrands` and its logo there.
- Edit text files with the editor tools (Windows PowerShell 5.1 `Get-Content`/`Set-Content` double-encode umlauts).

## Versioning, branches, CI

- `Directory.Build.props` holds major/minor. CI sets `Channel`, `BuildNumber` (GitHub run number), `BuildDate`: release `0.1.42-20261117` (main),
  nightly `nightly-43-20261117` (dev), local `local-20261117` (everything else).
- Branches: `main` = release, `dev` = nightly. **Commits are authored by Matthias Schmoldt only – no co-author trailers, no other names, no
  "generated with" lines.**
- `.github/workflows/cloud.yml`: tests (with a Postgres service) and the multi-arch image `ghcr.io/real-ttx/matcloud` (`:latest`/`:release`/`:<version>`
  from main, `:nightly`/`:<version>` from dev). Compose files: `docker-compose.yml` (release), `docker-compose.dev.yml` (nightly + pgAdmin),
  `docker-compose.local.yml` (build + pgAdmin + Postgres on 4952), no variables, one stack each (services `web`, `db`, volumes `web-data`, `db-data`).
- MatCloud is registered in MatDesign (`AppRegistry.cs`, `matui.apps.css`: id `matcloud`, port 4950, accent `#0369a1`) and has a catalog entry in
  Mattiverse (`src/Mattiverse.Core/Catalog/apps/matcloud.yml`).
