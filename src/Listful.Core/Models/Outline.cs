using System.Runtime.InteropServices;

namespace Listful;

public enum TaskState
{
    Open,
    /// <summary>Completed; shown struck through.</summary>
    Done,
    /// <summary>
    /// Completed and tidied away; hidden unless "show decrufted" is on.
    /// Kept in the file unless the Delete decrufted tasks setting is on.
    /// </summary>
    Decrufted,
}

public record struct TaskItem
{
    public Guid Id;
    public string Title;
    public TaskState State;
    /// <summary>Nesting level; 0 is top level.</summary>
    public int Depth;
    /// <summary>When true, the item's descendants (or, for a heading, its section) are hidden.</summary>
    public bool IsCollapsed;
    /// <summary>
    /// 1–3 for a heading (<c>#</c>, <c>##</c>, <c>###</c>); null for a task. Headings are
    /// titles, not tasks: always top level, never done, and never parents.
    /// </summary>
    public int? HeadingLevel;
    /// <summary>
    /// A heading's attribute block from the file, e.g. <c>{#setup .wide}</c>
    /// (Pandoc, kramdown and others write these). Not shown, but written
    /// back after the heading so the file keeps it.
    /// </summary>
    public string? HeadingAttributes;

    public readonly bool IsHeading => HeadingLevel != null;

    public TaskItem(
        string title,
        TaskState state = TaskState.Open,
        int depth = 0,
        bool isCollapsed = false,
        int? headingLevel = null,
        string? headingAttributes = null,
        Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Title = title;
        State = state;
        Depth = depth;
        IsCollapsed = isCollapsed;
        HeadingLevel = headingLevel;
        HeadingAttributes = headingAttributes;
    }
}

/// <summary>
/// A task tree stored as a flat, pre-order list of items with depths.
/// <para>
/// Invariant: the first item has depth 0 and every item's depth is at most
/// one more than the item before it. An item's subtree is the run of
/// following items with a greater depth.
/// </para>
/// <para>
/// Headings have depth 0, and the item after a heading has depth 0 too, so a
/// heading never has children. Instead it heads a <em>section</em>: the items up to
/// the next heading of the same or a higher level (<see cref="SectionEnd"/>).
/// </para>
/// Swift's <c>Outline</c> is a value type; here it is a class, so take a
/// <see cref="Clone"/> where the Swift code relies on copying.
/// </summary>
public sealed class Outline : IEquatable<Outline>
{
    private List<TaskItem> items;

    public IReadOnlyList<TaskItem> Items => items;
    /// <summary>The list's own title, stored as a <c>#</c> heading on the file's first line.
    /// Not an item; null when the file doesn't start with one.</summary>
    public string? Title { get; set; }
    /// <summary>Set when this list is a repeating checklist's template.</summary>
    public RepeatSchedule? Schedule { get; set; }
    /// <summary>The title's attribute block, if the file has one (see <see cref="TaskItem.HeadingAttributes"/>).</summary>
    public string? TitleAttributes { get; set; }

    public Outline(string? title = null, IEnumerable<TaskItem>? items = null, RepeatSchedule? schedule = null, string? titleAttributes = null)
    {
        Title = title;
        TitleAttributes = titleAttributes;
        this.items = items?.ToList() ?? [];
        Schedule = schedule;
        Normalize();
    }

    private void Normalize()
    {
        var span = CollectionsMarshal.AsSpan(items);
        var previousDepth = -1;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i].HeadingLevel is int level)
            {
                span[i].HeadingLevel = Math.Min(Math.Max(level, 1), 3);
                span[i].Depth = 0;
                span[i].State = TaskState.Open;
                previousDepth = -1;
                continue;
            }
            var depth = Math.Min(Math.Max(span[i].Depth, 0), previousDepth + 1);
            span[i].Depth = depth;
            previousDepth = depth;
        }
    }

    /// <summary>A copy that can be changed without changing this one.</summary>
    public Outline Clone() => new(Title, items, Schedule, TitleAttributes);

    /// <summary>Writable access to an item in place.</summary>
    private ref TaskItem At(int index) => ref CollectionsMarshal.AsSpan(items)[index];

    public int Count => items.Count;
    public bool IsEmpty => items.Count == 0;

    public int? IndexOf(Guid id)
    {
        var i = items.FindIndex(item => item.Id == id);
        return i < 0 ? null : i;
    }

    // MARK: - Structure queries

    /// <summary>Index one past the end of the subtree rooted at <paramref name="index"/>.</summary>
    public int SubtreeEnd(int index)
    {
        var depth = items[index].Depth;
        var end = index + 1;
        while (end < items.Count && items[end].Depth > depth) end++;
        return end;
    }

    public bool HasChildren(int index) =>
        index + 1 < items.Count && items[index + 1].Depth > items[index].Depth;

    /// <summary>For a heading: one past the last item of its section.</summary>
    public int SectionEnd(int index)
    {
        if (items[index].HeadingLevel is not int level) return SubtreeEnd(index);
        var end = index + 1;
        while (end < items.Count)
        {
            if (items[end].HeadingLevel is int other && other <= level) break;
            end++;
        }
        return end;
    }

    /// <summary>Whether collapsing the item would hide anything.</summary>
    public bool CanCollapse(int index) =>
        items[index].IsHeading ? SectionEnd(index) > index + 1 : HasChildren(index);

    public int? ParentIndex(int index)
    {
        var depth = items[index].Depth;
        if (depth <= 0) return null;
        for (var i = index - 1; i >= 0; i--)
            if (items[i].Depth == depth - 1) return i;
        return null;
    }

    /// <summary>The nearest earlier item with the same parent, if any.</summary>
    public int? PreviousSiblingIndex(int index)
    {
        var depth = items[index].Depth;
        for (var i = index - 1; i >= 0; i--)
        {
            if (items[i].Depth == depth) return i;
            if (items[i].Depth < depth) return null;
        }
        return null;
    }

    /// <summary>
    /// Indices of items that should be displayed: anything under a collapsed
    /// item is hidden, and decrufted items (with their subtrees) are hidden
    /// unless <paramref name="showDecrufted"/> is set.
    /// </summary>
    public List<int> VisibleIndices(bool showDecrufted)
    {
        var result = new List<int>();
        int? hiddenBelowDepth = null;
        int? hiddenUnderHeadingLevel = null;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (hiddenUnderHeadingLevel is int hidden)
            {
                if (item.HeadingLevel is not int level || level > hidden) continue;
                hiddenUnderHeadingLevel = null;
            }
            if (hiddenBelowDepth is int below)
            {
                if (item.Depth > below) continue;
                hiddenBelowDepth = null;
            }
            if (item.State == TaskState.Decrufted && !showDecrufted)
            {
                hiddenBelowDepth = item.Depth;
                continue;
            }
            result.Add(i);
            if (item.IsCollapsed)
            {
                if (item.HeadingLevel is int level) hiddenUnderHeadingLevel = level;
                else if (HasChildren(i)) hiddenBelowDepth = item.Depth;
            }
        }
        return result;
    }

    // MARK: - Editing

    public void SetTitle(string title, int index) => At(index).Title = title;

    /// <summary>Appends an empty top-level task and returns its index.</summary>
    public int AppendItem()
    {
        items.Add(new TaskItem(""));
        Reveal(items.Count - 1);
        return items.Count - 1;
    }

    /// <summary>
    /// Inserts an empty task "after" <paramref name="index"/> the way an outliner does on Return:
    /// as the first child if the item has expanded children, otherwise as the
    /// next sibling. Returns the new item's index.
    /// </summary>
    public int InsertItem(int index)
    {
        var item = items[index];
        if (HasChildren(index) && !item.IsCollapsed)
        {
            items.Insert(index + 1, new TaskItem("", depth: item.Depth + 1));
            return index + 1;
        }
        var position = SubtreeEnd(index);
        items.Insert(position, new TaskItem("", depth: item.Depth));
        Reveal(position);
        return position;
    }

    /// <summary>
    /// Return in a row whose selection is <paramref name="selection"/> (UTF-16 offsets into
    /// the title; null means the caret is at the end). Returns the row to
    /// focus and where its caret goes (null: at the end).
    /// <list type="bullet">
    /// <item>An empty, childless nested task moves out a level, so Return twice steps back out.</item>
    /// <item>Otherwise the selection is deleted and the title split there: the
    /// text after it becomes a new task after this one. At the very start,
    /// an empty task goes in above instead, so the row keeps its children.</item>
    /// </list>
    /// </summary>
    public (int Index, int? Caret) ReturnKey(int index, IntRange? selection = null)
    {
        var title = items[index].Title;
        if (title.Length == 0 && !HasChildren(index) && Outdent(index) is int newIndex)
            return (newIndex, null);
        var lower = Math.Min(Math.Max(selection?.Lower ?? title.Length, 0), title.Length);
        var upper = Math.Min(Math.Max(selection?.Upper ?? title.Length, lower), title.Length);
        var head = title[..lower];
        var tail = title[upper..];

        if (head.Length == 0 && tail.Length != 0)
        {
            At(index).Title = tail;
            items.Insert(index, new TaskItem("", depth: items[index].Depth));
            return (index + 1, 0);
        }
        At(index).Title = head;
        var inserted = InsertItem(index);
        At(inserted).Title = tail;
        return (inserted, 0);
    }

    /// <summary>
    /// Makes the item (with its subtree) the last child of its previous sibling.
    /// Returns false if there is no previous sibling, or either is a heading.
    /// </summary>
    public bool Indent(int index)
    {
        if (items[index].IsHeading || PreviousSiblingIndex(index) is not int parent || items[parent].IsHeading)
            return false;
        At(parent).IsCollapsed = false;
        var end = SubtreeEnd(index);
        for (var i = index; i < end; i++) At(i).Depth += 1;
        return true;
    }

    /// <summary>
    /// Moves the item (with its subtree) out one level, placing it after its
    /// parent's subtree. Following siblings stay with the old parent.
    /// Returns the item's new index, or null if it is already top level.
    /// </summary>
    public int? Outdent(int index)
    {
        if (ParentIndex(index) is not int parent) return null;
        var parentEnd = SubtreeEnd(parent);
        var end = SubtreeEnd(index);
        var block = items.GetRange(index, end - index);
        var span = CollectionsMarshal.AsSpan(block);
        for (var i = 0; i < span.Length; i++) span[i].Depth -= 1;
        items.RemoveRange(index, end - index);
        var destination = parentEnd - block.Count;
        items.InsertRange(destination, block);
        return destination;
    }

    /// <summary>
    /// The tasks a Sort on the item at <paramref name="index"/> reorders: the level it's on,
    /// i.e. the task and its siblings (for a top-level task, those between
    /// the headings either side of it); for a heading, the tasks under it up
    /// to the next heading. Null if that's fewer than two.
    /// </summary>
    public IntRange? SortRange(int index)
    {
        IntRange range;
        if (items[index].IsHeading)
            range = new(index + 1, NextHeading(index));
        else if (ParentIndex(index) is int parent)
            range = new(parent + 1, SubtreeEnd(parent));
        else
        {
            var start = 0;
            for (var i = index - 1; i >= 0; i--)
                if (items[i].IsHeading) { start = i + 1; break; }
            range = new(start, NextHeading(index));
        }
        if (range.IsEmpty) return null;
        var depth = items[range.Lower].Depth;
        return range.Indices().Count(i => items[i].Depth == depth) > 1 ? range : null;
    }

    private int NextHeading(int index)
    {
        for (var i = index + 1; i < items.Count; i++)
            if (items[i].IsHeading) return i;
        return items.Count;
    }

    /// <summary>
    /// Sorts the tasks <see cref="SortRange"/> picks by title, A–Z the way Finder
    /// does (ignoring case, numbers by value, so "2" comes before "10").
    /// Each task keeps its subtasks; untitled tasks go last. Returns whether
    /// the order changed.
    /// </summary>
    public bool SortTasks(int index)
    {
        if (SortRange(index) is not IntRange range) return false;
        var depth = items[range.Lower].Depth;
        var blocks = new List<List<TaskItem>>();
        var start = range.Lower;
        for (var i = range.Lower + 1; i < range.Upper; i++)
        {
            if (items[i].Depth != depth) continue;
            blocks.Add(items.GetRange(start, i - start));
            start = i;
        }
        blocks.Add(items.GetRange(start, range.Upper - start));
        // Stable, so equal titles keep their order.
        var sorted = blocks
            .Select((block, offset) => (block, offset))
            .OrderBy(x => x, Comparer<(List<TaskItem> block, int offset)>.Create((a, b) =>
            {
                string x = a.block[0].Title, y = b.block[0].Title;
                if ((x.Length == 0) != (y.Length == 0)) return x.Length == 0 ? 1 : -1;
                var order = x.LocalizedStandardCompare(y);
                return order != 0 ? order : a.offset.CompareTo(b.offset);
            }))
            .SelectMany(x => x.block)
            .ToList();
        if (sorted.Select(i => i.Id).SequenceEqual(items.GetRange(range.Lower, range.Count).Select(i => i.Id)))
            return false;
        items.RemoveRange(range.Lower, range.Count);
        items.InsertRange(range.Lower, sorted);
        return true;
    }

    /// <summary>
    /// Swaps the item's subtree with its previous sibling's. Returns the new index.
    /// A top-level task can swap with a heading, moving it into the section
    /// above. Headings themselves don't move this way.
    /// </summary>
    public int? MoveUp(int index)
    {
        if (items[index].IsHeading || PreviousSiblingIndex(index) is not int sibling) return null;
        var end = SubtreeEnd(index);
        var moved = items.GetRange(index, end - index).Concat(items.GetRange(sibling, index - sibling)).ToList();
        items.RemoveRange(sibling, end - sibling);
        items.InsertRange(sibling, moved);
        Reveal(sibling);
        return sibling;
    }

    /// <summary>Swaps the item's subtree with its next sibling's. Returns the new index.</summary>
    public int? MoveDown(int index)
    {
        if (items[index].IsHeading) return null;
        var end = SubtreeEnd(index);
        if (end >= items.Count || items[end].Depth != items[index].Depth) return null;
        var nextEnd = SubtreeEnd(end);
        var moved = items.GetRange(end, nextEnd - end).Concat(items.GetRange(index, end - index)).ToList();
        items.RemoveRange(index, nextEnd - index);
        items.InsertRange(index, moved);
        var newIndex = index + (nextEnd - end);
        Reveal(newIndex);
        return newIndex;
    }

    /// <summary>
    /// The depths the subtree at <paramref name="index"/> could take if moved to just before
    /// <paramref name="destination"/> (an index into <see cref="Items"/> as they are now; <c>Count</c>
    /// means the end). Null for a heading, or a destination inside the subtree.
    /// The range runs from the depth of the item that would follow (so it
    /// isn't adopted as a child) to one deeper than the item that would precede.
    /// </summary>
    public ClosedIntRange? MoveDepths(int index, int destination)
    {
        var end = SubtreeEnd(index);
        if (items[index].IsHeading || !(destination <= index || destination >= end) || destination > items.Count)
            return null;
        var previous = destination == end ? index - 1 : destination - 1;
        var next = destination == index ? end : destination;
        var maxDepth = previous < 0 || items[previous].IsHeading ? 0 : items[previous].Depth + 1;
        var minDepth = next < items.Count && !items[next].IsHeading ? items[next].Depth : 0;
        return new(minDepth, maxDepth);
    }

    /// <summary>
    /// The gap a dragged row is dropped into, in terms of the visible rows
    /// either side of it (indices into <see cref="Items"/>, neither inside the dragged
    /// subtree): where the subtree would go and the depths it may take.
    /// Beyond <see cref="MoveDepths"/>, the depth is capped by the visible row above, so a
    /// drop below a collapsed task can make it that task's last child but
    /// can't burrow deeper into what's hidden.
    /// </summary>
    public (int Destination, ClosedIntRange Depths)? DropTarget(int moving, int? afterVisible, int? beforeVisible)
    {
        var destination = beforeVisible ?? items.Count;
        if (MoveDepths(moving, destination) is not ClosedIntRange depths) return null;
        var visibleMax = afterVisible is int previous ? (items[previous].IsHeading ? 0 : items[previous].Depth + 1) : 0;
        var capped = new ClosedIntRange(depths.Lower, Math.Max(depths.Lower, Math.Min(depths.Upper, visibleMax)));
        return (destination, capped);
    }

    /// <summary>
    /// Moves the item and its subtree to just before <paramref name="destination"/> (see
    /// <see cref="MoveDepths"/>), at <paramref name="depth"/>, keeping the subtree's shape. Collapsed
    /// parents and headings it lands under are expanded so it stays visible.
    /// Returns the item's new index, or null if the move isn't allowed.
    /// </summary>
    public int? Move(int index, int destination, int depth)
    {
        if (MoveDepths(index, destination) is not ClosedIntRange depths || !depths.Contains(depth)) return null;
        var end = SubtreeEnd(index);
        var block = items.GetRange(index, end - index);
        var shift = depth - block[0].Depth;
        var span = CollectionsMarshal.AsSpan(block);
        for (var i = 0; i < span.Length; i++) span[i].Depth += shift;
        items.RemoveRange(index, end - index);
        var newIndex = destination > index ? destination - block.Count : destination;
        items.InsertRange(newIndex, block);
        var ancestor = ParentIndex(newIndex);
        while (ancestor is int a)
        {
            At(a).IsCollapsed = false;
            ancestor = ParentIndex(a);
        }
        Reveal(newIndex);
        return newIndex;
    }

    /// <summary>
    /// Turns the item into a heading of <paramref name="level"/>. A nested task is first
    /// outdented to the top level, and its children move up a level since
    /// headings can't have children. Returns the item's new index.
    /// </summary>
    public int MakeHeading(int level, int index)
    {
        if (!items[index].IsHeading)
        {
            while (items[index].Depth > 0 && Outdent(index) is int outdented) index = outdented;
            var end = SubtreeEnd(index);
            for (var i = index + 1; i < end; i++) At(i).Depth -= 1;
            At(index).State = TaskState.Open;
            At(index).IsCollapsed = false;
        }
        At(index).HeadingLevel = Math.Min(Math.Max(level, 1), 3);
        return index;
    }

    /// <summary>Turns a heading back into a top-level task.</summary>
    public void MakeTask(int index)
    {
        if (!items[index].IsHeading) return;
        At(index).HeadingLevel = null;
        At(index).IsCollapsed = false;
    }

    /// <summary>
    /// Expands any collapsed headings whose section contains <paramref name="index"/>, so an
    /// item that was just created or moved there doesn't vanish.
    /// </summary>
    private void Reveal(int index)
    {
        var nearestLevel = int.MaxValue;
        for (var i = index - 1; i >= 0 && nearestLevel > 1; i--)
        {
            if (items[i].HeadingLevel is int level && level < nearestLevel)
            {
                At(i).IsCollapsed = false;
                nearestLevel = level;
            }
        }
    }

    /// <summary>Removes the item and its subtree.</summary>
    public void RemoveSubtree(int index) => items.RemoveRange(index, SubtreeEnd(index) - index);

    /// <summary>
    /// Delete at the start of a task: appends its title to <paramref name="previous"/> (the
    /// row shown above it) and removes it. Its subtasks become the last
    /// subtasks of <paramref name="previous"/>, or stay where they were if they're already
    /// inside it; under a heading they're top-level. Returns the offset
    /// (UTF-16) where the two titles meet, for the caret.
    /// </summary>
    public int Join(int index, int previous)
    {
        if (!(previous < index && !items[index].IsHeading))
            throw new ArgumentException("Join needs an earlier row and a task to join.");
        var item = items[index];
        var caret = items[previous].Title.Length;
        var end = SubtreeEnd(index);
        var children = items.GetRange(index + 1, end - index - 1);
        var isInside = items[previous].IsHeading || index < SubtreeEnd(previous);
        items.RemoveRange(index, end - index);
        At(previous).Title += item.Title;
        if (children.Count == 0) return caret;

        var @base = items[previous].IsHeading ? 0 : items[previous].Depth + 1;
        var span = CollectionsMarshal.AsSpan(children);
        for (var k = 0; k < span.Length; k++) span[k].Depth += @base - (item.Depth + 1);
        var at = isInside ? index : SubtreeEnd(previous);
        items.InsertRange(at, children);
        if (!items[previous].IsHeading) At(previous).IsCollapsed = false;
        return caret;
    }

    /// <summary>The item at <paramref name="index"/> with its subtree, moved up to the top level.</summary>
    public List<TaskItem> SubtreeItems(int index)
    {
        var @base = items[index].Depth;
        return items.GetRange(index, SubtreeEnd(index) - index)
            .Select(item => item with { Depth = item.Depth - @base })
            .ToList();
    }

    /// <summary>
    /// Adds a top-level item and its subtree (as from <see cref="SubtreeItems"/>) at the
    /// end. A blank last task, the row left for typing into, stays last.
    /// </summary>
    public void AppendSubtree(IReadOnlyList<TaskItem> subtree)
    {
        if (subtree.Count == 0) return;
        var @base = subtree[0].Depth;
        var added = subtree.Select(item => item with { Id = Guid.NewGuid(), Depth = item.Depth - @base }).ToList();
        var end = items.Count;
        if (items.Count > 0 && items[^1] is { IsHeading: false, Depth: 0, Title.Length: 0 }) end -= 1;
        items.InsertRange(end, added);
    }

    /// <summary>Open becomes done; done or decrufted becomes open.</summary>
    public void ToggleDone(int index)
    {
        if (items[index].IsHeading) return;
        At(index).State = items[index].State == TaskState.Open ? TaskState.Done : TaskState.Open;
    }

    public void SetCollapsed(bool collapsed, int index) => At(index).IsCollapsed = collapsed;

    /// <summary>
    /// Gives items the ids of their counterparts in <paramref name="old"/>, so a reloaded
    /// outline keeps row identity (and the row being edited keeps focus).
    /// Items are matched as a longest common subsequence on depth, kind and
    /// title; between matches, runs of the same length pair up in order,
    /// which covers rows whose title was edited.
    /// </summary>
    public void AdoptIdentities(Outline old)
    {
        static string Key(TaskItem item) => $"{item.Depth}|{item.HeadingLevel ?? 0}|{item.Title}";
        int previousOld = 0, previousNew = 0;
        var pairs = Diff.Matches(old.items.Select(Key).ToList(), items.Select(Key).ToList());
        pairs.Add((old.items.Count, items.Count));
        foreach (var (o, n) in pairs)
        {
            if (o - previousOld == n - previousNew)
                for (var k = 0; k < n - previousNew; k++) At(previousNew + k).Id = old.items[previousOld + k].Id;
            if (n < items.Count) At(n).Id = old.items[o].Id;
            previousOld = o + 1;
            previousNew = n + 1;
        }
    }

    /// <summary>Moves every done task to decrufted. Returns how many changed.</summary>
    public int DecruftDone()
    {
        var count = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].State != TaskState.Done) continue;
            At(i).State = TaskState.Decrufted;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Deletes every done and decrufted task, for decrufting by deleting.
    /// Open tasks under a deleted one are kept, moved up to its place.
    /// Returns how many were deleted.
    /// </summary>
    public int DeleteDone()
    {
        var kept = new List<TaskItem>();
        // Depths of the deleted tasks enclosing the current item.
        var deleted = new List<int>();
        foreach (var original in items)
        {
            var item = original;
            while (deleted.Count > 0 && deleted[^1] >= item.Depth) deleted.RemoveAt(deleted.Count - 1);
            if (!item.IsHeading && item.State != TaskState.Open)
            {
                deleted.Add(item.Depth);
                continue;
            }
            item.Depth -= deleted.Count;
            kept.Add(item);
        }
        var count = items.Count - kept.Count;
        items = kept;
        return count;
    }

    // MARK: - Blocks of rows

    /// <summary>
    /// The items a selection of rows from <paramref name="first"/> to <paramref name="last"/> covers: those
    /// rows plus everything hidden under them (subtasks, and the section of
    /// a collapsed heading).
    /// </summary>
    public IntRange BlockRange(int first, int last)
    {
        var end = last + 1;
        for (var i = first; i <= last; i++)
        {
            var itemEnd = items[i].IsHeading
                ? (items[i].IsCollapsed ? SectionEnd(i) : i + 1)
                : SubtreeEnd(i);
            end = Math.Max(end, itemEnd);
        }
        return new(first, end);
    }

    /// <summary>The items in <paramref name="range"/>, moved out so the shallowest is top level.</summary>
    public List<TaskItem> BlockItems(IntRange range)
    {
        if (range.IsEmpty) return [];
        var slice = items.GetRange(range.Lower, range.Count);
        var @base = slice.Min(i => i.Depth);
        return slice.Select(item => item with { Depth = item.Depth - @base }).ToList();
    }

    /// <summary>
    /// Removes the items in <paramref name="range"/>. Anything after it that would now be
    /// nested too deeply moves out to fit.
    /// </summary>
    public void RemoveBlock(IntRange range)
    {
        items.RemoveRange(range.Lower, range.Count);
        Normalize();
    }

    /// <summary>
    /// Inserts <paramref name="block"/> (as from <see cref="BlockItems"/>, with fresh ids) where a new
    /// task after <paramref name="index"/> would go, at that task's depth. If the row at
    /// <paramref name="index"/> is an empty task with nothing under it, the block replaces
    /// it. Returns the range the block now occupies.
    /// </summary>
    public IntRange InsertBlock(IReadOnlyList<TaskItem> block, int index)
    {
        if (block.Count == 0) return new(index, index);
        var row = items[index];
        if (!row.IsHeading && row.Title.Length == 0 && !HasChildren(index))
            return ReplaceBlock(new(index, index + 1), block);
        int position, depth;
        if (row.IsHeading)
            (position, depth) = (row.IsCollapsed ? SectionEnd(index) : index + 1, 0);
        else if (HasChildren(index) && !row.IsCollapsed)
            (position, depth) = (index + 1, row.Depth + 1);
        else
            (position, depth) = (SubtreeEnd(index), row.Depth);
        return Place(block, new(position, position), depth);
    }

    /// <summary>
    /// Replaces the items in <paramref name="range"/> with <paramref name="block"/> (with fresh ids), at the
    /// depth of the first item replaced. Returns the range the block occupies.
    /// </summary>
    public IntRange ReplaceBlock(IntRange range, IReadOnlyList<TaskItem> block) =>
        Place(block, range, items[range.Lower].Depth);

    private IntRange Place(IReadOnlyList<TaskItem> block, IntRange range, int depth)
    {
        var @base = block.Count == 0 ? 0 : block.Min(i => i.Depth);
        var added = block.Select(item => item with { Id = Guid.NewGuid(), Depth = item.Depth + depth - @base }).ToList();
        items.RemoveRange(range.Lower, range.Count);
        items.InsertRange(range.Lower, added);
        Normalize();
        if (added.Count > 0) Reveal(range.Lower);
        return new(range.Lower, range.Lower + added.Count);
    }

    /// <summary>Marks the tasks at <paramref name="indices"/> done, or open again.</summary>
    public void SetDone(bool done, IEnumerable<int> indices)
    {
        foreach (var i in indices)
        {
            if (items[i].IsHeading) continue;
            if (done)
            {
                if (items[i].State == TaskState.Open) At(i).State = TaskState.Done;
            }
            else
            {
                At(i).State = TaskState.Open;
            }
        }
    }

    // MARK: - Equality (Swift's synthesized Equatable)

    public bool Equals(Outline? other) =>
        other is not null
        && Title == other.Title
        && TitleAttributes == other.TitleAttributes
        && Equals(Schedule, other.Schedule)
        && items.SequenceEqual(other.items);

    public override bool Equals(object? obj) => Equals(obj as Outline);
    public override int GetHashCode() => HashCode.Combine(Title, items.Count);
    public static bool operator ==(Outline? a, Outline? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(Outline? a, Outline? b) => !(a == b);
}
