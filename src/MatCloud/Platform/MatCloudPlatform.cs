using System.Text.Json;
using MatCloud.Contract;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using MatCloud.Core.Web;
using MatPlatform;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Platform;

/// <summary>
/// MatCloud as an app of the platform contract (MatDesign docs/PLATFORM.md, contract 2) on the shared package MatPlatform (the copy in MatCloud.Core):
/// how a consumer such as Mattiverse shows it (<see cref="Describe"/>: tiles, the few settings, buttons), what it consumes (the sign-in of MatIdentity),
/// what it adds to a shell (the launcher MatCloud with its quick actions, two widgets for administrators), its values and what its buttons do.
/// The values come from the business layer.
/// </summary>
public sealed class MatCloudPlatform(AppDbContext db, MatPlatformSettings settings, ILogger<MatCloudPlatform> log)
    : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of MatCloud in the platform (and the default client id at MatIdentity), as in MatDesign <c>AppRegistry.cs</c>.</summary>
    public const string AppId = "matcloud";

    /// <summary>The logo as the shell shows it.</summary>
    public const string Icon = "/img/logo.svg";

    /// <summary>The MatApiDefinition without <c>MAT_PUBLIC_URL</c> (tests).</summary>
    public static void Describe(MatPlatformBuilder o) => Describe(o, null);

    /// <summary>The MatApiDefinition of MatCloud (<c>GET /api</c>); <paramref name="publicUrl"/> is <c>MAT_PUBLIC_URL</c>: while it is set, the field is fixed.</summary>
    public static void Describe(MatPlatformBuilder o, string? publicUrl)
    {
        var T = o.T;

        o.App(AppId, "MatCloud", T("Die Cloud der Thix-Apps: Zwischenablage, Notizen und Einstellungen auf allen Geräten",
            "The cloud of the Thix apps: clipboard, notes and settings on every device"), AppInfo.Version, Icon, Ui.Accent, "/");

        // What MatCloud uses from other apps: the sign-in, set by its variables.
        o.Consumes(MatCapabilities.IdentityOidc, optional: true);

        // What it adds to the home screen of a shell (Mattiverse).
        o.Launcher("matcloud", "MatCloud", Icon, "/");
        o.QuickAction("matcloud", T("Zwischenablage", "Clipboard"), "clock", url: "/Clip");
        o.QuickAction("matcloud", T("Apps und Daten", "Apps and data"), "grid", url: "/Apps");
        o.QuickAction("matcloud", T("Gerät verbinden", "Connect a device"), "link", url: "/Connect");
        o.Widget(new MatWidget("devices", T("Geräte", "Devices"), MatWidgetKinds.Tiles, "medium")
        {
            Items = ["devices", "devicesToday", "users"],
            Icon = "monitor",
            Url = "/Admin",
            Audience = MatAudience.Admin,
        });
        o.Widget(new MatWidget("apps", T("Apps an", "Apps on"), MatWidgetKinds.Stat)
        {
            Items = ["apps"],
            Icon = "grid",
            Url = "/Admin/Apps",
            Audience = MatAudience.Admin,
        });

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("users", "devices", "devicesToday", "apps", "syncItems", "syncSize", "lastContact"),
            MatSection.Actions("openApp", "openUsers", "openApps", "openConnections", "cleanUp"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Allgemein", "General"), "publicUrl"));

        o.Tile("users", T("Aktive Benutzer", "Active users"), icon: "users");
        o.Tile("devices", T("Geräte", "Devices"), icon: "monitor");
        o.Tile("devicesToday", T("Heute aktiv", "Active today"), icon: "activity");
        o.Tile("apps", T("Apps an", "Apps on"), icon: "grid");
        o.Tile("syncItems", T("Sync-Einträge", "Sync items"), icon: "database");
        o.Tile("syncSize", T("Sync-Daten", "Sync data"), "text", icon: "database");
        o.Tile("lastContact", T("Letzter Kontakt eines Geräts", "Last contact of a device"), "time", icon: "clock");

        var address = new MatField("publicUrl", T("Öffentliche Adresse", "Public address"), "url")
        {
            Max = 300,
            Placeholder = "https://cloud.example.com",
            Hint = T("Für den Link der Anmeldung im Browser; leer: die Adresse des Aufrufs.", "For the link of the sign-in in the browser; empty: the address of the request."),
        };
        o.Field(publicUrl is null ? address : address with
        {
            Fixed = true,
            Hint = T("Festgelegt durch MAT_PUBLIC_URL (Mattiverse); hier lässt sie sich nicht ändern.", "Set by MAT_PUBLIC_URL (Mattiverse); it cannot be changed here."),
        });

        o.LinkAction("openApp", T("Öffnen", "Open"), "external", "/");
        o.LinkAction("openUsers", T("Benutzer", "Users"), "users", "/Admin/Users");
        o.LinkAction("openApps", T("Apps", "Apps"), "grid", "/Admin/Apps");
        o.LinkAction("openConnections", T("Verbindungen", "Connections"), "plug", "/Admin/Connections");
        o.PostAction("cleanUp", T("Aufräumen", "Clean up"), "refresh",
            T("Beendete Sitzungen, alte Geräte-Anmeldungen, alte Löschmarken und abgelaufene Einträge der Apps jetzt entfernen?",
              "Remove ended sessions, old device sign-ins, old tombstones and expired entries of the apps now?"));

        o.Role("admin", T("Administrator", "Administrator"));
        o.Role("user", T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
    {
        if (!await db.Database.CanConnectAsync(ct))
        {
            return [new MatHealthCheck("database", "down", call.T("Die Datenbank ist nicht erreichbar.", "The database cannot be reached."))];
        }

        var checks = new List<MatHealthCheck> { new("database", "ok") };
        var snapshot = await SystemService.SnapshotAsync(db, ct);
        if (snapshot.Users > 0 && snapshot.Admins == 0)
        {
            checks.Add(new MatHealthCheck("administrators", "degraded", call.T("Kein aktiver Administrator.", "No active administrator.")));
        }

        return checks;
    }

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = await SystemService.SnapshotAsync(db, ct);
        return new Dictionary<string, MatStateValue>
        {
            ["users"] = new(s.ActiveUsers),
            ["devices"] = new(s.Devices),
            ["devicesToday"] = new(s.DevicesSeenToday),
            ["apps"] = new(s.AppsOn),
            ["syncItems"] = new(s.SyncItems),
            ["syncSize"] = new(Ui.Bytes(s.SyncBytes)),
            ["lastContact"] = new(s.LastDeviceContact is { } last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) : null),
        };
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
        => new Dictionary<string, object?> { ["publicUrl"] = settings.AppPublicUrl ?? await SystemService.PublicUrlAsync(db, ct) ?? string.Empty };

    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();
        foreach (var (key, value) in values)
        {
            if (key != "publicUrl" || settings.AppPublicUrl is not null)
            {
                errors[key] = call.T("Dieser Wert lässt sich hier nicht ändern.", "This value cannot be changed here.");
                continue;
            }

            var url = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var outcome = await SystemService.SetPublicUrlAsync(db, SystemActor(), url, ct);
            if (!outcome.Ok)
            {
                errors[key] = call.T("Eine vollständige Adresse mit http:// oder https://.", "A complete address with http:// or https://.");
            }
        }

        if (errors.Count == 0)
        {
            log.LogInformation("Settings changed through the platform API by {Caller}: {Keys}", call.Caller, string.Join(", ", values.Keys));
        }

        return errors;
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        if (id != "cleanUp")
        {
            return null;
        }

        var (sessions, links, tombstones, modules) = await SystemService.CleanUpAsync(db, ct);
        log.LogInformation("Clean-up through the platform API by {Caller}", call.Caller);
        return new MatActionResult(true, call.T(
            $"Aufgeräumt: {sessions} Sitzungen, {links} Geräte-Anmeldungen, {tombstones} Löschmarken, {modules} Einträge der Apps.",
            $"Cleaned up: {sessions} sessions, {links} device sign-ins, {tombstones} tombstones, {modules} entries of the apps."));
    }

    /// <summary>The platform API acts as an administrator (the key or a token of MatIdentity was checked by MatPlatform).</summary>
    private static Actor SystemActor() => new(0, IsAdmin: true);
}
