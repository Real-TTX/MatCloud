using MatCloud.Apps.Clip.Data;
using MatCloud.Contract;
using MatCloud.Contract.Clip;
using MatCloud.Core.Business;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Clip.Business;

/// <summary>What holds for the clips of an account: how long they are kept (for every device of the account; a shorter time removes older clips at once).</summary>
public static class ClipSettingsService
{
    /// <summary>How long the account keeps its clips, in days (0 = forever).</summary>
    public static async Task<int> RetentionDaysAsync(AppDbContext db, long userId, CancellationToken ct = default)
        => await db.ClipAccounts.AsNoTracking().Where(a => a.UserId == userId).Select(a => (int?)a.RetentionDays).FirstOrDefaultAsync(ct)
           ?? ClipContract.DefaultRetentionDays;

    public static async Task<ClipSettings> GetAsync(AppDbContext db, Actor actor, CancellationToken ct = default)
    {
        Require(actor);
        return new ClipSettings(await RetentionDaysAsync(db, actor.UserId, ct));
    }

    /// <summary>Changes how long clips are kept (the browser or a device of ThixClip). A shorter time removes what is older at once, on every device too.</summary>
    public static async Task<Outcome<ClipSettings>> SetRetentionAsync(AppDbContext db, Actor actor, int retentionDays, CancellationToken ct = default)
    {
        Require(actor);
        if (!ClipContract.IsValidRetention(retentionDays))
        {
            return Outcome<ClipSettings>.Fail(
                "Diese Speicherdauer gibt es nicht. Erlaubt: " + string.Join(", ", ClipContract.RetentionChoices) + " Tage (0 = unbegrenzt).",
                actor.DeviceId is null ? "RetentionDays" : "retentionDays");
        }

        var row = await db.ClipAccounts.FirstOrDefaultAsync(a => a.UserId == actor.UserId, ct);
        if (row is null)
        {
            row = new ClipAccount { UserId = actor.UserId };
            db.ClipAccounts.Add(row);
        }

        row.RetentionDays = retentionDays;
        await db.SaveChangesAsync(ct);
        await ClipService.ApplyRetentionAsync(db, actor.UserId, ct);
        return Outcome<ClipSettings>.Success(new ClipSettings(retentionDays));
    }

    public static string RetentionName(int days) => days switch
    {
        0 => "Unbegrenzt",
        1 => "1 Tag",
        7 => "7 Tage",
        30 => "30 Tage",
        90 => "90 Tage",
        365 => "1 Jahr",
        _ => $"{days} Tage",
    };

    private static void Require(Actor actor)
    {
        if (!actor.MayUse(CloudApps.Clip))
        {
            throw new NotAllowedException("Dieses Gerät darf die Zwischenablage nicht lesen.");
        }
    }
}
