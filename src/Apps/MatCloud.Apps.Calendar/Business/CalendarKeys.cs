using System.Reflection;
using MatCloud.Apps.Calendar.Data;
using MatCloud.Contract.Calendar;
using MatCloud.Core.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ThixCalendar.Core.Providers;

namespace MatCloud.Apps.Calendar.Business;

/// <summary>The Google web client of this MatCloud (the sign-in in the browser), as the administrator entered it.</summary>
public sealed record GoogleWebClient(string ClientId, string ClientSecret)
{
    public bool IsSet => ClientId.Length > 0 && ClientSecret.Length > 0;
}

/// <summary>
/// The secrets of the module (a singleton): protects the refresh tokens (Data Protection, the keys of MatCloud's data folder), knows ThixCalendar's
/// registrations (Microsoft: a public client; Google: the desktop client, its secret from the build or MATCLOUD_CALENDAR_GOOGLE_SECRET) and the Google web
/// client of this MatCloud (MATCLOUD_CALENDAR_GOOGLE_WEB_CLIENT_ID / _SECRET, else what the administrator entered: SystemSetting, the secret protected).
/// </summary>
public sealed class CalendarKeys(IDataProtectionProvider protection)
{
    public const string GoogleWebClientIdSetting = "Calendar.GoogleWebClientId";
    public const string GoogleWebClientSecretSetting = "Calendar.GoogleWebClientSecret";

    private readonly IDataProtector _tokens = protection.CreateProtector("MatCloud.Calendar.Tokens");
    private readonly IDataProtector _settings = protection.CreateProtector("MatCloud.Calendar.Settings");

    /// <summary>The secret of ThixCalendar's Google desktop client ("" when this build has none: handed-over Google sign-ins cannot be kept then).</summary>
    public string GoogleAppSecret { get; } = Environment.GetEnvironmentVariable("MATCLOUD_CALENDAR_GOOGLE_SECRET") is { Length: > 0 } secret
        ? secret
        : typeof(CalendarKeys).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
              .FirstOrDefault(a => a.Key == "ThixCalendarGoogleClientSecret")?.Value ?? string.Empty;

    public string Protect(string token) => token.Length == 0 ? string.Empty : _tokens.Protect(token);

    /// <summary>The token, or "" when it cannot be read (other keys): the account then needs a new sign-in.</summary>
    public string Unprotect(string protectedToken)
    {
        if (protectedToken.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return _tokens.Unprotect(protectedToken);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return string.Empty;
        }
    }

    /// <summary>The registration a refresh token of <paramref name="connection"/> is refreshed with.</summary>
    internal async Task<OAuthProfile?> ProfileAsync(AppDbContext db, CalendarConnection connection, CancellationToken ct) => connection.Kind switch
    {
        CalendarAccountKind.Microsoft => OAuthProfile.Microsoft(CalendarContract.MicrosoftClientId),
        CalendarAccountKind.Google when connection.Client == CalendarTokenClient.Web =>
            await GoogleWebClientAsync(db, ct) is { IsSet: true } web ? OAuthProfile.Google(web.ClientId, web.ClientSecret) : null,
        CalendarAccountKind.Google => GoogleAppSecret.Length > 0 ? OAuthProfile.Google(CalendarContract.GoogleClientId, GoogleAppSecret) : null,
        _ => null,
    };

    public async Task<GoogleWebClient> GoogleWebClientAsync(AppDbContext db, CancellationToken ct)
    {
        var id = Environment.GetEnvironmentVariable("MATCLOUD_CALENDAR_GOOGLE_WEB_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("MATCLOUD_CALENDAR_GOOGLE_WEB_CLIENT_SECRET");
        if (id is { Length: > 0 } && secret is { Length: > 0 })
        {
            return new GoogleWebClient(id, secret);
        }

        var settings = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == GoogleWebClientIdSetting || s.Key == GoogleWebClientSecretSetting)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var storedSecret = settings.GetValueOrDefault(GoogleWebClientSecretSetting) ?? string.Empty;
        try
        {
            storedSecret = storedSecret.Length == 0 ? string.Empty : _settings.Unprotect(storedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            storedSecret = string.Empty;
        }

        return new GoogleWebClient(settings.GetValueOrDefault(GoogleWebClientIdSetting) ?? string.Empty, storedSecret);
    }

    /// <summary>Whether the Google web client comes from the environment (then the page cannot change it).</summary>
    public static bool GoogleWebClientFromEnvironment =>
        Environment.GetEnvironmentVariable("MATCLOUD_CALENDAR_GOOGLE_WEB_CLIENT_ID") is { Length: > 0 };

    internal string ProtectSetting(string value) => value.Length == 0 ? string.Empty : _settings.Protect(value);
}
