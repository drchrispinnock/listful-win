using System.Globalization;

namespace Listful;

/// <summary>
/// Repeating checklists: templates (lists with a <see cref="RepeatSchedule"/>, kept in a
/// templates folder) that are copied into the lists folder once per period,
/// as "&lt;template&gt; &lt;period key&gt;.md". A copy's existence is what records that a
/// period has been done, so making copies is idempotent, and two computers sharing
/// the folder find each other's copies instead of repeating them.
/// </summary>
public static class RepeatingChecklists
{
    /// <summary>The name a template's copy for a period gets, without the extension.</summary>
    public static string CopyName(string template, string key) => $"{template} {key}";

    /// <summary>
    /// The lists among <paramref name="lists"/> that are copies of the template called
    /// <paramref name="template"/>, newest first. A copy is "&lt;template&gt; &lt;key&gt;" for a period key
    /// of any frequency, so copies made before a schedule change still count.
    /// So is "&lt;template&gt; &lt;key&gt; &lt;n&gt;", the name a sync service may give a second copy when
    /// two computers make the same one; it comes after the copy it duplicates.
    /// </summary>
    public static List<string> Copies(string template, IEnumerable<string> lists) =>
        lists
            .Select(path => (path, key: CopyKey(path, template)))
            .Where(x => x.key != null)
            .OrderByDescending(x => x.key!.Value.Key, StringComparer.Ordinal)
            .ThenBy(x => x.key!.Value.Duplicate)
            .Select(x => x.path)
            .ToList();

    /// <summary>
    /// The period key and duplicate number (0 for the copy itself) of a copy
    /// of <paramref name="template"/>; null if <paramref name="path"/> isn't one.
    /// </summary>
    public static (string Key, int Duplicate)? CopyKey(string path, string template)
    {
        var prefix = template + " ";
        var name = TaskLibrary.DisplayName(path);
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var key = name[prefix.Length..];
        var duplicate = 0;
        var space = key.LastIndexOf(' ');
        if (space >= 0 && int.TryParse(key[(space + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            duplicate = n;
            key = key[..space];
        }
        var isKey = Enum.GetValues<RepeatSchedule.Frequency>().Any(f => new RepeatSchedule(f).IsPeriodKey(key));
        return isKey ? (key, duplicate) : null;
    }

    /// <summary>
    /// <paramref name="duplicate"/>, a second copy of the same period, folded into <paramref name="original"/>.
    /// A task both have (by title, under the same heading and parent) is
    /// done if it's done in either; tasks only the duplicate has are added
    /// after the original's, under the same heading.
    /// </summary>
    public static Outline Merging(Outline duplicate, Outline original)
    {
        var merged = Sections(original.Items);
        foreach (var section in Sections(duplicate.Items))
        {
            var nodes = DropBlank(section.Nodes);
            var target = merged.FindIndex(s => SameHeading(s.Heading, section.Heading));
            if (target >= 0) Fold(nodes, merged[target].Nodes);
            else if (section.Heading == null) merged.Insert(0, new Section(null, nodes));
            else merged.Add(new Section(section.Heading, nodes));
        }
        return new Outline(original.Title, Flatten(merged), original.Schedule);
    }

    /// <summary>
    /// Merges each duplicate copy (see <see cref="Copies"/>) into
    /// its original and moves it to the Recycle Bin, except <paramref name="keeping"/> (the open
    /// list, which may have unsaved edits). The original is written first,
    /// so nothing is lost if trashing fails. Returns the duplicates trashed.
    /// </summary>
    public static List<string> MergeDuplicates(TaskLibrary templates, TaskLibrary lists, string? keeping = null)
    {
        List<string> templatePaths, listPaths;
        try
        {
            templatePaths = templates.Lists();
            listPaths = lists.Lists();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        var trashed = new List<string>();
        foreach (var template in templatePaths.Select(TaskLibrary.DisplayName))
        {
            foreach (var path in listPaths)
            {
                if (keeping != null && PathUtility.SamePath(path, keeping)) continue;
                if (CopyKey(path, template) is not (string key, int duplicate) || duplicate <= 0) continue;
                var originalStore = new MarkdownFileStore(lists.PathForName(CopyName(template, key)));
                try
                {
                    if (originalStore.ReadText() is not string originalText
                        || new MarkdownFileStore(path).ReadText() is not string duplicateText)
                        continue;
                    var merged = MarkdownCodec.Encode(Merging(MarkdownCodec.Decode(duplicateText), MarkdownCodec.Decode(originalText)));
                    if (merged != originalText) originalStore.WriteText(merged);
                    lists.TrashList(path);
                    trashed.Add(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        return trashed;
    }

    /// <summary>Whether a list has any task left to do (a blank row doesn't count).</summary>
    public static bool HasOpenTasks(Outline outline) =>
        outline.Items.Any(item => !item.IsHeading && item.State == TaskState.Open && item.Title.TrimWhitespace().Length > 0);

    /// <summary>
    /// A fresh copy of <paramref name="template"/>: every task open again, titled "&lt;title&gt; — &lt;label&gt;",
    /// without the schedule. If <paramref name="previous"/> (the template's last copy) is
    /// given, its unfinished tasks are moved to the top of the copy, and the
    /// previous copy without them is returned too (null if nothing moved).
    /// <para>
    /// What moves: each open task with its whole subtree, done subtasks
    /// included. Under a done task, its open subtasks move on their own. A
    /// moved task that the copy already has (same title, at the same place)
    /// isn't duplicated; its subtasks are merged in instead. Tasks under a
    /// heading go into the copy's heading of the same name, or under a new
    /// one at the end if the template doesn't have it.
    /// </para>
    /// </summary>
    public static (Outline Copy, Outline? Previous) MakeCopy(Outline template, string name, string label, Outline? carryingOverFrom)
    {
        var copySections = Sections(template.Items.Select(item =>
        {
            item.Id = Guid.NewGuid();
            if (!item.IsHeading) item.State = TaskState.Open;
            return item;
        }).ToList()).Select(section => section with { Nodes = DropBlank(section.Nodes) }).ToList();
        Outline? updatedPrevious = null;

        if (carryingOverFrom is Outline previous)
        {
            var kept = new List<Section>();
            var moved = false;
            foreach (var section in Sections(previous.Items))
            {
                var remaining = new List<Node>();
                var carried = new List<Node>();
                foreach (var node in section.Nodes)
                {
                    var (keep, carry) = ExtractUnfinished(node);
                    if (keep != null) remaining.Add(keep);
                    carried.AddRange(carry);
                }
                // A blank row left at the end of the list stays where it is.
                remaining.AddRange(carried.Where(IsBlank));
                carried = carried.Where(n => !IsBlank(n)).ToList();
                kept.Add(new Section(section.Heading, remaining));
                if (carried.Count == 0) continue;
                moved = true;
                var target = copySections.FindIndex(s => SameHeading(s.Heading, section.Heading));
                if (target >= 0) MergeCarried(carried, copySections[target].Nodes);
                else if (section.Heading == null) copySections.Insert(0, new Section(null, carried));
                else copySections.Add(new Section(section.Heading, carried));
            }
            if (moved) updatedPrevious = new Outline(previous.Title, Flatten(kept), previous.Schedule);
        }

        var title = $"{template.Title ?? name} — {label}";
        return (new Outline(title, Flatten(copySections)), updatedPrevious);
    }

    /// <summary>The outcome of <see cref="MakeDueCopies"/> for one template.</summary>
    public sealed record Result(string Template, string? Copy, string? Previous, string? Error);

    /// <summary>
    /// Makes each template's copy for the period containing <paramref name="date"/>, if it
    /// doesn't exist yet. Templates with no tasks written in them are skipped,
    /// so a new template isn't copied before it's been filled in.
    /// </summary>
    public static List<Result> MakeDueCopies(TaskLibrary templates, TaskLibrary lists, DateTime date, CultureInfo culture)
    {
        List<string> templatePaths;
        try { templatePaths = templates.Lists(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        HashSet<string> existing;
        try { existing = lists.Lists().Select(TaskLibrary.DisplayName).ToHashSet(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { existing = []; }
        var results = new List<Result>();
        foreach (var path in templatePaths)
        {
            var name = TaskLibrary.DisplayName(path);
            try
            {
                var template = new MarkdownFileStore(path).Load();
                if (template.Schedule is not RepeatSchedule schedule
                    || !template.Items.Any(i => i.Title.TrimWhitespace().Length > 0))
                    continue;

                var occurrence = schedule.Occurrence(date);
                var key = schedule.PeriodKey(occurrence);
                var copyName = CopyName(name, key);
                if (existing.Contains(copyName)) continue;

                string? previousPath = null;
                Outline? previous = null;
                if (schedule.CarriesOver)
                {
                    var prefix = name + " ";
                    var last = existing
                        .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
                        .Select(n => n[prefix.Length..])
                        .Where(k => schedule.IsPeriodKey(k) && string.CompareOrdinal(k, key) < 0)
                        .Max(StringComparer.Ordinal);
                    if (last != null)
                    {
                        previousPath = lists.PathForName(CopyName(name, last));
                        previous = new MarkdownFileStore(previousPath).Load();
                    }
                }

                var label = schedule.PeriodLabel(occurrence, culture);
                var made = MakeCopy(template, name, label, previous);
                var copyPath = lists.PathForName(copyName);
                // Write the copy first, so a failure can't lose moved tasks.
                new MarkdownFileStore(copyPath).Save(made.Copy);
                if (made.Previous is Outline updated && previousPath != null)
                    new MarkdownFileStore(previousPath).Save(updated);
                results.Add(new Result(path, copyPath, made.Previous == null ? null : previousPath, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                results.Add(new Result(path, null, null, $"Couldn't make today's copy of “{name}”: {e.Message}"));
            }
        }
        return results;
    }

    // MARK: - Trees

    /// <summary>An item with its subtree, for restructuring outlines.</summary>
    internal sealed class Node(TaskItem item, List<Node> children)
    {
        public TaskItem Item = item;
        public List<Node> Children = children;
        public Node Clone() => new(Item, Children.Select(c => c.Clone()).ToList());
    }

    /// <summary>A heading (null for the part before the first heading) and the tasks under it.</summary>
    internal sealed record Section(TaskItem? Heading, List<Node> Nodes);

    internal static List<Section> Sections(IReadOnlyList<TaskItem> items)
    {
        var sections = new List<Section>();
        var start = 0;
        TaskItem? heading = null;
        void Close(int end)
        {
            if (heading != null || end > start)
                sections.Add(new Section(heading, Forest(items, start, end)));
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (!items[i].IsHeading) continue;
            Close(i);
            heading = items[i];
            start = i + 1;
        }
        Close(items.Count);
        return sections;
    }

    private static List<Node> Forest(IReadOnlyList<TaskItem> items, int start, int endIndex)
    {
        var nodes = new List<Node>();
        var i = start;
        while (i < endIndex)
        {
            var end = i + 1;
            while (end < endIndex && items[end].Depth > items[i].Depth) end++;
            nodes.Add(new Node(items[i], Forest(items, i + 1, end)));
            i = end;
        }
        return nodes;
    }

    internal static List<TaskItem> Flatten(IEnumerable<Section> sections)
    {
        var items = new List<TaskItem>();
        void Add(Node node, int depth)
        {
            var item = node.Item;
            item.Depth = depth;
            items.Add(item);
            foreach (var child in node.Children) Add(child, depth + 1);
        }
        foreach (var section in sections)
        {
            if (section.Heading is TaskItem heading) items.Add(heading);
            foreach (var node in section.Nodes) Add(node, 0);
        }
        return items;
    }

    /// <summary>
    /// Splits <paramref name="node"/> into what stays behind and what moves on: an open task
    /// moves whole; a done one stays, minus its open subtasks, which move.
    /// </summary>
    private static (Node? Keep, List<Node> Carry) ExtractUnfinished(Node node)
    {
        if (node.Item.State == TaskState.Open) return (null, [node]);
        var keep = new Node(node.Item, []);
        var carry = new List<Node>();
        foreach (var child in node.Children)
        {
            var (k, c) = ExtractUnfinished(child);
            if (k != null) keep.Children.Add(k);
            carry.AddRange(c);
        }
        return (keep, carry);
    }

    /// <summary>
    /// Leaves out tasks with no text and no subtasks, such as the empty row
    /// left at the end of a list after typing it.
    /// </summary>
    private static List<Node> DropBlank(List<Node> nodes) =>
        nodes.Select(node => new Node(node.Item, DropBlank(node.Children))).Where(node => !IsBlank(node)).ToList();

    private static bool IsBlank(Node node) =>
        !node.Item.IsHeading && node.Children.Count == 0 && node.Item.Title.TrimWhitespace().Length == 0;

    /// <summary>
    /// Adds <paramref name="incoming"/> after <paramref name="nodes"/>, except that a task <paramref name="nodes"/> already has
    /// (by title) takes its done state if it's done there, and gets its
    /// subtasks folded in the same way.
    /// </summary>
    private static void Fold(List<Node> incoming, List<Node> nodes)
    {
        foreach (var node in incoming)
        {
            var i = nodes.FindIndex(n => SameTitle(n.Item, node.Item));
            if (i >= 0)
            {
                if (nodes[i].Item.State == TaskState.Open) nodes[i].Item.State = node.Item.State;
                Fold(node.Children, nodes[i].Children);
            }
            else nodes.Add(node);
        }
    }

    /// <summary>
    /// Adds <paramref name="carried"/> before <paramref name="nodes"/>, except that a task <paramref name="nodes"/> already has
    /// (by title) gets the carried one's subtasks merged into it instead.
    /// </summary>
    private static void MergeCarried(List<Node> carried, List<Node> nodes)
    {
        var fresh = new List<Node>();
        foreach (var node in carried)
        {
            var i = nodes.FindIndex(n => SameTitle(n.Item, node.Item));
            if (i >= 0) MergeCarried(node.Children, nodes[i].Children);
            else fresh.Add(node);
        }
        nodes.InsertRange(0, fresh);
    }

    private static bool SameTitle(TaskItem a, TaskItem b) => a.Title.TrimWhitespace() == b.Title.TrimWhitespace();

    private static bool SameHeading(TaskItem? a, TaskItem? b)
    {
        if (a is not TaskItem x || b is not TaskItem y) return a == null && b == null;
        return x.HeadingLevel == y.HeadingLevel && SameTitle(x, y);
    }
}
