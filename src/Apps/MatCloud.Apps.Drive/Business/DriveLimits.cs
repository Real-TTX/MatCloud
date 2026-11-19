using System.Globalization;

namespace MatCloud.Apps.Drive.Business;

/// <summary>
/// The limits of the Drive of this installation, from the environment (all optional): <c>MATCLOUD_DRIVE_QUOTA_GB</c> the space of an account
/// (default 10, 0 = no limit), <c>MATCLOUD_DRIVE_MAX_FILE_MB</c> the largest file (default 2048). Tests set <see cref="Current"/>.
/// </summary>
public sealed record DriveLimits(long? DefaultQuotaBytes, long MaxFileBytes)
{
    private const long Megabyte = 1024L * 1024;

    public static DriveLimits Current { get; set; } = FromEnvironment();

    public static DriveLimits FromEnvironment()
    {
        var quotaGb = Number("MATCLOUD_DRIVE_QUOTA_GB", 10);
        var maxFileMb = Number("MATCLOUD_DRIVE_MAX_FILE_MB", 2048);
        return new DriveLimits(quotaGb <= 0 ? null : quotaGb * 1024 * Megabyte, Math.Max(1, maxFileMb) * Megabyte);
    }

    private static long Number(string name, long fallback)
        => long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}
