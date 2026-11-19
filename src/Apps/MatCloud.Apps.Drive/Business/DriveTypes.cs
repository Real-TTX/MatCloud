using MatCloud.Contract;
using MatCloud.Contract.Drive;
using Microsoft.AspNetCore.StaticFiles;

namespace MatCloud.Apps.Drive.Business;

/// <summary>An app that opens a file in the browser: its name, its app id (the brand) and the address with the file in it.</summary>
public sealed record DriveOpener(string App, string Label, string Url);

/// <summary>
/// What a file is for the pages: its category (icon, filter "Typ"), its MIME type (by the name: the bytes are never sniffed) and which app makes it.
/// The categories are in the order of the filter.
/// </summary>
public static class DriveTypes
{
    public const string Folder = "folder";
    public const string Document = "document";
    public const string Spreadsheet = "spreadsheet";
    public const string Presentation = "presentation";
    public const string Pdf = "pdf";
    public const string Image = "image";
    public const string Media = "media";
    public const string Archive = "archive";
    public const string Text = "text";
    public const string Other = "other";

    /// <summary>The categories of the filter with their German names.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Categories =
    [
        (Folder, "Ordner"), (Document, "Dokumente"), (Spreadsheet, "Tabellen"), (Presentation, "Präsentationen"), (Pdf, "PDF"), (Image, "Bilder"),
        (Media, "Audio und Video"), (Archive, "Archive"), (Text, "Text"), (Other, "Andere"),
    ];

    private static readonly FileExtensionContentTypeProvider Mime = CreateProvider();

    private static readonly Dictionary<string, string> ByExtension = Build(
        (Document, [".docx", ".doc", ".docm", ".dotx", ".odt", ".ott", ".rtf", ".pages"]),
        (Spreadsheet, [".xlsx", ".xls", ".xlsm", ".xltx", ".ods", ".ots", ".csv", ".tsv", ".numbers"]),
        (Presentation, [".pptx", ".ppt", ".pptm", ".potx", ".odp", ".otp", ".key"]),
        (Pdf, [".pdf"]),
        (Image, [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".avif", ".heic", ".tif", ".tiff", ".ico"]),
        (Media, [".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".opus", ".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v"]),
        (Archive, [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz"]),
        (Text, [".txt", ".md", ".log", ".json", ".xml", ".yml", ".yaml", ".ini", ".html", ".htm", ".css", ".js"]));

    /// <summary>The office app that makes a kind of document: ThixWrite for documents, ThixCalc for tables, ThixShow for presentations.</summary>
    public static string? OfficeAppOf(string category) => category switch
    {
        Document => CloudApps.Write,
        Spreadsheet => CloudApps.Calc,
        Presentation => CloudApps.Show,
        _ => null,
    };

    public static string CategoryOf(DriveItemKind kind, string name)
        => kind == DriveItemKind.Folder ? Folder : ByExtension.GetValueOrDefault(System.IO.Path.GetExtension(name), Other);

    /// <summary>The extensions of a category (the filter in the database); empty for <see cref="Other"/> and <see cref="Folder"/>.</summary>
    public static IReadOnlyList<string> ExtensionsOf(string category) => ByExtension.Where(p => p.Value == category).Select(p => p.Key).ToList();

    /// <summary>Every extension with a category (the filter "Andere" is everything else).</summary>
    public static IReadOnlyList<string> KnownExtensions => ByExtension.Keys.ToList();

    /// <summary>The icon of the sprite: the toolkit's, and the Drive's own document icons (img/app-icons.svg).</summary>
    public static string IconOf(string category) => category switch
    {
        Folder => "folder",
        Document => "file-doc",
        Spreadsheet => "file-sheet",
        Presentation => "file-slides",
        Pdf => "file-pdf",
        Image => "image",
        Media => "file-media",
        Archive => "archive",
        Text => "file-text",
        _ => "file",
    };

    /// <summary>The German name of a kind of file for the list ("Word-Dokument" would promise Word: the pages say "Dokument").</summary>
    public static string LabelOf(DriveItemKind kind, string name)
    {
        var category = CategoryOf(kind, name);
        var extension = System.IO.Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        return category switch
        {
            Folder => "Ordner",
            Document => $"Dokument ({extension})",
            Spreadsheet => $"Tabelle ({extension})",
            Presentation => $"Präsentation ({extension})",
            Pdf => "PDF",
            Other when extension.Length == 0 => "Datei",
            _ => extension.Length == 0 ? "Datei" : extension,
        };
    }

    /// <summary>The MIME type by the name; unknown ones are application/octet-stream.</summary>
    public static string ContentTypeOf(string name) => Mime.TryGetContentType(name, out var type) ? type : "application/octet-stream";

    private static FileExtensionContentTypeProvider CreateProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".md"] = "text/markdown";
        provider.Mappings[".odt"] = "application/vnd.oasis.opendocument.text";
        provider.Mappings[".ods"] = "application/vnd.oasis.opendocument.spreadsheet";
        provider.Mappings[".odp"] = "application/vnd.oasis.opendocument.presentation";
        provider.Mappings[".webp"] = "image/webp";
        provider.Mappings[".avif"] = "image/avif";
        provider.Mappings[".heic"] = "image/heic";
        provider.Mappings[".opus"] = "audio/opus";
        provider.Mappings[".mkv"] = "video/x-matroska";
        provider.Mappings[".7z"] = "application/x-7z-compressed";
        return provider;
    }

    private static Dictionary<string, string> Build(params (string Category, string[] Extensions)[] groups)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (category, extensions) in groups)
        {
            foreach (var extension in extensions)
            {
                map[extension] = category;
            }
        }

        return map;
    }
}

/// <summary>
/// The seam for "Öffnen mit": an app that edits files in the browser registers here the extensions it opens and the address it opens them at
/// (<c>{id}</c> is replaced by the id of the file). Nothing is registered yet: the web editors of ThixWrite, ThixCalc and ThixShow do not exist, and the
/// pages show no button for something that cannot be done. A module (or the host) calls <see cref="Register"/> at start-up, before the first request.
/// </summary>
public static class DriveOpeners
{
    private static readonly List<(string Extension, string App, string Label, string UrlTemplate)> Registered = [];

    public static void Register(string app, string label, string urlTemplate, params string[] extensions)
    {
        foreach (var extension in extensions)
        {
            Registered.Add((extension.StartsWith('.') ? extension : "." + extension, app, label, urlTemplate));
        }
    }

    /// <summary>The apps that open a file in the browser, in the order they were registered.</summary>
    public static IReadOnlyList<DriveOpener> For(long id, string name)
    {
        var extension = System.IO.Path.GetExtension(name);
        return Registered
            .Where(r => string.Equals(r.Extension, extension, StringComparison.OrdinalIgnoreCase))
            .Select(r => new DriveOpener(r.App, r.Label, r.UrlTemplate.Replace("{id}", id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)))
            .ToList();
    }
}
