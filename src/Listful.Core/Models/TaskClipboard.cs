namespace Listful;

/// <summary>
/// How copied rows are written to and read from the clipboard. Rows go up
/// in two forms: Listful's own Markdown, keeping everything (done,
/// decrufted, collapsed), and plain text for other apps, without Listful's
/// hidden markers. A single row on its own is copied as just its text, so
/// it pastes into a line of text as you'd expect.
/// </summary>
public static class TaskClipboard
{
    /// <summary>Listful's own clipboard format (upstream's pasteboard type).</summary>
    public const string TypeIdentifier = "com.chrispinnock.listful.tasks";

    public static string Markdown(IReadOnlyList<TaskItem> block) => MarkdownCodec.Encode(new Outline(items: block));

    public static string PlainText(IReadOnlyList<TaskItem> block)
    {
        if (block.Count == 1) return block[0].Title;
        return string.Join("\n", Markdown(block).Split('\n').Select(line =>
        {
            var marker = line.IndexOf(" " + MarkdownCodec.MarkerPrefix, StringComparison.Ordinal);
            return marker < 0 ? line : line[..marker];
        }));
    }

    /// <summary>
    /// Rows from pasted text: Markdown list items, headings, or any other
    /// lines, one task each. Blank lines are dropped; a <c>#</c> line at the top
    /// is a heading here, not a list title.
    /// </summary>
    public static List<TaskItem> Items(string text)
    {
        var outline = MarkdownCodec.Decode(text);
        var items = outline.Items.ToList();
        if (outline.Title is string title) items.Insert(0, new TaskItem(title, headingLevel: 1));
        return new Outline(items: items.Where(i => i.IsHeading || i.Title.TrimWhitespace().Length > 0)).Items.ToList();
    }
}
