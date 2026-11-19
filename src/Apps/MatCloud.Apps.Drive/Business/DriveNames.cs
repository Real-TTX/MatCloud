using MatCloud.Contract.Drive;

namespace MatCloud.Apps.Drive.Business;

/// <summary>
/// The rules of names: a name works on every file system people use (Windows, macOS, Linux), so a folder can be downloaded or synced anywhere. A name
/// is refused, never changed behind the back of the person or the app: no / \ : * ? " &lt; &gt; |, no control characters, not "." or "..", no space or
/// dot at the end, none of the names Windows keeps for itself (CON, NUL, COM1 …), at most <see cref="DriveContract.MaxNameLength"/> characters.
/// Items are reached by their id, never by a path, so a name can never lead out of its folder.
/// </summary>
public static class DriveNames
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The name as it is kept (trimmed at the start), or what is wrong with it (German, for the field <c>name</c>).</summary>
    public static (string? Name, string? Problem) Check(string? name)
    {
        var text = (name ?? string.Empty).TrimStart();
        if (text.Length == 0)
        {
            return (null, "Bitte einen Namen eingeben.");
        }

        if (text.Length > DriveContract.MaxNameLength)
        {
            return (null, $"Der Name ist zu lang (höchstens {DriveContract.MaxNameLength} Zeichen).");
        }

        if (text is "." or "..")
        {
            return (null, "„.“ und „..“ sind keine Namen.");
        }

        if (text.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|'))
        {
            return (null, "Ein Name darf diese Zeichen nicht enthalten: / \\ : * ? \" < > |");
        }

        if (text.EndsWith(' ') || text.EndsWith('.'))
        {
            return (null, "Ein Name darf nicht mit einem Leerzeichen oder einem Punkt enden.");
        }

        var stem = text.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem))
        {
            return (null, $"„{stem}“ ist unter Windows kein erlaubter Name.");
        }

        return (text, null);
    }

    /// <summary>The key of a name in its folder: one name once, whatever the case.</summary>
    public static string KeyOf(string name) => name.ToLowerInvariant();

    /// <summary>The n-th free variant of a name: "Angebot (2).docx", "Ordner (3)".</summary>
    public static string Numbered(string name, DriveItemKind kind, int number)
    {
        var (stem, extension) = Split(name, kind);
        var suffix = $" ({number}){extension}";
        var room = DriveContract.MaxNameLength - suffix.Length;
        return (stem.Length > room ? stem[..Math.Max(1, room)].TrimEnd() : stem) + suffix;
    }

    /// <summary>The name of a copy in the same folder: "Kopie von Angebot.docx".</summary>
    public static string CopyOf(string name)
    {
        var copy = "Kopie von " + name;
        return copy.Length <= DriveContract.MaxNameLength ? copy : name;
    }

    private static (string Stem, string Extension) Split(string name, DriveItemKind kind)
    {
        var extension = kind == DriveItemKind.File ? System.IO.Path.GetExtension(name) : string.Empty;
        if (extension.Length == 0 || extension.Length == name.Length)
        {
            return (name, string.Empty);
        }

        return (name[..^extension.Length], extension);
    }
}
