using MatCloud.Core.Business;
using MatCloud.Core.Modules;

namespace MatCloud.Core.Web;

/// <summary>One menu of the switcher at the foot of the menu (MatDesign SHELL-15); <see cref="Partial"/> draws its entries.</summary>
public sealed record MenuArea(string Id, string Title, string Icon, string Href, string Partial);

/// <summary>
/// The menus: MatCloud itself (overview, apps, devices), one per app module that is switched on (ThixClip: the clipboard), the account and, for an
/// administrator, the administration. A page belongs to one of them by its address.
/// </summary>
public static class Menu
{
    public const string Home = "home";
    public const string Account = "account";
    public const string Admin = "admin";

    public static IReadOnlyList<MenuArea> Areas(bool admin, IEnumerable<AppView> enabledApps)
    {
        var areas = new List<MenuArea> { new(Home, "MatCloud", "cloud", "/", "_MenuHome") };
        foreach (var app in enabledApps)
        {
            if (CloudModules.Find(app.Id) is { } module)
            {
                areas.Add(new MenuArea(module.AppId, module.MenuTitle, module.Icon, module.Home, module.MenuPartial));
            }
        }

        areas.Add(new MenuArea(Account, "Konto", "user", "/Account", "_MenuAccount"));
        if (admin)
        {
            areas.Add(new MenuArea(Admin, "Administration", "shield", "/Admin", "_MenuAdmin"));
        }

        return areas;
    }

    public static string AreaOf(string? path)
    {
        path ??= "/";
        if (Under(path, "/Admin"))
        {
            return Admin;
        }

        if (Under(path, "/Account"))
        {
            return Account;
        }

        return CloudModules.All.FirstOrDefault(m => Under(path, m.Home))?.AppId ?? Home;
    }

    public static bool Under(string path, string prefix)
        => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
}
