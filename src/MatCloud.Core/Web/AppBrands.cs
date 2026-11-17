using MatCloud.Contract;
using MatCloud.Core.Modules;
using Microsoft.AspNetCore.Html;

namespace MatCloud.Core.Web;

/// <summary>
/// How an app looks in MatCloud: its name, its logo and its colour (<see cref="DataApp"/> picks the accent in app.css: <c>[data-app="thixnote"]</c>).
/// MatCloud itself is a brand too. The pages of an app module (<c>/Clip</c>, <c>/Note</c>) wear the brand of their app: logo and name above the menu,
/// the accent, the title of the tab, the favicon and a manifest of their own, so that the app can be installed on its own (<c>/Note/manifest.webmanifest</c>).
/// </summary>
/// <param name="Prefix">The first part of the word mark ("Thix" of "ThixNote"); the rest is drawn in the accent.</param>
/// <param name="Logo">The app icon in full detail (36 px and up).</param>
/// <param name="Favicon">The app icon for 16 to 32 px.</param>
/// <param name="Icon256">A PNG for the manifest, null for MatCloud (it has its own manifest).</param>
public sealed record AppBrand(string AppId, string Name, string Prefix, string DataApp, string Logo, string Favicon, string? Icon256, string? Icon512)
{
    /// <summary>The rest of the word mark ("Note" of "ThixNote").</summary>
    public string Suffix => Name[Prefix.Length..];

    /// <summary>The address of the app's pages, or "/" for MatCloud.</summary>
    public string Home => CloudModules.Find(AppId)?.Home ?? "/";

    public bool IsCloud => AppId == AppBrands.Cloud.AppId;

    /// <summary>The logo as an image (decorative next to the name: the name says it).</summary>
    public IHtmlContent Image(int px, string? cls = null, bool decorative = true)
        => new HtmlString($"<img class=\"mc-logo{(cls is null ? string.Empty : " " + cls)}\" src=\"{Logo}\" alt=\"{(decorative ? string.Empty : Name)}\" width=\"{px}\" height=\"{px}\" />");
}

public static class AppBrands
{
    public static readonly AppBrand Cloud = new("matcloud", Ui.AppName, "Mat", Ui.AppId, "/img/logo.svg", "/img/favicon.svg", null, null);

    private static readonly Dictionary<string, AppBrand> Apps = new(StringComparer.Ordinal)
    {
        [CloudApps.Clip] = new(CloudApps.Clip, "ThixClip", "Thix", "thixclip", "/img/apps/thixclip.svg", "/img/apps/thixclip-small.svg", "/img/apps/thixclip-256.png", "/img/apps/thixclip-512.png"),
        [CloudApps.Note] = new(CloudApps.Note, "ThixNote", "Thix", "thixnote", "/img/apps/thixnote.svg", "/img/apps/thixnote-small.svg", "/img/apps/thixnote-256.png", "/img/apps/thixnote-512.png"),
        [CloudApps.Calendar] = new(CloudApps.Calendar, "ThixCalendar", "Thix", "thixcalendar", "/img/apps/thixcalendar.svg", "/img/apps/thixcalendar.svg", null, null),
        [CloudApps.Tunnel] = new(CloudApps.Tunnel, "ThixTunnel", "Thix", "thixtunnel", "/img/apps/thixtunnel.svg", "/img/apps/thixtunnel.svg", null, null),
        [CloudApps.Launcher] = new(CloudApps.Launcher, "ThixLauncher", "Thix", "thixlauncher", "/img/apps/thixlauncher.svg", "/img/apps/thixlauncher.svg", null, null),
    };

    /// <summary>The brand of an app of the contract (MatCloud's own for an app it does not know).</summary>
    public static AppBrand Of(string? appId) => appId is not null && Apps.TryGetValue(appId, out var brand) ? brand : Cloud;

    /// <summary>The brand a page wears: the app whose module the address belongs to, else MatCloud.</summary>
    public static AppBrand ForPath(string? path)
        => CloudModules.All.FirstOrDefault(m => Menu.Under(path ?? "/", m.Home)) is { } module ? Of(module.AppId) : Cloud;
}
