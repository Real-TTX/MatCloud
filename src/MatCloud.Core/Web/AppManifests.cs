using System.Text.Encodings.Web;
using System.Text.Json;
using MatCloud.Core.Modules;

namespace MatCloud.Core.Web;

/// <summary>
/// A web app manifest per app module (<c>/Note/manifest.webmanifest</c>): installed from its pages, the app is an app of its own on the phone or the
/// desktop – "ThixNote" with its icon, opening at <c>/Note</c>. MatCloud keeps its manifest for everything else (<c>/manifest.webmanifest</c>).
/// </summary>
public static class AppManifests
{
    public static void Map(IEndpointRouteBuilder app)
    {
        foreach (var module in CloudModules.All)
        {
            var brand = AppBrands.Of(module.AppId);
            if (brand.IsCloud || brand.Icon256 is null || brand.Icon512 is null)
            {
                continue;
            }

            var json = JsonSerializer.Serialize(Manifest(module, brand), new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            app.MapGet(module.Home + "/manifest.webmanifest", (HttpContext http) =>
            {
                // asked again every time (like MatCloud's own): a new icon or name is used at once
                http.Response.Headers.CacheControl = "no-cache";
                return Results.Text(json, "application/manifest+json");
            }).AllowAnonymous();
        }
    }

    private static object Manifest(ICloudModule module, AppBrand brand) => new Dictionary<string, object>
    {
        ["id"] = module.Home,
        ["name"] = brand.Name,
        ["short_name"] = brand.Name,
        ["description"] = $"{brand.Name} in deiner MatCloud: {module.MenuTitle} auf allen Geräten, im Browser und als App.",
        ["lang"] = "de",
        ["start_url"] = module.Home,
        ["scope"] = module.Home,
        ["display"] = "standalone",
        ["display_override"] = new[] { "standalone", "minimal-ui" },
        ["background_color"] = "#f5f4f1",
        ["theme_color"] = "#f5f4f1",
        ["categories"] = new[] { "productivity", "utilities" },
        ["icons"] = new object[]
        {
            new Dictionary<string, string> { ["src"] = brand.Icon256!, ["sizes"] = "256x256", ["type"] = "image/png" },
            new Dictionary<string, string> { ["src"] = brand.Icon512!, ["sizes"] = "512x512", ["type"] = "image/png" },
            new Dictionary<string, string> { ["src"] = brand.Logo, ["sizes"] = "any", ["type"] = "image/svg+xml" },
        },
    };
}
