using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MatCloud.Apps.Calendar.Data;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;
using ThixCalendar.Core.Providers;

namespace MatCloud.Apps.Calendar.Business;

/// <summary>A sign-in at Microsoft with a code (the device authorization grant): the person enters <see cref="UserCode"/> at <see cref="VerificationUrl"/>.</summary>
public sealed record MicrosoftCodeStart(string Id, string UserCode, string VerificationUrl, DateTime ExpiresAt, int IntervalSeconds);

/// <summary>An account as the page "Konten" shows it: the item of the account and, when MatCloud keeps it in step, its connection.</summary>
public sealed record CalendarAccountView(string Key, CalendarAccountItem Account, CalendarCloudAccount? Cloud, bool HasConnection, int Calendars)
{
    public CalendarSyncMode Mode => Account.SyncMode ?? CalendarSyncMode.Computers;

    public bool IsOnline => Account.Kind is not CalendarAccountKind.Local;
}

/// <summary>
/// The accounts MatCloud keeps in step: a device hands a sign-in over, the browser connects one (Microsoft with a code, Google through this MatCloud's web
/// client, an ICS address), the user lets one go back to the computers. Every change wakes the worker (<see cref="CalendarWorker"/>).
/// </summary>
public static class CalendarConnections
{
    private const string MicrosoftDeviceCodeUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/devicecode";
    private const string MicrosoftTokenUrl = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
    private const string MicrosoftScope = "offline_access openid profile User.Read Calendars.ReadWrite";
    private const string GoogleAuthorizeUrl = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
    private const string GoogleScope = "openid email https://www.googleapis.com/auth/calendar";

    /// <summary>The sign-ins with a code that wait for the person (in memory: one server instance, like the long polls).</summary>
    private static readonly ConcurrentDictionary<string, PendingCode> Codes = new(StringComparer.Ordinal);

    /// <summary>The sign-ins at Google in the browser that wait for Google's answer (state → PKCE verifier, account).</summary>
    private static readonly ConcurrentDictionary<string, PendingGoogle> GoogleStates = new(StringComparer.Ordinal);

    private sealed record PendingCode(long UserId, string DeviceCode, DateTime ExpiresAt, string? Account);

    private sealed record PendingGoogle(long UserId, string Verifier, string RedirectUri, DateTime ExpiresAt, string? Account);

    // ===== the device hands over =====

    /// <summary>A device of ThixCalendar hands over the sign-in of an account (its SyncId must be in the accounts already).</summary>
    public static async Task<Outcome<CalendarCloudAccount>> HandOverAsync(AppDbContext db, CalendarKeys keys, Actor actor, CalendarHandover? handover, CancellationToken ct)
    {
        Require(actor);
        if (handover is null || string.IsNullOrWhiteSpace(handover.Account) || handover.Account.Length > 64)
        {
            return Outcome<CalendarCloudAccount>.Fail("Das Konto fehlt.", "account");
        }

        if (handover.Kind == CalendarAccountKind.Local)
        {
            return Outcome<CalendarCloudAccount>.Fail("Eigene Kalender sind schon in der Cloud.", "kind");
        }

        if (handover.Kind == CalendarAccountKind.Ics ? !Uri.TryCreate(handover.Url, UriKind.Absolute, out _) : handover.RefreshToken.Length is 0 or > 8_000)
        {
            return Outcome<CalendarCloudAccount>.Fail(handover.Kind == CalendarAccountKind.Ics ? "Die Adresse des Abos fehlt." : "Die Anmeldung fehlt.", "refreshToken");
        }

        if (handover.Kind == CalendarAccountKind.Google && keys.GoogleAppSecret.Length == 0)
        {
            return Outcome<CalendarCloudAccount>.Fail("Diese MatCloud kann Google-Konten von ThixCalendar nicht halten: ihr fehlt die Registrierung von ThixCalendar.", "kind");
        }

        var connection = await SaveAsync(db, keys, actor.UserId, handover.Account, handover.Kind, handover.UserName, handover.Url, handover.RefreshToken,
            CalendarTokenClient.App, ct);
        return Outcome<CalendarCloudAccount>.Success(new CalendarCloudAccount(connection.Kind, connection.UserName, connection.State, null));
    }

    /// <summary>MatCloud lets an account go (it goes back to the computers): the sign-in is forgotten, the items MatCloud wrote are removed.</summary>
    public static async Task<Outcome> DisconnectAsync(AppDbContext db, Actor actor, string? account, CancellationToken ct)
    {
        Require(actor);
        var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.UserId == actor.UserId && c.Account == account, ct);
        if (connection is null)
        {
            return Outcome.Success();
        }

        await CalendarCloudSync.RemoveAsync(db, connection, ct);
        return Outcome.Success();
    }

    /// <summary>Sync now: one account, or every account of the user (empty).</summary>
    public static async Task SyncNowAsync(AppDbContext db, Actor actor, string? account, CancellationToken ct)
    {
        Require(actor);
        await db.CalendarConnections
            .Where(c => c.UserId == actor.UserId && (string.IsNullOrEmpty(account) || c.Account == account) && c.State != CalendarCloudState.SignInRequired)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.NextSync, DateTime.UtcNow), ct);
        CalendarWorker.Wake();
    }

    // ===== the browser connects =====

    /// <summary>Starts a sign-in at Microsoft with a code (<paramref name="account"/>: sign an existing account in again).</summary>
    public static async Task<Outcome<MicrosoftCodeStart>> StartMicrosoftAsync(Actor actor, string? account, CancellationToken ct)
    {
        RequireBrowser(actor);
        var (status, body) = await ProviderHttp.PostFormAsync(MicrosoftDeviceCodeUrl,
            new Dictionary<string, string> { ["client_id"] = CalendarContract.MicrosoftClientId, ["scope"] = MicrosoftScope }, ct);
        if (status != HttpStatusCode.OK || body["device_code"]?.GetValue<string>() is not { Length: > 0 } deviceCode)
        {
            return Outcome<MicrosoftCodeStart>.Fail("Microsoft hat die Anmeldung mit Code abgelehnt" +
                (body["error_description"]?.GetValue<string>() is { Length: > 0 } d ? ": " + d.Split('\n', '\r')[0] : "."));
        }

        var id = Token();
        var expires = DateTime.UtcNow.AddSeconds(Seconds(body["expires_in"], 900));
        Codes[id] = new PendingCode(actor.UserId, deviceCode, expires, account);
        Forget();
        return Outcome<MicrosoftCodeStart>.Success(new MicrosoftCodeStart(id, body["user_code"]?.GetValue<string>() ?? string.Empty,
            body["verification_uri"]?.GetValue<string>() ?? "https://microsoft.com/devicelogin", expires, Seconds(body["interval"], 5)));
    }

    /// <summary>
    /// Asks Microsoft whether the code was confirmed: null while it waits; the SyncId of the account when it is connected (a new account, MatCloud keeps it
    /// in step); a failure when it was refused or ran out.
    /// </summary>
    public static async Task<Outcome<string?>> PollMicrosoftAsync(AppDbContext db, CalendarKeys keys, Actor actor, string id, CancellationToken ct)
    {
        RequireBrowser(actor);
        if (!Codes.TryGetValue(id, out var pending) || pending.UserId != actor.UserId)
        {
            return Outcome<string?>.Fail("Die Anmeldung ist nicht mehr da – bitte neu beginnen.");
        }

        var (status, body) = await ProviderHttp.PostFormAsync(MicrosoftTokenUrl, new Dictionary<string, string>
        {
            ["client_id"] = CalendarContract.MicrosoftClientId,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = pending.DeviceCode,
        }, ct);
        var error = body["error"]?.GetValue<string>();
        if (error is "authorization_pending" or "slow_down")
        {
            return Outcome<string?>.Success(null);
        }

        Codes.TryRemove(id, out _);
        if (status != HttpStatusCode.OK || body["refresh_token"]?.GetValue<string>() is not { Length: > 0 } refreshToken)
        {
            var description = body["error_description"]?.GetValue<string>() ?? string.Empty;
            return Outcome<string?>.Fail(error switch
            {
                "expired_token" => "Der Code ist abgelaufen – bitte neu beginnen.",
                "authorization_declined" or "access_denied" => "Die Anmeldung wurde abgelehnt.",
                _ when OAuthClient.NeedsAdminConsent(description) =>
                    "Deine Firma lässt ThixCalendar noch nicht zu. Ein Administrator von Microsoft 365 gibt ThixCalendar einmal frei: " +
                    OAuthClient.AdminConsentUrl(CalendarContract.MicrosoftClientId),
                _ => "Microsoft hat die Anmeldung abgelehnt" + (description.Length > 0 ? ": " + description.Split('\n', '\r')[0] : "."),
            });
        }

        var claims = OAuthClient.ReadIdToken(body["id_token"]?.GetValue<string>());
        var mail = claims["preferred_username"]?.GetValue<string>() ?? claims["email"]?.GetValue<string>() ?? string.Empty;
        var name = claims["name"]?.GetValue<string>() ?? mail;
        var key = await ConnectAsync(db, keys, actor, pending.Account, CalendarAccountKind.Microsoft, mail, name, string.Empty, refreshToken, CalendarTokenClient.App, ct);
        return Outcome<string?>.Success(key);
    }

    /// <summary>The address of Google's sign-in for the browser (null: this MatCloud has no Google web client).</summary>
    public static async Task<string?> StartGoogleAsync(AppDbContext db, CalendarKeys keys, Actor actor, string redirectUri, string? account, CancellationToken ct)
    {
        RequireBrowser(actor);
        var client = await keys.GoogleWebClientAsync(db, ct);
        if (!client.IsSet)
        {
            return null;
        }

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = Token();
        GoogleStates[state] = new PendingGoogle(actor.UserId, verifier, redirectUri, DateTime.UtcNow.AddMinutes(15), account);
        Forget();
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["scope"] = GoogleScope,
            ["state"] = state,
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256",
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
        };
        return GoogleAuthorizeUrl + "?" + string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
    }

    /// <summary>Google's answer in the browser: the account is connected (its SyncId) or what went wrong.</summary>
    public static async Task<Outcome<string>> FinishGoogleAsync(AppDbContext db, CalendarKeys keys, Actor actor, string? state, string? code, string? error, CancellationToken ct)
    {
        RequireBrowser(actor);
        if (state is null || !GoogleStates.TryRemove(state, out var pending) || pending.UserId != actor.UserId || pending.ExpiresAt < DateTime.UtcNow)
        {
            return Outcome<string>.Fail("Die Anmeldung bei Google passt nicht zu dieser Seite – bitte neu beginnen.");
        }

        if (error is not null || code is null)
        {
            return Outcome<string>.Fail(error == "access_denied" ? "Die Anmeldung wurde abgebrochen oder der Zugriff nicht erlaubt." : "Google hat die Anmeldung abgelehnt.");
        }

        var client = await keys.GoogleWebClientAsync(db, ct);
        var (status, body) = await ProviderHttp.PostFormAsync(GoogleTokenUrl, new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri,
            ["code_verifier"] = pending.Verifier,
        }, ct);
        if (status != HttpStatusCode.OK || body["refresh_token"]?.GetValue<string>() is not { Length: > 0 } refreshToken)
        {
            return Outcome<string>.Fail("Google hat keine dauerhafte Anmeldung ausgegeben – bitte noch einmal versuchen.");
        }

        var claims = OAuthClient.ReadIdToken(body["id_token"]?.GetValue<string>());
        var mail = claims["email"]?.GetValue<string>() ?? string.Empty;
        var key = await ConnectAsync(db, keys, actor, pending.Account, CalendarAccountKind.Google, mail, mail, string.Empty, refreshToken, CalendarTokenClient.Web, ct);
        return Outcome<string>.Success(key);
    }

    /// <summary>Subscribes an ICS address; MatCloud reads it.</summary>
    public static async Task<Outcome<string>> AddIcsAsync(AppDbContext db, CalendarKeys keys, Actor actor, string? name, string? url, CancellationToken ct)
    {
        RequireBrowser(actor);
        var normalized = (url ?? string.Empty).Trim();
        if (normalized.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized["webcal://".Length..];
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var address) || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            return Outcome<string>.Fail("Eine Adresse beginnt mit https://, http:// oder webcal://.", "Url");
        }

        var title = string.IsNullOrWhiteSpace(name) ? address.Host : name.Trim();
        if (title.Length > 100)
        {
            return Outcome<string>.Fail("Der Name hat höchstens 100 Zeichen.", "Name");
        }

        var key = await ConnectAsync(db, keys, actor, null, CalendarAccountKind.Ics, string.Empty, title, normalized, string.Empty, CalendarTokenClient.None, ct);
        return Outcome<string>.Success(key);
    }

    /// <summary>The accounts of the user for the page, the own calendars first.</summary>
    public static async Task<IReadOnlyList<CalendarAccountView>> ListAsync(AppDbContext db, Actor actor, CancellationToken ct)
    {
        RequireBrowser(actor);
        var accounts = await CalendarItems.ReadAsync<CalendarAccountItem>(db, actor.UserId, CalendarContract.Accounts, ct);
        var states = (await CalendarItems.ReadAsync<CalendarCloudAccount>(db, actor.UserId, CalendarContract.CloudAccounts, ct)).ToDictionary(r => r.Key, r => r.Data);
        var connected = (await db.CalendarConnections.AsNoTracking().Where(c => c.UserId == actor.UserId).Select(c => c.Account).ToListAsync(ct)).ToHashSet();
        var calendars = await CalendarItems.Live(db, actor.UserId, CalendarContract.Calendars).Select(i => i.Key).ToListAsync(ct);
        var cloudCalendars = await CalendarItems.Live(db, actor.UserId, CalendarContract.CloudCalendars).Select(i => i.Key).ToListAsync(ct);
        int Count(string key) => calendars.Concat(cloudCalendars).Where(k => k.StartsWith(key + "/", StringComparison.Ordinal)).Distinct().Count();
        return accounts
            .Select(a => new CalendarAccountView(a.Key, a.Data, states.GetValueOrDefault(a.Key), connected.Contains(a.Key), Count(a.Key)))
            .OrderBy(a => a.Account.Kind == CalendarAccountKind.Local ? 0 : 1).ThenBy(a => a.Account.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Changes the sync mode of an account in the browser. To the computers: MatCloud lets it go. To MatCloud: an ICS account is connected at once; an online
    /// account needs a sign-in here (the page then offers it) or the hand-over of a computer.
    /// </summary>
    public static async Task<Outcome> SetModeAsync(AppDbContext db, CalendarKeys keys, Actor actor, string key, CalendarSyncMode mode, CancellationToken ct)
    {
        RequireBrowser(actor);
        var account = await CalendarItems.FindAsync<CalendarAccountItem>(db, actor.UserId, CalendarContract.Accounts, key, ct);
        if (account is null || account.Data.Kind == CalendarAccountKind.Local)
        {
            return Outcome.Missing();
        }

        if ((account.Data.SyncMode ?? CalendarSyncMode.Computers) != mode)
        {
            await CalendarItems.PushAsync(db, actor, [CalendarItems.Item(CalendarContract.Accounts, key, account.Data with { SyncMode = mode })], ct);
        }

        var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.UserId == actor.UserId && c.Account == key, ct);
        if (mode == CalendarSyncMode.Computers && connection is not null)
        {
            await CalendarCloudSync.RemoveAsync(db, connection, ct);
        }
        else if (mode == CalendarSyncMode.Cloud && connection is null && account.Data.Kind == CalendarAccountKind.Ics)
        {
            await SaveAsync(db, keys, actor.UserId, key, CalendarAccountKind.Ics, string.Empty, account.Data.Url, string.Empty, CalendarTokenClient.None, ct);
        }

        return Outcome.Success();
    }

    /// <summary>Removes an account everywhere (its calendars and the choices about them go too; the own events of a "Eigene Kalender" account stay as they are).</summary>
    public static async Task<Outcome> DeleteAccountAsync(AppDbContext db, Actor actor, string key, CancellationToken ct)
    {
        RequireBrowser(actor);
        var account = await CalendarItems.FindAsync<CalendarAccountItem>(db, actor.UserId, CalendarContract.Accounts, key, ct);
        if (account is null)
        {
            return Outcome.Missing();
        }

        var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.UserId == actor.UserId && c.Account == key, ct);
        if (connection is not null)
        {
            await CalendarCloudSync.RemoveAsync(db, connection, ct);
        }

        var calendars = await CalendarItems.Live(db, actor.UserId, CalendarContract.Calendars).Where(i => i.Key.StartsWith(key + "/")).Select(i => i.Key).ToListAsync(ct);
        await CalendarItems.PushAsync(db, actor,
            [CalendarItems.Deleted(CalendarContract.Accounts, key), .. calendars.Select(c => CalendarItems.Deleted(CalendarContract.Calendars, c))], ct);
        return Outcome.Success();
    }

    // ===== shared =====

    /// <summary>Creates the account item (or signs an existing one in again), keeps the sign-in and wakes the worker.</summary>
    private static async Task<string> ConnectAsync(AppDbContext db, CalendarKeys keys, Actor actor, string? existing, CalendarAccountKind kind, string userName,
        string name, string url, string refreshToken, CalendarTokenClient client, CancellationToken ct)
    {
        var key = existing;
        var account = key is null ? null : await CalendarItems.FindAsync<CalendarAccountItem>(db, actor.UserId, CalendarContract.Accounts, key, ct);
        if (account is null)
        {
            key = CalendarItems.NewKey();
            await CalendarItems.PushAsync(db, actor,
                [CalendarItems.Item(CalendarContract.Accounts, key, new CalendarAccountItem(kind, name.Length > 0 ? name : userName, userName, url, true, CalendarSyncMode.Cloud))], ct);
        }
        else if (account.Data.SyncMode != CalendarSyncMode.Cloud)
        {
            await CalendarItems.PushAsync(db, actor, [CalendarItems.Item(CalendarContract.Accounts, key!, account.Data with { SyncMode = CalendarSyncMode.Cloud })], ct);
        }

        await SaveAsync(db, keys, actor.UserId, key!, kind, userName, url, refreshToken, client, ct);
        return key!;
    }

    private static async Task<CalendarConnection> SaveAsync(AppDbContext db, CalendarKeys keys, long userId, string account, CalendarAccountKind kind, string userName,
        string url, string refreshToken, CalendarTokenClient client, CancellationToken ct)
    {
        db.ActingUserId = userId;
        var connection = await db.CalendarConnections.FirstOrDefaultAsync(c => c.UserId == userId && c.Account == account, ct);
        if (connection is null)
        {
            connection = new CalendarConnection { UserId = userId, Account = account };
            db.CalendarConnections.Add(connection);
        }

        connection.Kind = kind;
        connection.UserName = userName.Length > 320 ? userName[..320] : userName;
        connection.Url = url;
        connection.ProtectedToken = keys.Protect(refreshToken);
        connection.Client = client;
        connection.State = CalendarCloudState.Connecting;
        connection.Problem = string.Empty;
        connection.Failures = 0;
        connection.NextSync = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await CalendarCloudSync.WriteStateAsync(db, connection, ct);
        CalendarWorker.Wake();
        return connection;
    }

    /// <summary>Only the account itself (a device of ThixCalendar or its browser).</summary>
    private static void Require(Actor actor)
    {
        if (!actor.MayUse(CalendarItems.App))
        {
            throw new NotAllowedException("Dieses Gerät darf diese Daten nicht lesen.");
        }
    }

    private static void RequireBrowser(Actor actor)
    {
        if (actor.DeviceId is not null)
        {
            throw new NotAllowedException("Nur im Browser.");
        }
    }

    private static void Forget()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, code) in Codes.Where(c => c.Value.ExpiresAt < now).ToList())
        {
            Codes.TryRemove(id, out _);
        }

        foreach (var (state, google) in GoogleStates.Where(g => g.Value.ExpiresAt < now).ToList())
        {
            GoogleStates.TryRemove(state, out _);
        }
    }

    private static int Seconds(System.Text.Json.Nodes.JsonNode? node, int fallback) =>
        node is null ? fallback : node.GetValueKind() == System.Text.Json.JsonValueKind.Number ? node.GetValue<int>() : int.TryParse(node.GetValue<string>(), out var s) ? s : fallback;

    private static string Token() => Base64Url(RandomNumberGenerator.GetBytes(24));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
