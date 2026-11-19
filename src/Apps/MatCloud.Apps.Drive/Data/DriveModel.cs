using MatCloud.Contract.Drive;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Apps.Drive.Data;

/// <summary>What kind of drive: v1 knows the personal one; a shared drive of a team is ready in the model (shares and links come with it).</summary>
public enum DriveKind
{
    /// <summary>The Drive of one account (<see cref="DriveSpace.OwnerUserId"/>): only that account reaches it.</summary>
    Personal = 1,

    /// <summary>Reserved for shared drives (not used yet).</summary>
    Shared = 2,
}

/// <summary>A drive: the files and folders of one account. Made at the first change of the account.</summary>
public sealed class DriveSpace : SoftEntity
{
    public DriveKind Kind { get; set; } = DriveKind.Personal;

    /// <summary>The account of a personal drive.</summary>
    public long? OwnerUserId { get; set; }

    public User? Owner { get; set; }

    /// <summary>The space in bytes; null = the default of the installation (<c>MATCLOUD_DRIVE_QUOTA_GB</c>).</summary>
    public long? QuotaBytes { get; set; }
}

/// <summary>
/// A file or a folder. <see cref="Path"/> is the chain of ids from the top of the drive to the node itself (<c>/12/40/41/</c>): the folders above and
/// everything below are found with one query. The trash keeps an item in its place: <see cref="TrashedDate"/> marks the item that was put there,
/// <see cref="InTrash"/> everything below it too. Deleted for good, a node stays as a tombstone (<see cref="SoftEntity.UpdateState"/> 0, no versions)
/// with its <see cref="Revision"/>, so that a device that syncs later learns about it; the tombstones go after a year.
/// </summary>
public sealed class DriveNode : SoftEntity
{
    public long DriveId { get; set; }

    public DriveSpace? Drive { get; set; }

    /// <summary>The folder it lies in; null = the top of the drive.</summary>
    public long? ParentId { get; set; }

    public DriveNode? Parent { get; set; }

    public DriveItemKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The name in lower case: a folder has every name once, whatever the case (Windows and macOS see it so).</summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary>What it is by its name (<c>DriveTypes</c>: folder, document, spreadsheet …): the filter "Typ" and the sort by type.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The ids from the top of the drive to this node, each followed by a slash: <c>/12/40/41/</c>.</summary>
    public string Path { get; set; } = "/";

    /// <summary>The bytes of the current version (a folder: 0).</summary>
    public long Size { get; set; }

    public string? ContentType { get; set; }

    /// <summary>The current version of a file (its id is the ETag), its number and its SHA-256.</summary>
    public long? CurrentVersionId { get; set; }

    public int? VersionNumber { get; set; }

    public string? Sha256 { get; set; }

    /// <summary>When the content of a file changed last (a folder: when something in it changed).</summary>
    public DateTime ModifiedDate { get; set; }

    /// <summary>Who changed it last: the name of the device, or "Web".</summary>
    public string? ModifiedBy { get; set; }

    /// <summary>When the node itself was put into the trash (not because a folder above it was); null while it is not.</summary>
    public DateTime? TrashedDate { get; set; }

    /// <summary>True for everything in the trash, also through a folder above (the lists hide these).</summary>
    public bool InTrash { get; set; }

    /// <summary>The number of the last change, from the sequence <c>DriveRevision</c>.</summary>
    public long Revision { get; set; }
}

/// <summary>One state of a file. The bytes are the blob of <see cref="Sha256"/> (<c>/data/drive/blobs/ab/cd/&lt;sha256&gt;</c>).</summary>
public sealed class DriveNodeVersion : Entity
{
    public long NodeId { get; set; }

    public DriveNode? Node { get; set; }

    /// <summary>1, 2, 3 … per file.</summary>
    public int Number { get; set; }

    /// <summary>SHA-256 of the bytes (hex, lower case): the name of the blob.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    public string? ContentType { get; set; }

    /// <summary>Who saved it: the name of the device, or "Web".</summary>
    public string? Source { get; set; }

    /// <summary>The app that saved it ("write"); null in the browser.</summary>
    public string? App { get; set; }
}

/// <summary>
/// The bytes of one content, once for the whole installation (the same document in two places, or saved twice unchanged, is stored once). A blob that no
/// version names any more is removed by the housekeeping after an hour (<see cref="Entity.UpdateDate"/> is touched whenever an upload uses it).
/// </summary>
public sealed class DriveBlob : Entity
{
    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }
}

/// <summary>An item an account starred.</summary>
public sealed class DriveStar : Entity
{
    public long UserId { get; set; }

    public long NodeId { get; set; }

    public DriveNode? Node { get; set; }
}

/// <summary>When an account opened or saved a file last, and in which app (the list "Zuletzt verwendet").</summary>
public sealed class DriveVisit : Entity
{
    public long UserId { get; set; }

    public long NodeId { get; set; }

    public DriveNode? Node { get; set; }

    public DateTime VisitDate { get; set; }

    public string? App { get; set; }
}

/// <summary>An upload in pieces: where it goes (a new file in a folder, or a new version of a file), how big it is and how much arrived.</summary>
public sealed class DriveUpload : Entity
{
    /// <summary>The id the device sends the pieces to (a secret of the device).</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    /// <summary>A new file: its folder (null = the top) and its name.</summary>
    public long? ParentId { get; set; }

    public string? Name { get; set; }

    public DriveNameConflict OnConflict { get; set; } = DriveNameConflict.Fail;

    /// <summary>A new version: the file and the ETag it was opened with.</summary>
    public long? NodeId { get; set; }

    public string? IfMatch { get; set; }

    public long Size { get; set; }

    public long Received { get; set; }

    public DateTime ExpiresDate { get; set; }

    /// <summary>The device (or "Web") and its app, for the version it makes.</summary>
    public string? Source { get; set; }

    public string? App { get; set; }
}

/// <summary>The tables of the Drive in the one database, and the short ways to them (<c>db.DriveNodes</c>).</summary>
public static class DriveModel
{
    /// <summary>The one sequence every change of a node takes its revision from.</summary>
    public const string RevisionSequence = "DriveRevision";

    public static void Configure(ModelBuilder b)
    {
        b.HasSequence<long>(RevisionSequence).StartsAt(1).IncrementsBy(1);

        b.Entity<DriveSpace>(e =>
        {
            e.ToTable("DriveSpace");
            e.HasIndex(x => x.OwnerUserId).IsUnique().HasFilter("\"Kind\" = 1 AND \"UpdateState\" <> 0");
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Kind).HasConversion<int>();
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        // The query filter hides the tombstones; the sync of devices (v2) will read them with IgnoreQueryFilters.
        b.Entity<DriveNode>(e =>
        {
            e.ToTable("DriveNode");
            e.HasOne(x => x.Drive).WithMany().HasForeignKey(x => x.DriveId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Kind).HasConversion<int>();
            e.Property(x => x.Name).HasMaxLength(DriveContract.MaxNameLength);
            e.Property(x => x.NameKey).HasMaxLength(DriveContract.MaxNameLength);
            e.Property(x => x.Category).HasMaxLength(20);
            e.Property(x => x.Path).HasMaxLength(2000);
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.ModifiedBy).HasMaxLength(100);

            // every name once per folder (the top of a drive too: NULLS NOT DISTINCT), the trash and the tombstones aside
            e.HasIndex(x => new { x.DriveId, x.ParentId, x.NameKey }).IsUnique().AreNullsDistinct(false)
                .HasFilter("\"InTrash\" = false AND \"UpdateState\" <> 0").HasDatabaseName("IX_DriveNode_Name");
            e.HasIndex(x => x.Path).HasOperators("text_pattern_ops");
            e.HasIndex(x => new { x.DriveId, x.Revision });
            e.HasIndex(x => x.ParentId);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<DriveNodeVersion>(e =>
        {
            e.ToTable("DriveNodeVersion");
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.NodeId, x.Number }).IsUnique();
            e.HasIndex(x => x.Sha256);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.ContentType).HasMaxLength(200);
            e.Property(x => x.Source).HasMaxLength(100);
            e.Property(x => x.App).HasMaxLength(32);
        });

        b.Entity<DriveBlob>(e =>
        {
            e.ToTable("DriveBlob");
            e.HasIndex(x => x.Sha256).IsUnique();
            e.Property(x => x.Sha256).HasMaxLength(64);
        });

        b.Entity<DriveStar>(e =>
        {
            e.ToTable("DriveStar");
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.NodeId }).IsUnique();
        });

        b.Entity<DriveVisit>(e =>
        {
            e.ToTable("DriveVisit");
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.NodeId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.VisitDate });
            e.Property(x => x.App).HasMaxLength(32);
        });

        b.Entity<DriveUpload>(e =>
        {
            e.ToTable("DriveUpload");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.Property(x => x.Name).HasMaxLength(DriveContract.MaxNameLength);
            e.Property(x => x.IfMatch).HasMaxLength(100);
            e.Property(x => x.OnConflict).HasConversion<int>();
            e.Property(x => x.Source).HasMaxLength(100);
            e.Property(x => x.App).HasMaxLength(32);
        });
    }

    extension(AppDbContext db)
    {
        public DbSet<DriveSpace> DriveSpaces => db.Set<DriveSpace>();

        public DbSet<DriveNode> DriveNodes => db.Set<DriveNode>();

        public DbSet<DriveNodeVersion> DriveVersions => db.Set<DriveNodeVersion>();

        public DbSet<DriveBlob> DriveBlobs => db.Set<DriveBlob>();

        public DbSet<DriveStar> DriveStars => db.Set<DriveStar>();

        public DbSet<DriveVisit> DriveVisits => db.Set<DriveVisit>();

        public DbSet<DriveUpload> DriveUploads => db.Set<DriveUpload>();
    }
}
