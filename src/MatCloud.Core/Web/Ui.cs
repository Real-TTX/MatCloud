using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatCloud.Core.Web;

/// <summary>Small helpers for the markup of the toolkit: icons, the logo, initials.</summary>
public static partial class Ui
{
    public const string AppId = "matcloud";
    public const string AppName = "MatCloud";

    /// <summary>The accent of MatCloud (the sky blue of the logo, as in MatDesign <c>AppRegistry.cs</c>). White text on it reaches 5.9:1.</summary>
    public const string Accent = "#0369a1";

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>An icon from the page's inline sprite (the toolkit's and MatCloud's own).</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return new HtmlString($"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    /// <summary>The logo of the menu and the sign-in: the app icon of MatCloud.</summary>
    public static IHtmlContent Logo(string size = "md")
    {
        var px = size switch { "lg" => 56, "sm" => 28, _ => 36 };
        return new HtmlString($"<img class=\"mc-logo mc-logo--{size}\" src=\"/img/logo.svg\" alt=\"{AppName}\" width=\"{px}\" height=\"{px}\" />");
    }

    public static string Initials(string name)
    {
        var letters = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length == 0 ? "?" : letters;
    }

    /// <summary>A size in bytes as people read it (German: 1,4 MB), the number and the unit held together by a no-break space.</summary>
    public static string Bytes(long? bytes)
    {
        if (bytes is null)
        {
            return "–";
        }

        const char space = ' ';
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        string Format(double value, string unit) => value.ToString(value < 10 ? "0.0" : "0", culture).Replace('.', ',') + space + unit;
        return bytes.Value switch
        {
            < 1024 => $"{bytes.Value}{space}B",
            < 1024 * 1024 => Format(bytes.Value / 1024d, "KB"),
            < 1024L * 1024 * 1024 => Format(bytes.Value / 1024d / 1024d, "MB"),
            _ => Format(bytes.Value / 1024d / 1024d / 1024d, "GB"),
        };
    }

    /// <summary>A number with a German thousands separator (12.345).</summary>
    public static string Number(long value)
    {
        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && (text.Length - i) % 3 == 0 && text[i - 1] != '-')
            {
                sb.Append('.');
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }
}
