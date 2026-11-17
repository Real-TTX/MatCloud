using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MatCloud.Apps.Note.Business;

/// <summary>
/// The Markdown of a note as HTML and as plain text. HTML of the note's own is never passed through (Markdig with HTML switched off), so a note cannot put
/// a script into the page. The page and the preview of the editor draw the same way.
/// </summary>
public static partial class NoteMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseReferralLinks("noopener", "noreferrer")
        .DisableHtml()
        .Build();

    /// <summary>
    /// The note as HTML. A first heading that only repeats the title is left out (the page shows the title above it already).
    /// </summary>
    public static string ToHtml(string markdown, string? title = null)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        if (title is not null && document.Count > 0 && document[0] is HeadingBlock { Level: 1 } heading
            && string.Equals(TextOf(heading).Trim(), title.Trim(), StringComparison.CurrentCultureIgnoreCase))
        {
            document.RemoveAt(0);
        }

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    /// <summary>
    /// The first lines of a text that say something, as plain text: no marks of Markdown (#, **, [link](…), - [ ], |), no code fences, no rules, no heading
    /// that only repeats the title. At most <paramref name="lines"/> lines and <paramref name="max"/> characters (with "…" when cut).
    /// </summary>
    public static IReadOnlyList<string> PlainLines(string text, string? title, int lines, int max)
    {
        var result = new List<string>();
        var length = 0;
        var inFence = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            var plain = inFence ? line : Plain(line);
            if (plain.Length == 0 || (result.Count == 0 && string.Equals(plain, title?.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            {
                continue;
            }

            if (length + plain.Length > max)
            {
                var room = max - length;
                if (room >= 12 || result.Count == 0)
                {
                    // cut after a whole word where one ends in the second half
                    var cut = plain[..Math.Max(room, 1)];
                    var space = cut.LastIndexOf(' ');
                    result.Add((space > cut.Length / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';', ':', '-') + "…");
                }
                else
                {
                    result[^1] += "…";
                }

                break;
            }

            result.Add(plain);
            length += plain.Length;
            if (result.Count == lines)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>One line of Markdown as people read it.</summary>
    public static string Plain(string line)
    {
        if (RuleRegex().IsMatch(line) || TableRuleRegex().IsMatch(line))
        {
            return string.Empty;
        }

        var s = BlockMarkRegex().Replace(line, string.Empty);
        s = ImageRegex().Replace(s, "$1");
        s = LinkRegex().Replace(s, "$1");
        s = s.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("~~", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);
        s = EmphasisRegex().Replace(s, "$1");
        if (s.StartsWith('|') || s.EndsWith('|'))
        {
            s = string.Join(" · ", s.Trim('|').Split('|').Select(c => c.Trim()).Where(c => c.Length > 0));
        }

        return s.Trim();
    }

    private static string TextOf(LeafBlock block)
    {
        var text = new StringBuilder();
        if (block.Inline is not null)
        {
            foreach (var literal in block.Inline.Descendants<LiteralInline>())
            {
                text.Append(literal.Content.ToString());
            }
        }

        return text.ToString();
    }

    /// <summary>The marks at the start of a line: headings, quotes, list items, tasks.</summary>
    [GeneratedRegex(@"^(\s*(#{1,6}\s+|>\s?|[-*+]\s+|\d+[.)]\s+|\[[ xX]\]\s+))+")]
    private static partial Regex BlockMarkRegex();

    [GeneratedRegex(@"^\s*([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex RuleRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableRuleRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?<![\w*_])[*_](?=\S)([^*_]+?)(?<=\S)[*_](?![\w*_])")]
    private static partial Regex EmphasisRegex();
}
