using System.Reflection;

namespace MatCloud.Core.Web;

/// <summary>The build: <c>0.1.42-20261117</c> (release), <c>nightly-42-20261117</c> (development) or <c>local-20261117</c> (Directory.Build.props, the same for every project).</summary>
public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "local";

    /// <summary>release, nightly or local.</summary>
    public static string Channel { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "MatCloudChannel")?.Value ?? "local";

    /// <summary>"Version 0.1.42-20261117 · Release" as everywhere in the family (MatVersion, SETUP-07).</summary>
    public static string VersionText => $"Version {Version} · {ChannelName}";

    public static string ChannelName => Channel switch
    {
        "release" => "Release",
        "nightly" => "Nightly",
        _ => "Lokal",
    };
}
