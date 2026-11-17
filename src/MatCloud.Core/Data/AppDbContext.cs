using MatCloud.Core.Modules;
using Microsoft.EntityFrameworkCore;

namespace MatCloud.Core.Data;

/// <summary>
/// The one database of MatCloud (PostgreSQL): the accounts, sessions, devices and the general sync of the platform, and the tables every module adds
/// (<see cref="ICloudModule.ConfigureModel"/>). Tables are PascalCase and singular, the key is always <c>Id</c> (BIGINT), every record has CreateDate,
/// CreateUserId, UpdateDate and UpdateUserId (filled in <see cref="SaveChangesAsync(bool, CancellationToken)"/> with the person or device of the request),
/// and a record with an <c>UpdateState</c> is deleted softly (0 = deleted, 1 = created, 2 = updated).
/// Only the business layers (of the platform and of the modules) use it.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, IHttpContextAccessor? accessor = null) : DbContext(options)
{
    /// <summary>The item of the request that names the acting person (set by the cookie sign-in and by the device token).</summary>
    public const string ActorItem = "MatCloud.ActorId";

    /// <summary>The one sequence every change of the general sync takes its revision from.</summary>
    public const string SyncRevisionSequence = "SyncRevision";

    public DbSet<User> Users => Set<User>();

    public DbSet<UserSession> Sessions => Set<UserSession>();

    public DbSet<Device> Devices => Set<Device>();

    public DbSet<DeviceLink> DeviceLinks => Set<DeviceLink>();

    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    public DbSet<SyncItem> SyncItems => Set<SyncItem>();

    /// <summary>Who changes something outside of a request (tests, background work); a request names its person itself.</summary>
    public long? ActingUserId { get; set; }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasSequence<long>(SyncRevisionSequence).StartsAt(1).IncrementsBy(1);

        b.Entity<User>(e =>
        {
            e.ToTable("User");
            e.Property(x => x.UserName).HasMaxLength(64);
            e.Property(x => x.DisplayName).HasMaxLength(120);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Property(x => x.ExternalSubject).HasMaxLength(200);
            e.Property(x => x.Theme).HasMaxLength(10);
            e.HasIndex(x => x.UserName).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasIndex(x => x.ExternalSubject).IsUnique().HasFilter("\"ExternalSubject\" IS NOT NULL");
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.IdentitySessionId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserAgent).HasMaxLength(300);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.IdentitySessionId).HasMaxLength(200);
        });

        b.Entity<Device>(e =>
        {
            e.ToTable("Device");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => new { x.UserId, x.App });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.App).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Platform).HasMaxLength(40);
            e.Property(x => x.AppVersion).HasMaxLength(60);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasQueryFilter(x => x.UpdateState != SoftEntity.Deleted);
        });

        b.Entity<DeviceLink>(e =>
        {
            e.ToTable("DeviceLink");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(8);
            e.Property(x => x.App).HasMaxLength(32);
            e.Property(x => x.DeviceName).HasMaxLength(100);
            e.Property(x => x.Platform).HasMaxLength(40);
            e.Property(x => x.AppVersion).HasMaxLength(60);
            e.Property(x => x.IpAddress).HasMaxLength(64);
        });

        b.Entity<AppSetting>(e =>
        {
            e.ToTable("AppSetting");
            e.HasIndex(x => x.App).IsUnique();
            e.Property(x => x.App).HasMaxLength(32);
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("SystemSetting");
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Key).HasMaxLength(64);
            e.Property(x => x.Value).HasMaxLength(2000);
        });

        // No query filter: the tombstones are part of the sync. The business layer asks for live items itself.
        b.Entity<SyncItem>(e =>
        {
            e.ToTable("SyncItem");
            e.HasIndex(x => new { x.UserId, x.App, x.Collection, x.Key }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.App, x.Revision });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.App).HasMaxLength(32);
            e.Property(x => x.Collection).HasMaxLength(64);
            e.Property(x => x.Key).HasMaxLength(200);
            e.Property(x => x.ContentHash).HasMaxLength(128);
            e.Property(x => x.Source).HasMaxLength(100);
        });

        foreach (var module in CloudModules.All)
        {
            module.ConfigureModel(b);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns, and turns the delete of a soft record into UpdateState 0.</summary>
    private void Stamp()
    {
        var now = DateTime.UtcNow;
        var actor = ActingUserId ?? accessor?.HttpContext?.Items[ActorItem] as long?;
        foreach (var entry in ChangeTracker.Entries<Entity>().ToList())
        {
            var entity = entry.Entity;
            if (entry.State == EntityState.Deleted && entity is SoftEntity soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = SoftEntity.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entity.CreateDate == default)
                    {
                        entity.CreateDate = now;
                    }

                    entity.CreateUserId ??= actor;
                    entity.UpdateDate = now;
                    entity.UpdateUserId = actor ?? entity.CreateUserId;
                    break;
                case EntityState.Modified:
                    entity.UpdateDate = now;
                    entity.UpdateUserId = actor ?? entity.UpdateUserId;
                    if (entity is SoftEntity { UpdateState: not SoftEntity.Deleted } updated)
                    {
                        updated.UpdateState = SoftEntity.Updated;
                    }

                    break;
            }
        }
    }
}

/// <summary>How MatCloud opens its database: PostgreSQL, the migrations in the host (they contain the tables of every module).</summary>
public static class AppDatabase
{
    public const string MigrationsAssembly = "MatCloud";

    public static DbContextOptionsBuilder Use(DbContextOptionsBuilder options, string connection)
        => options.UseNpgsql(connection, npgsql => npgsql.MigrationsAssembly(MigrationsAssembly));
}
