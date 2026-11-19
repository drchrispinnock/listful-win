using System.Text.RegularExpressions;

namespace Listful;

/// <summary>
/// Reads and writes an <see cref="Outline"/> as a Markdown task list:
/// <code>
/// # Groceries
///
/// - [ ] Open task &lt;!-- listful: collapsed --&gt;
///   - [x] Done child
///   - [x] Tidied-away child &lt;!-- listful: decrufted --&gt;
///
/// ## Later
///
/// - [ ] Another task
/// </code>
/// A repeating checklist's template has its schedule in a marker line of its
/// own after the title (see <see cref="RepeatSchedule"/>).
/// A <c>#</c> heading on the first line is the list's title. Other <c>#</c>, <c>##</c> and
/// <c>###</c> lines at the left margin are headings; a trailing <c>{…}</c> attribute
/// block on a heading (<c>## Setup {#setup}</c>) is kept aside, not shown as
/// part of its text. Nesting is two spaces per
/// level. State the checkbox can't express (decrufted, collapsed) goes in a
/// trailing HTML comment, which Markdown renderers hide. Other lines that
/// aren't list items become open tasks so that nothing in a hand-edited file
/// is lost.
/// <para>Files are written with LF line endings, as the Mac writes them; CRLF is read.</para>
/// </summary>
public static partial class MarkdownCodec
{
    internal const string IndentUnit = "  ";
    internal const string MarkerPrefix = "<!-- listful:";
    /// <summary>Written by versions from before the app was renamed; still read.</summary>
    internal static readonly string[] LegacyMarkerPrefixes = ["<!-- taskmgr:"];
    internal const string MarkerSuffix = "-->";
    internal const int TabWidth = 4;

    public static string Encode(Outline outline)
    {
        var lines = new List<string>();
        var previousWasHeading = false;

        if (outline.Title is string title)
        {
            lines.Add(HeadingLine(1, title, outline.TitleAttributes));
            previousWasHeading = true;
        }
        if (outline.Schedule is RepeatSchedule schedule)
            lines.Add($"{MarkerPrefix} {string.Join(" ", schedule.MarkerWords)} {MarkerSuffix}");

        for (var i = 0; i < outline.Items.Count; i++)
        {
            var item = outline.Items[i];
            string line;
            var flags = new List<string>();
            if (item.HeadingLevel is int level)
            {
                // Blank lines around headings, as Markdown is usually written.
                if (lines.Count > 0) lines.Add("");
                line = HeadingLine(level, item.Title, item.HeadingAttributes);
            }
            else
            {
                if (previousWasHeading) lines.Add("");
                line = string.Concat(Enumerable.Repeat(IndentUnit, item.Depth));
                line += item.State == TaskState.Open ? "- [ ] " : "- [x] ";
                line += item.Title;
                if (item.State == TaskState.Decrufted) flags.Add("decrufted");
            }
            if (item.IsCollapsed && outline.CanCollapse(i)) flags.Add("collapsed");
            if (flags.Count > 0)
                line += $" {MarkerPrefix} {string.Join(" ", flags)} {MarkerSuffix}";
            lines.Add(line);
            previousWasHeading = item.IsHeading;
        }
        return lines.Count == 0 ? "" : string.Join("\n", lines) + "\n";
    }

    public static Outline Decode(string text)
    {
        string? title = null;
        string? titleAttributes = null;
        RepeatSchedule? schedule = null;
        var items = new List<TaskItem>();
        var sawContent = false;
        // Indent widths of the current chain of ancestors.
        var indentStack = new List<int>();

        foreach (var rawLine in SplitLines(text))
        {
            var (indent, content) = SplitIndent(rawLine);
            var rest = content.TrimWhitespace();
            if (rest.Length == 0) continue;

            var flags = TakeMarker(ref rest) ?? [];
            // A marker on a line of its own is the list's repeat schedule.
            if (rest.Length == 0 && schedule == null && RepeatSchedule.FromMarkerWords(flags) is RepeatSchedule parsed)
            {
                schedule = parsed;
                continue;
            }
            var isFirstLine = !sawContent;
            sawContent = true;

            if (indent == 0 && ParseHeading(rest) is (int level, string headingText))
            {
                var (text2, attributes) = SplitAttributes(headingText);
                if (isFirstLine && level == 1)
                {
                    title = text2;
                    titleAttributes = attributes;
                    continue;
                }
                items.Add(new TaskItem(text2, isCollapsed: flags.Contains("collapsed"), headingLevel: level, headingAttributes: attributes));
                // Tasks after a heading start again at the top level.
                indentStack.Clear();
                continue;
            }

            while (indentStack.Count > 0 && indentStack[^1] >= indent) indentStack.RemoveAt(indentStack.Count - 1);
            var depth = indentStack.Count;
            indentStack.Add(indent);

            var state = flags.Contains("decrufted") ? TaskState.Decrufted : TaskState.Open;
            if (DropBullet(rest) is string bulletless)
            {
                rest = bulletless;
                if (DropCheckbox(rest) is (bool @checked, string boxTitle))
                {
                    rest = boxTitle;
                    if (@checked && state != TaskState.Decrufted) state = TaskState.Done;
                    if (!@checked) state = TaskState.Open;
                }
            }

            items.Add(new TaskItem(rest, state, depth, isCollapsed: flags.Contains("collapsed")));
        }
        return new Outline(title, items, schedule, titleAttributes);
    }

    /// <summary>The list title a file would decode to, without decoding the rest.</summary>
    public static string? TitleIn(string text)
    {
        foreach (var rawLine in SplitLines(text))
        {
            if (rawLine.Length == 0) continue;
            var (indent, content) = SplitIndent(rawLine);
            var rest = content.TrimWhitespace();
            if (rest.Length == 0) continue;
            TakeMarker(ref rest);
            if (rest.Length == 0) continue;
            if (indent != 0 || ParseHeading(rest) is not (1, string heading)) return null;
            return SplitAttributes(heading).Text;
        }
        return null;
    }

    /// <summary>
    /// Parses an ATX heading of level 1–3 (<c># Title</c>, <c>## Title ##</c>).
    /// Deeper levels aren't headings here. <c>#tag</c> (no space) isn't a heading.
    /// </summary>
    public static (int Level, string Text)? ParseHeading(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        if (level is < 1 or > 3) return null;
        var rest = line[level..];
        if (rest.Length != 0 && !char.IsWhiteSpace(rest[0])) return null;
        var text = rest.TrimWhitespace();
        // Optional closing sequence: "## Title ##".
        var closing = ClosingSequence().Match(text);
        if (closing.Success) text = text[..closing.Index].TrimWhitespace();
        return (level, text);
    }

    internal static string HeadingLine(int level, string title, string? attributes = null)
    {
        var hashes = new string('#', level);
        var text = string.Join(" ", new[] { title, attributes ?? "" }.Where(s => s.Length > 0));
        return text.Length == 0 ? hashes : $"{hashes} {text}";
    }

    /// <summary>
    /// Splits a trailing attribute block off a heading's text:
    /// <c>"Setup {#setup .wide}"</c> → <c>("Setup", "{#setup .wide}")</c>. Only a block
    /// after some text counts, so a heading that's all braces stays as it is.
    /// </summary>
    public static (string Text, string? Attributes) SplitAttributes(string text)
    {
        var match = AttributeBlock().Match(text);
        if (!match.Success) return (text, null);
        return (text[..match.Index], match.Value.TrimWhitespace());
    }

    // MARK: - Line parsing

    /// <summary>Lines split at any line break, as Swift's <c>isNewline</c> does (CRLF counts once).</summary>
    internal static string[] SplitLines(string text) => LineBreak().Split(text);

    private static (int Width, string Content) SplitIndent(string line)
    {
        var width = 0;
        var index = 0;
        while (index < line.Length)
        {
            switch (line[index])
            {
                case ' ': width += 1; break;
                case '\t': width += TabWidth - width % TabWidth; break;
                default: return (width, line[index..]);
            }
            index++;
        }
        return (width, "");
    }

    /// <summary>Strips a trailing <c>&lt;!-- listful: ... --&gt;</c> comment and returns its words.</summary>
    private static HashSet<string>? TakeMarker(ref string text)
    {
        if (!text.EndsWith(MarkerSuffix, StringComparison.Ordinal)) return null;
        var line = text;
        var starts = new[] { MarkerPrefix }.Concat(LegacyMarkerPrefixes)
            .Select(prefix => (index: line.LastIndexOf(prefix, StringComparison.Ordinal), prefix))
            .Where(x => x.index >= 0)
            .ToList();
        if (starts.Count == 0) return null;
        var (start, found) = starts.MaxBy(x => x.index);
        var bodyStart = start + found.Length;
        var bodyEnd = text.Length - MarkerSuffix.Length;
        if (bodyEnd < bodyStart) return null;
        var body = text[bodyStart..bodyEnd];
        var flags = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        text = text[..start].TrimWhitespace();
        return flags;
    }

    private static string? DropBullet(string text)
    {
        foreach (var bullet in new[] { "- ", "* ", "+ " })
            if (text.StartsWith(bullet, StringComparison.Ordinal)) return text[bullet.Length..];
        return text is "-" or "*" or "+" ? "" : null;
    }

    private static (bool Checked, string Title)? DropCheckbox(string text)
    {
        foreach (var (box, @checked) in new[] { ("[ ]", false), ("[x]", true), ("[X]", true) })
        {
            if (text == box) return (@checked, "");
            if (text.StartsWith(box + " ", StringComparison.Ordinal)) return (@checked, text[(box.Length + 1)..]);
        }
        return null;
    }

    [GeneratedRegex(@"(^|\s)#+$")]
    private static partial Regex ClosingSequence();

    [GeneratedRegex(@"\s+\{[^{}]*\}\z")]
    private static partial Regex AttributeBlock();

    [GeneratedRegex(@"\r\n|[\n\r\x0B\x0C\x85\p{Zl}\p{Zp}]")]
    private static partial Regex LineBreak();
}
