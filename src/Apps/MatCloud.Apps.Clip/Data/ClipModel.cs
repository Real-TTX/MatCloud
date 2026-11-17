using MatCloud.Contract.Clip;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Clip.Data;

/// <summary>
/// One clip of an account. A deleted clip stays as a tombstone (<see cref="SoftEntity.UpdateState"/> 0, its <see cref="SyncId"/> and
/// <see cref="Revision"/> kept, its content wiped) so that every device learns about the deletion.
/// </summary>
public sealed class ClipEntry : SoftEntity
{
    public long UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The id the creator (a device or the browser) gave the clip; unique per account, never changes.</summary>
    public Guid SyncId { get; set; }

    public ClipKind Kind { get; set; }

    public string? Text { get; set; }

    public string? OcrText { get; set; }

    /// <summary>SHA-256 of the text (UTF-8) or of the PNG, hex upper case: the same content is one clip.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public bool IsFavorite { get; set; }

    public bool IsPinned { get; set; }

    /// <summary>When the content was copied the last time (the order of the list).</summary>
    public DateTime CopiedDate { get; set; }

    /// <summary>When it was copied the first time (on the device that created it).</summary>
    public DateTime ClientCreateDate { get; set; }

    /// <summary>The last change on a device or in the browser: an older change does not overwrite a newer one.</summary>
    public DateTime ClientUpdateDate { get; set; }

    /// <summary>Where it was copied: the name of the device, or "Web".</summary>
    public string? Source { get; set; }

    public bool HasImage { get; set; }

    public int? ImageWidth { get; set; }

    public int? ImageHeight { get; set; }

    public long? ImageBytes { get; set; }

    /// <summary>The number of the last change, from the one sequence <c>ClipRevision</c>: devices ask for everything above the revision they know.</summary>
    public long Revision { get; set; }

    public ClipImage? Image { get; set; }
}

/// <summary>The picture of an image clip: the PNG and a small preview. A row of its own, so that lists never load the bytes.</summary>
public sealed class ClipImage : Entity
{
    public long ClipEntryId { get; set; }

    public ClipEntry? ClipEntry { get; set; }

    public byte[] Png { get; set; } = [];

    public byte[] Thumbnail { get; set; } = [];
}

/// <summary>What holds for the clips of an account (no row: the defaults of <see cref="ClipContract"/>).</summary>
public sealed class ClipAccount : Entity
{
    public long UserId { get; set; }

    public User? User { get; set; }

    /// <summary>How long clips are kept, in days (0 = forever). Favourites and pinned clips are always kept.</summary>
    public int RetentionDays { get; set; } = ClipContract.DefaultRetentionDays;
}

/// <summary>The tables of the module in the one database, and the short ways to them (<c>db.Clips</c>).</summary>
public static class ClipModel
{
    /// <summary>The one sequence every change of a clip takes its revision from.</summary>
    public const string RevisionSequence = "ClipRevision";

    public static void Configure(ModelBuilder b)
    {
        b.HasSequence<long>(RevisionSequence).StartsAt(1).IncrementsBy(1);

        // No query filter: the tombstones of deleted clips are part of the sync. The business layer asks for live clips itself.
        b.Entity<ClipEntry>(e =>
        {
            e.ToTable("ClipEntry");
            e.HasIndex(x => new { x.UserId, x.SyncId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.Revision });
            e.HasIndex(x => new { x.UserId, x.ContentHash });
            e.HasIndex(x => new { x.UserId, x.CopiedDate });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.ContentHash).HasMaxLength(64);
            e.Property(x => x.Source).HasMaxLength(100);
        });

        b.Entity<ClipImage>(e =>
        {
            e.ToTable("ClipImage");
            e.HasIndex(x => x.ClipEntryId).IsUnique();
            e.HasOne(x => x.ClipEntry).WithOne(x => x.Image).HasForeignKey<ClipImage>(x => x.ClipEntryId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ClipAccount>(e =>
        {
            e.ToTable("ClipAccount");
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    extension(AppDbContext db)
    {
        public DbSet<ClipEntry> Clips => db.Set<ClipEntry>();

        public DbSet<ClipImage> ClipImages => db.Set<ClipImage>();

        public DbSet<ClipAccount> ClipAccounts => db.Set<ClipAccount>();
    }
}
