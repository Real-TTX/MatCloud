using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Calendar.Business;

/// <summary>What only an administrator of this MatCloud sets for the calendar: the Google web client (the sign-in at Google in the browser).</summary>
public static class CalendarAdmin
{
    /// <summary>Saves the Google web client (an empty secret keeps the stored one; an empty id removes both).</summary>
    public static async Task<Outcome> SaveGoogleWebClientAsync(AppDbContext db, CalendarKeys keys, Actor actor, string? clientId, string? clientSecret, CancellationToken ct)
    {
        if (!actor.IsAdmin || actor.DeviceId is not null)
        {
            throw new NotAllowedException("Nur Administratoren.");
        }

        if (CalendarKeys.GoogleWebClientFromEnvironment)
        {
            return Outcome.Fail("Der Google-Zugang kommt aus der Konfiguration des Servers (MATCLOUD_CALENDAR_GOOGLE_WEB_CLIENT_ID) und lässt sich hier nicht ändern.", "GoogleClientId");
        }

        var id = (clientId ?? string.Empty).Trim();
        var secret = (clientSecret ?? string.Empty).Trim();
        if (id.Length > 0 && !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
        {
            return Outcome.Fail("Eine Google-Client-ID endet auf „.apps.googleusercontent.com“.", "GoogleClientId");
        }

        var current = await keys.GoogleWebClientAsync(db, ct);
        if (id.Length > 0 && secret.Length == 0 && current.ClientSecret.Length == 0)
        {
            return Outcome.Fail("Zur Client-ID gehört ihr Client-Secret.", "GoogleClientSecret");
        }

        db.ActingUserId = actor.UserId;
        await SetAsync(db, CalendarKeys.GoogleWebClientIdSetting, id, ct);
        if (id.Length == 0)
        {
            await SetAsync(db, CalendarKeys.GoogleWebClientSecretSetting, string.Empty, ct);
        }
        else if (secret.Length > 0)
        {
            await SetAsync(db, CalendarKeys.GoogleWebClientSecretSetting, keys.ProtectSetting(secret), ct);
        }

        await db.SaveChangesAsync(ct);
        return Outcome.Success();
    }

    private static async Task SetAsync(AppDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            if (value.Length > 0)
            {
                db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
            }

            return;
        }

        if (value.Length == 0)
        {
            db.SystemSettings.Remove(row);
        }
        else
        {
            row.Value = value;
        }
    }
}
