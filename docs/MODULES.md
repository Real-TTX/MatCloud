# App modules

An app gets a **module** when MatCloud should do more than keep its data the same on every device: pages in the browser, an API of its own, rules of
its own (ThixClip: a searchable history, pictures with previews, merging the same content, retention). Every other app uses the **general sync** and
needs no code here (docs/CONTRACT.md).

Modules are projects in this repository, built in at compile time (not loaded at run time): one model of the database, one migration history, one image.

## What a module is

A Razor class library under `src/Apps/MatCloud.Apps.<Name>` that references `MatCloud.Core` and implements `ICloudModule` (`src/MatCloud.Core/Modules`):

| Member | What |
|---|---|
| `AppId` | the app of the contract (`CloudApps.Clip`); the app id must be in `CloudApps` first |
| `Home`, `Icon`, `MenuTitle`, `MenuPartial` | its pages in the browser (`/Clip`), its icon of the sprite, the title of its menu in the switcher, the partial that draws its menu entries (in the module's `Pages/Shared`) |
| `MaxRequestBodyBytes` | the largest body it takes (pictures), or null. It raises the limit of the whole server, so keep it small; a route that takes more raises its own (the Drive: a piece of 16 MB for the server, 64 MB on the routes that take a whole file) |
| `UsesGeneralSync` | true for a module that is a web client of the general sync (ThixNote: its devices keep `/api/v1/sync`, its pages read and write the same items through `SyncService`, no tables); false (the default) for a module with an API of its own (ThixClip) |
| `GuestApps` | the grant to other apps: their devices may call the module's device API and reach its data of their own account (the Drive: `write`, `calc`, `show`). Empty by default. `ForApp` lets them through while the module's app is on; the business layer checks it with `Actor.MayUse(AppId)`, which honours the grant |
| `AddServices` | services, if any (the business layer is static) |
| `ConfigureModel` | its tables and sequences in the one `AppDbContext` (relational rules: PascalCase, `Id`, audit columns, `UpdateState`) |
| `MapEndpoints` | its device routes (`DeviceEndpoints.ForApp(routes, AppId)`: protocol header, valid token, a device of this app, the app switched on) under `/api/v1/<app>/…`, and what its pages fetch under `/live/<app>/…` (`RequireAuthorization`) |
| `MaintainAsync` | the hourly housekeeping (retention …) |
| `DeleteDataAsync` | removes everything an account keeps in the app (account deleted, or "Daten in der Cloud löschen") |
| `UsageAsync`, `TotalUsageAsync` | items, size and last change for one account / for all (the overview, the apps, the administration) |

## The look of the app

The pages under `Home` wear the brand of their app, not MatCloud's (`MatCloud.Core/Web/AppBrands.cs`): the app's logo and word mark above the menu
("Thix**Note**", "MatCloud" small below it, a link back to the overview), its logo and name in the switcher, `<html data-app="thixnote">` – so buttons,
the current menu item and links take the app's accent (`app.css`, `[data-app="thixnote"] { --mat-app-accent: … }`) –, "Notizen · ThixNote" as the title
of the tab, its favicon, and `Home/manifest.webmanifest` (`AppManifests`), so that the app can be installed on its own (it opens at `/Note`). A new
module adds its app to `AppBrands`, copies its logos from the app's repository to `src/MatCloud/wwwroot/img/apps` and sets its accent in `app.css`
(white text on it at least 4.5:1; ThixNote's `#DB4C3F` reaches 4.1:1, so the web uses `#C53D30`).

## Steps for a new module

1. Add the app id to `CloudApps` in `src/MatCloud.Contract` (and copy the contract into the app).
2. `src/Apps/MatCloud.Apps.<Name>/MatCloud.Apps.<Name>.csproj` (Razor SDK, `FrameworkReference Microsoft.AspNetCore.App`, `<MatCloudWebUsings>true</MatCloudWebUsings>`,
   a reference to `MatCloud.Core`); `Pages/_ViewImports.cshtml` (`@addTagHelper *, MatCloud.Core`), `Pages/_ViewStart.cshtml` (`Layout = "_Layout"`).
3. The module class, its data (`Data/`, extension members `db.<Things>` like `ClipModel`), its business layer (static, `Actor.MayUse(AppId)`), its routes,
   its pages (`@page "/<Name>/…"`, `AppPageModel`) and its menu partial (`data-nav-panel="<app id>"`).
4. Reference it in `src/MatCloud/MatCloud.csproj` and add it to `InstalledModules.Install()` (`src/MatCloud/Modules.cs`).
5. `dotnet ef migrations add Add<Name> -p src/MatCloud -o Data/Migrations`.
6. Tests in `tests/MatCloud.Tests` (business layer against PostgreSQL, the routes over HTTP), browser checks in `tests/e2e/run.js`, the module's line in
   README and CLAUDE.md.
7. In the app: its own client for its routes on top of `MatCloudClient.SendAsync`.

An app that moves from the general sync to a module keeps its id; the module's `DeleteDataAsync`/`UsageAsync` take over, and the old items of the
general sync are no longer read (a module has no general sync): migrate them in the module's first migration if they matter.
