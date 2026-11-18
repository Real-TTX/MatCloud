using MatCloud.Contract.Calendar;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Calendar.Data;

/// <summary>Which registration a Google sign-in belongs to (a refresh token works only with the client that issued it).</summary>
public enum CalendarTokenClient
{
    /// <summary>No sign-in (ICS).</summary>
    None = 0,

    /// <summary>ThixCalendar's registration: Microsoft's public client, Google's desktop client – what the apps hand over.</summary>
    App = 1,

    /// <summary>The Google web client of this MatCloud (the sign-in in the browser).</summary>
    Web = 2,
}

/// <summary>
/// An account of ThixCalendar that MatCloud keeps in step (sync mode Cloud): the sign-in (protected with Data Protection, never shown, never logged) and
/// how the last sync went. Its account, calendars and events are items of the general sync; this row is what only the server needs.
/// </summary>
public sealed class CalendarConnection : Entity
{
    public long UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The SyncId of the account (collection accounts).</summary>
    public string Account { get; set; } = string.Empty;

    public CalendarAccountKind Kind { get; set; }

    /// <summary>The e-mail address of the online account.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>The address of an ICS account.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The refresh token, protected (empty for ICS and after the provider refused it).</summary>
    public string ProtectedToken { get; set; } = string.Empty;

    public CalendarTokenClient Client { get; set; }

    public CalendarCloudState State { get; set; }

    public DateTime? LastSync { get; set; }

    /// <summary>When the worker syncs it next.</summary>
    public DateTime NextSync { get; set; }

    /// <summary>What went wrong last (for the person, German; never a token).</summary>
    public string Problem { get; set; } = string.Empty;

    /// <summary>Failed syncs in a row (the wait grows with them).</summary>
    public int Failures { get; set; }
}

/// <summary>The table of the module in the one database, and the short way to it (<c>db.CalendarConnections</c>).</summary>
public static class CalendarModel
{
    public static void Configure(ModelBuilder b)
    {
        b.Entity<CalendarConnection>(e =>
        {
            e.ToTable("CalendarConnection");
            e.HasIndex(x => new { x.UserId, x.Account }).IsUnique();
            e.HasIndex(x => x.NextSync);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Account).HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Client).HasConversion<int>();
            e.Property(x => x.State).HasConversion<int>();
            e.Property(x => x.UserName).HasMaxLength(320);
            e.Property(x => x.Url).HasMaxLength(2000);
            e.Property(x => x.Problem).HasMaxLength(500);
        });
    }

    extension(AppDbContext db)
    {
        public DbSet<CalendarConnection> CalendarConnections => db.Set<CalendarConnection>();
    }
}
