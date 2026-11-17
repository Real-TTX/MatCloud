namespace MatCloud.Core.Data;

/// <summary>What every record has: an Id (BIGINT) and who created and changed it, and when (the columns are filled by <see cref="AppDbContext"/>).</summary>
public abstract class Entity
{
    public long Id { get; set; }

    public DateTime CreateDate { get; set; }

    public long? CreateUserId { get; set; }

    public DateTime UpdateDate { get; set; }

    public long? UpdateUserId { get; set; }
}

/// <summary>A record that supports soft delete: 0 = deleted, 1 = created, 2 = updated.</summary>
public abstract class SoftEntity : Entity
{
    public const short Deleted = 0;
    public const short Created = 1;
    public const short Updated = 2;

    public short UpdateState { get; set; } = Created;
}

/// <summary>An account. Its data in every app is its own: nobody else sees it, an administrator neither.</summary>
public sealed class User : SoftEntity
{
    /// <summary>The name to sign in with (devices and browser); unique, stored in lower case.</summary>
    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Empty for an account that only signs in through MatIdentity.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>A password set by an administrator: the person chooses their own at the next sign-in.</summary>
    public bool MustChangePassword { get; set; }

    /// <summary>The person of MatIdentity (the claim <c>sub</c>) this account belongs to; null for a local account.</summary>
    public string? ExternalSubject { get; set; }

    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    public int FailedLogins { get; set; }

    public DateTime? LockoutUntil { get; set; }

    public DateTime? LastLoginDate { get; set; }
}

/// <summary>A signed-in browser. The cookie carries the <see cref="Token"/>; the session lives in the database.</summary>
public sealed class UserSession : Entity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    public DateTime LastSeenDate { get; set; }

    public DateTime ExpiresDate { get; set; }

    public bool IsRevoked { get; set; }

    public string? UserAgent { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>The ID token of MatIdentity (for its sign-out), only after a sign-in through MatIdentity.</summary>
    public string? IdentityToken { get; set; }

    /// <summary>The claim <c>sid</c> of MatIdentity: a back-channel logout of that session ends this one.</summary>
    public string? IdentitySessionId { get; set; }
}

/// <summary>
/// An app on a computer that signed in (ThixClip on ANNA-PC). It sends its <see cref="Token"/> as a bearer token with every request and reads and
/// writes only the data of its <see cref="App"/>; removing the device (soft delete) ends its access at once.
/// </summary>
public sealed class Device : SoftEntity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long UserId { get; set; }

    public User? User { get; set; }

    /// <summary>The app (<c>MatCloud.Contract.CloudApps</c>: clip, note ...).</summary>
    public string App { get; set; } = string.Empty;

    /// <summary>The name the computer gave itself (usually the computer name).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>windows, macos ...</summary>
    public string Platform { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public DateTime LastSeenDate { get; set; }

    public string? IpAddress { get; set; }
}

/// <summary>
/// A sign-in in the browser that a device started: the device shows <see cref="Code"/>, a person confirms it on the page Connect and the device
/// fetches its token with the secret <see cref="Token"/> (once). Ten minutes long; the housekeeping removes old ones.
/// </summary>
public sealed class DeviceLink : Entity
{
    public const short Pending = 0;
    public const short Approved = 1;
    public const short Denied = 2;
    public const short Fetched = 3;

    /// <summary>The secret the device polls with.</summary>
    public Guid Token { get; set; } = Guid.NewGuid();

    /// <summary>The code the person compares: 8 letters and digits without the ones that look alike, stored without the dash (ABCDEFGH).</summary>
    public string Code { get; set; } = string.Empty;

    public string App { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public string? IpAddress { get; set; }

    public DateTime ExpiresDate { get; set; }

    public short State { get; set; } = Pending;

    /// <summary>The account that confirmed it.</summary>
    public long? UserId { get; set; }

    /// <summary>The device made when it was confirmed; its token goes to the device with the next poll.</summary>
    public long? DeviceId { get; set; }
}

/// <summary>Whether an app is switched on in this installation (no row = on).</summary>
public sealed class AppSetting : Entity
{
    public string App { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
}

/// <summary>A setting of the installation (the public address ...), as text.</summary>
public sealed class SystemSetting : Entity
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// One item of the general sync (<c>MatCloud.Contract.SyncContract</c>): the data an app without a module of its own keeps, named by
/// <see cref="Collection"/> and <see cref="Key"/>. A deleted item stays as a tombstone (UpdateState 0, its data wiped) so that every device learns of it.
/// </summary>
public sealed class SyncItem : SoftEntity
{
    public long UserId { get; set; }

    public User? User { get; set; }

    public string App { get; set; } = string.Empty;

    public string Collection { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    /// <summary>The content as the app sent it; null for a tombstone.</summary>
    public string? Data { get; set; }

    public string? ContentHash { get; set; }

    /// <summary>The length of <see cref="Data"/> (characters): the use of an account, without reading the data.</summary>
    public int Size { get; set; }

    /// <summary>The last change on a device: an older change does not overwrite a newer one.</summary>
    public DateTime ClientUpdateDate { get; set; }

    /// <summary>The name of the device that wrote it last.</summary>
    public string? Source { get; set; }

    /// <summary>The number of the last change, from the one sequence <c>SyncRevision</c>.</summary>
    public long Revision { get; set; }
}
