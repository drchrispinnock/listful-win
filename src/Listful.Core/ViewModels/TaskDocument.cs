namespace Listful;

/// <summary>
/// One open task list: owns the outline, persists it to a <see cref="MarkdownFileStore"/>, and
/// exposes id-based editing actions for the views.
/// <para>
/// Windows differences: upstream is <c>@Observable</c>; here the view listens to
/// <see cref="Changed"/> and redraws. Work that upstream runs in Swift <c>Task</c>s is
/// <c>async</c>, resuming on the synchronization context of the caller (the UI thread in the
/// app). The clipboard is the view's business: <see cref="CopySelection"/> returns the two
/// forms to put on it, and pastes pass in what was on it.
/// </para>
/// </summary>
public sealed class TaskDocument : IDisposable
{
    public Outline Outline { get; private set; } = new();
    public string FilePath { get; }
    private readonly Preferences preferences;

    /// <summary>The row the user is working on; menu commands act on it.</summary>
    public Guid? FocusedID { get; private set; }
    /// <summary>A one-shot request for a row's text field to take keyboard focus.
    /// Cleared once that field has focus, so it can't steal focus back later.</summary>
    public Guid? FocusRequestID { get; private set; }
    /// <summary>Where the requested field puts its caret (UTF-16 offset); null is the end.</summary>
    public int? FocusRequestCaret { get; private set; }
    /// <summary>
    /// Whether to show the title field: only when the file has a <c>#</c> title.
    /// Stays on if the title is cleared, so the field doesn't vanish while
    /// being edited; it's re-evaluated on load.
    /// </summary>
    public bool ShowsListTitle { get; private set; }

    private string? errorMessage;
    public string? ErrorMessage
    {
        get => errorMessage;
        set { errorMessage = value; RaiseChanged(); }
    }

    public abstract record LoadStateValue
    {
        public sealed record Loaded : LoadStateValue;
        /// <summary>Waiting for OneDrive or another sync client to bring the file to this computer.</summary>
        public sealed record Downloading : LoadStateValue;
        /// <summary>The file couldn't be read; nothing is saved until it can be.</summary>
        public sealed record Failed(string Message) : LoadStateValue;
    }

    /// <summary>Until this is <c>Loaded</c> the outline isn't the file's, so it's neither
    /// shown nor saved: saving it would replace the list with an empty one.</summary>
    public LoadStateValue LoadState { get; private set; } = new LoadStateValue.Loaded();
    public bool IsLoaded => LoadState is LoadStateValue.Loaded;

    /// <summary>Raised after anything the view shows has changed.</summary>
    public event Action? Changed;

    private readonly MarkdownFileStore store;
    private CancellationTokenSource? pendingSave;
    /// <summary>The file's text as last read or written here. If the file no longer
    /// matches it, someone else (another app, or another computer via a synced folder) has changed it.</summary>
    private string? syncedText;
    private FileWatcher? watcher;
    private CancellationTokenSource? loadTask;

    /// <summary>How long after an edit the list is saved. Tests shorten it.</summary>
    public TimeSpan SaveDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    public TaskDocument(string filePath, Preferences preferences, bool watch = true)
    {
        FilePath = filePath;
        this.preferences = preferences;
        store = new MarkdownFileStore(filePath);
        Load();
        if (watch) watcher = new FileWatcher(filePath, CheckDisk);
    }

    public void Dispose()
    {
        watcher?.Dispose();
        watcher = null;
        loadTask?.Cancel();
        pendingSave?.Cancel();
    }

    private void RaiseChanged() => Changed?.Invoke();

    public string FileName => TaskLibrary.DisplayName(FilePath);

    /// <summary>What to call the list: its <c>#</c> title if it has one, else the file name.</summary>
    public string DisplayTitle =>
        Outline.Title is string title && title.TrimWhitespace().Length > 0 ? title : FileName;

    /// <summary>Sets the list's title; an empty title removes the <c>#</c> line from the file.</summary>
    public void SetListTitle(string title)
    {
        string? newTitle = title.Length == 0 ? null : title;
        if (newTitle == Outline.Title) return;
        if (newTitle != null) ShowsListTitle = true;
        Mutate(o => o.Title = newTitle);
    }

    // MARK: - Repeating checklists

    /// <summary>Changes the template's schedule.</summary>
    public void CommitSchedule(RepeatSchedule schedule)
    {
        if (Equals(schedule, Outline.Schedule)) return;
        Mutate(o => o.Schedule = schedule.Copy());
    }

    // MARK: - Rows

    public bool ShowDecrufted => preferences.ShowDecrufted;

    public List<TaskItem> VisibleItems =>
        Outline.VisibleIndices(ShowDecrufted).Select(i => Outline.Items[i]).ToList();

    public bool CanCollapse(Guid id) => Outline.IndexOf(id) is int i && Outline.CanCollapse(i);

    // MARK: - Editing

    public void SetTitle(string title, Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        var old = Outline.Items[i];
        if (old.Title == title) return;

        // Typing "# ", "## " or "### " at the start of a task makes it a heading.
        if (!old.IsHeading && MarkdownCodec.ParseHeading(title) is (int level, string text))
        {
            var prefix = new string('#', level) + " ";
            if (title.StartsWith(prefix, StringComparison.Ordinal) && !old.Title.StartsWith(prefix, StringComparison.Ordinal))
            {
                Mutate(o =>
                {
                    var index = o.MakeHeading(level, i);
                    o.SetTitle(text, index);
                });
                RequestFocus(id);
                return;
            }
        }
        // Typing doesn't redraw the rows (the field already shows it), so no Changed.
        Mutate(o => o.SetTitle(title, i), notify: false);
    }

    public void MakeHeading(int level, Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        Mutate(o => o.MakeHeading(level, i));
        RequestFocus(id);
    }

    public void MakeTask(Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        Mutate(o => o.MakeTask(i));
        RequestFocus(id);
    }

    /// <summary>Return in a row: splits it at the selection into a new task, or
    /// moves an empty nested one out a level.</summary>
    public void ReturnKey(Guid id, IntRange selection)
    {
        if (Outline.IndexOf(id) is not int i) return;
        (int Index, int? Caret) target = (0, null);
        Mutate(o => target = o.ReturnKey(i, selection));
        RequestFocus(Outline.Items[target.Index].Id, target.Caret);
    }

    public void AppendTask()
    {
        var newIndex = 0;
        Mutate(o => newIndex = o.AppendItem());
        RequestFocus(Outline.Items[newIndex].Id);
    }

    // Structural moves can make the view re-create the row's text field, which
    // drops keyboard focus, so each one re-requests focus for the moved row.

    public void Indent(Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        Mutate(o => o.Indent(i));
        RequestFocus(id, keepCaret: true);
    }

    public void Outdent(Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        Mutate(o => o.Outdent(i));
        RequestFocus(id, keepCaret: true);
    }

    public void MoveUp(Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        Mutate(o => o.MoveUp(i));
        RequestFocus(id, keepCaret: true);
    }

    public void MoveDown(Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        Mutate(o => o.MoveDown(i));
        RequestFocus(id, keepCaret: true);
    }

    /// <summary>
    /// Sorts A–Z the task and its siblings, or a heading's tasks (see
    /// <see cref="Outline.SortRange"/>); with no task given, the top level. Collapsed
    /// rows stay collapsed.
    /// </summary>
    public void Sort(Guid? id)
    {
        if (Outline.IsEmpty) return;
        var i = id is Guid g && Outline.IndexOf(g) is int found ? found : 0;
        var sorted = Outline.Clone();
        if (!sorted.SortTasks(i)) return;
        Mutate(o => Outline = sorted);
        if (id is Guid focused) RequestFocus(focused);
    }

    public void ToggleDone(Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        Mutate(o => o.ToggleDone(i));
    }

    // MARK: - Dragging rows

    /// <summary>A row being dragged by its bullet, with where it would land.</summary>
    public sealed record RowDrag(Guid Id, IReadOnlySet<Guid> MovingIDs, int Destination, int Depth, double GapY, string? TargetList)
    {
        public bool Equals(RowDrag? other) =>
            other is not null && Id == other.Id && Destination == other.Destination && Depth == other.Depth
            && GapY == other.GapY && TargetList == other.TargetList;
        public override int GetHashCode() => HashCode.Combine(Id, Destination, Depth, GapY, TargetList);
    }

    public RowDrag? Drag { get; private set; }

    /// <summary>
    /// Follows a drag of <paramref name="id"/>'s bullet. Over another list in the sidebar
    /// (<paramref name="targetList"/>), the row would move there. Otherwise the gap is the one nearest
    /// the pointer (<paramref name="y"/>, in the outline's coordinates, which <paramref name="rowFrames"/>
    /// also uses: each visible row's top and bottom), among the rows outside the dragged subtree, and
    /// dragging sideways by an indent changes the depth by one.
    /// </summary>
    public void UpdateDrag(Guid id, double y, double translationX, double indentWidth,
        IReadOnlyDictionary<Guid, (double Top, double Bottom)> rowFrames, string? targetList)
    {
        if (Outline.IndexOf(id) is not int index) return;
        var end = Outline.SubtreeEnd(index);
        var movingIDs = Drag?.Id == id
            ? Drag.MovingIDs
            : Enumerable.Range(index, end - index).Select(k => Outline.Items[k].Id).ToHashSet();

        if (targetList != null && !PathUtility.SamePath(targetList, FilePath))
        {
            SetDrag(new RowDrag(id, movingIDs, index, 0, 0, targetList));
            return;
        }

        var rows = Outline.VisibleIndices(ShowDecrufted).Where(i => i < index || i >= end).ToList();
        (double Top, double Bottom)? Frame(int i) => rowFrames.TryGetValue(Outline.Items[i].Id, out var f) ? f : null;

        var nextPosition = rows.FindIndex(i => Frame(i) is { } f && (f.Top + f.Bottom) / 2 > y);
        int? next = nextPosition >= 0 ? rows[nextPosition] : null;
        var count = nextPosition >= 0 ? nextPosition : rows.Count;
        int? previous = count > 0 ? rows[count - 1] : null;
        if (Outline.DropTarget(index, previous, next) is not var (destination, depths))
        {
            // Back from the sidebar to a spot it can't drop on: no target.
            if (Drag?.TargetList != null) SetDrag(null);
            return;
        }

        var wanted = Outline.Items[index].Depth + (int)Math.Round(translationX / indentWidth, MidpointRounding.AwayFromZero);
        var depth = Math.Min(Math.Max(wanted, depths.Lower), depths.Upper);
        var gapY = next is int n && Frame(n) is { } nf ? nf.Top
            : previous is int p && Frame(p) is { } pf ? pf.Bottom : 0;
        SetDrag(new RowDrag(id, movingIDs, destination, depth, gapY, null));
    }

    private void SetDrag(RowDrag? drag)
    {
        if (Equals(drag, Drag)) return;
        Drag = drag;
        RaiseChanged();
    }

    public void CancelDrag() => SetDrag(null);

    /// <summary>Drops the dragged row where <see cref="UpdateDrag"/> last put it.</summary>
    public void EndDrag()
    {
        if (Drag is not RowDrag drag) return;
        Drag = null;
        if (drag.TargetList is string target)
        {
            RaiseChanged();
            _ = MoveToList(drag.Id, target);
            return;
        }
        if (Outline.IndexOf(drag.Id) is not int i)
        {
            RaiseChanged();
            return;
        }
        Mutate(o => o.Move(i, drag.Destination, drag.Depth));
    }

    /// <summary>
    /// Moves a task and its subtasks to the end of another list. The other
    /// list is written first, off the UI thread (a sync client can stall a write),
    /// and the task is only removed here once that has worked.
    /// </summary>
    public async Task MoveToList(Guid id, string target)
    {
        if (Outline.IndexOf(id) is not int index || Outline.Items[index].IsHeading) return;
        var subtree = Outline.SubtreeItems(index);
        var error = await Task.Run(() =>
        {
            var targetStore = new MarkdownFileStore(target);
            try
            {
                var other = targetStore.Load();
                other.AppendSubtree(subtree);
                targetStore.Save(other);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return e.Message;
            }
        });
        if (error != null)
        {
            ErrorMessage = $"Couldn't move the task to {Path.GetFileName(target)}: {error}";
            return;
        }
        if (Outline.IndexOf(id) is not int i) return;
        var nextFocus = Neighbour(id, -1) ?? Neighbour(id, 1);
        if (FocusedID == id) FocusedID = null;
        Mutate(o =>
        {
            o.RemoveSubtree(i);
            if (o.IsEmpty) o.AppendItem();
        });
        // The row below may have been one of its subtasks, now gone.
        if (nextFocus is Guid next && Outline.IndexOf(next) != null) RequestFocus(next);
    }

    public void SetCollapsed(bool collapsed, Guid id)
    {
        if (Outline.IndexOf(id) is not int i || !Outline.CanCollapse(i)) return;
        Mutate(o => o.SetCollapsed(collapsed, i));
    }

    public void ToggleCollapsed(Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        SetCollapsed(!Outline.Items[i].IsCollapsed, id);
    }

    /// <summary>
    /// Delete at the start of a row. A heading becomes a task again. A
    /// task joins the row above: its text is appended there, with the caret
    /// where they meet, and its subtasks go with it. On the first row, an
    /// empty task is removed if there are others.
    /// </summary>
    public void DeleteAtStart(Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        if (Outline.Items[i].IsHeading)
        {
            MakeTask(id);
            return;
        }
        if (Neighbour(id, -1) is not Guid previousID || Outline.IndexOf(previousID) is not int previous)
        {
            if (Outline.Items[i].Title.Length != 0 || Outline.HasChildren(i) || Outline.Count <= 1) return;
            var next = Neighbour(id, 1);
            Mutate(o => o.RemoveSubtree(i));
            if (next is Guid n) RequestFocus(n);
            return;
        }
        var caret = 0;
        Mutate(o => caret = o.Join(i, previous));
        RequestFocus(previousID, caret);
    }

    public void Decruft()
    {
        if (preferences.DeleteDecrufted)
            Mutate(o =>
            {
                o.DeleteDone();
                if (o.IsEmpty) o.AppendItem();
            });
        else
            Mutate(o => o.DecruftDone());
        if (FocusedID is Guid focused && !VisibleItems.Any(i => i.Id == focused)) FocusedID = null;
    }

    // MARK: - Selecting rows

    /// <summary>A run of selected rows, from where selecting started to the end that moves.
    /// Both null while editing text.</summary>
    public Guid? SelectionAnchor { get; private set; }
    public Guid? SelectionHead { get; private set; }
    /// <summary>A one-shot request for the outline's key handler to take keyboard
    /// focus, so keys act on the selected rows rather than one row's text.</summary>
    public bool SelectionFocusRequested { get; set; }

    public bool HasSelection => SelectionAnchor != null;

    /// <summary>The selected rows, top to bottom.</summary>
    public List<Guid> SelectedIDs
    {
        get
        {
            if (SelectionAnchor is not Guid anchor || SelectionHead is not Guid head) return [];
            var rows = VisibleItems;
            var a = rows.FindIndex(r => r.Id == anchor);
            var b = rows.FindIndex(r => r.Id == head);
            if (a < 0 || b < 0) return [];
            return rows.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1).Select(r => r.Id).ToList();
        }
    }

    /// <summary>Everything the selection covers, including what's hidden under the selected rows.</summary>
    private IntRange? SelectedRange
    {
        get
        {
            var ids = SelectedIDs;
            if (ids.Count == 0 || Outline.IndexOf(ids[0]) is not int first || Outline.IndexOf(ids[^1]) is not int last) return null;
            return Outline.BlockRange(first, last);
        }
    }

    /// <summary>Selects one row, ending any text editing.</summary>
    public void Select(Guid id)
    {
        SelectionAnchor = id;
        SelectionHead = id;
        FocusedID = id;
        FocusRequestID = null;
        SelectionFocusRequested = true;
        RaiseChanged();
    }

    /// <summary>Extends the selection to <paramref name="id"/>, starting it from the row being edited.</summary>
    public void ExtendSelection(Guid id)
    {
        SelectionAnchor ??= FocusedID ?? id;
        SelectionHead = id;
        FocusedID = id;
        FocusRequestID = null;
        SelectionFocusRequested = true;
        RaiseChanged();
    }

    /// <summary>Shift-Up/Down: grows or shrinks the selection a row at a time.</summary>
    public void ExtendSelection(int offset)
    {
        if ((SelectionHead ?? FocusedID) is not Guid from) return;
        if (Neighbour(from, offset) is Guid target) ExtendSelection(target);
        else if (!HasSelection) Select(from);
    }

    /// <summary>Up/Down with rows selected: selects the single row above or below.</summary>
    public void MoveSelection(int offset)
    {
        var ids = SelectedIDs;
        if (ids.Count == 0) return;
        var edge = offset < 0 ? ids[0] : ids[^1];
        Select(Neighbour(edge, offset) ?? edge);
    }

    public void SelectAll()
    {
        var rows = VisibleItems;
        if (rows.Count == 0) return;
        Select(rows[0].Id);
        ExtendSelection(rows[^1].Id);
    }

    public void ClearSelection()
    {
        if (SelectionAnchor == null && SelectionHead == null) return;
        SelectionAnchor = null;
        SelectionHead = null;
        RaiseChanged();
    }

    /// <summary>Return or Escape with rows selected: goes back to editing text, in
    /// the row the selection ended on.</summary>
    public void EditSelection()
    {
        if (SelectionHead is Guid id) RequestFocus(id);
    }

    /// <summary>The selected rows for the clipboard, as plain text and as Listful's Markdown.</summary>
    public (string PlainText, string Markdown)? CopySelection()
    {
        if (SelectedRange is not IntRange range) return null;
        var block = Outline.BlockItems(range);
        return (TaskClipboard.PlainText(block), TaskClipboard.Markdown(block));
    }

    /// <summary>Deletes the selected rows, with everything under them, and goes on
    /// editing the row above.</summary>
    public void DeleteSelection()
    {
        var ids = SelectedIDs;
        if (SelectedRange is not IntRange range || ids.Count == 0) return;
        var target = Neighbour(ids[0], -1) ?? Neighbour(ids[^1], 1);
        SelectionAnchor = null;
        SelectionHead = null;
        Mutate(o =>
        {
            o.RemoveBlock(range);
            if (o.IsEmpty) o.AppendItem();
        });
        if (target is Guid t && Outline.IndexOf(t) != null) RequestFocus(t);
        else FocusFirstRow();
    }

    /// <summary>Pastes in place of the selected rows. <paramref name="tasksMarkdown"/> is Listful's own
    /// clipboard form, if it was there.</summary>
    public void PasteOverSelection(string text, string? tasksMarkdown)
    {
        if (SelectedRange is not IntRange range) return;
        var block = PastedBlock(text, tasksMarkdown);
        if (block.Count == 0) return;
        SelectionAnchor = null;
        SelectionHead = null;
        var pasted = new IntRange(0, 0);
        Mutate(o => pasted = o.ReplaceBlock(range, block));
        FocusLastVisible(pasted);
    }

    /// <summary>Pastes several lines of text (or copied rows) as tasks after the row
    /// <paramref name="id"/>, or in place of it if it's empty.</summary>
    public void PasteRows(string text, string? tasksMarkdown, Guid id)
    {
        if (Outline.IndexOf(id) is not int i) return;
        var block = PastedBlock(text, tasksMarkdown);
        if (block.Count == 0) return;
        var pasted = new IntRange(0, 0);
        Mutate(o => pasted = o.InsertBlock(block, i));
        FocusLastVisible(pasted);
    }

    /// <summary>Done if any selected task is open; otherwise all open again.</summary>
    public void ToggleSelectionDone()
    {
        var indices = SelectedIDs.Select(Outline.IndexOf).OfType<int>().ToList();
        var done = indices.Any(i => !Outline.Items[i].IsHeading && Outline.Items[i].State == TaskState.Open);
        Mutate(o => o.SetDone(done, indices));
    }

    /// <summary>Indents the selected rows that aren't under another selected row,
    /// stopping at the first that can't be indented.</summary>
    public void IndentSelection()
    {
        var roots = SelectionRoots;
        Mutate(o =>
        {
            foreach (var id in roots)
                if (o.IndexOf(id) is not int i || !o.Indent(i)) break;
        });
    }

    /// <summary>Outdents the selected rows that aren't under another selected row.
    /// Last first, so they keep their order.</summary>
    public void OutdentSelection()
    {
        var roots = SelectionRoots;
        Mutate(o =>
        {
            foreach (var id in Enumerable.Reverse(roots))
                if (o.IndexOf(id) is int i) o.Outdent(i);
        });
    }

    private List<Guid> SelectionRoots
    {
        get
        {
            var ids = SelectedIDs;
            var selected = ids.ToHashSet();
            return ids.Where(id => Outline.IndexOf(id) is not int i || Outline.ParentIndex(i) is not int parent
                                   || !selected.Contains(Outline.Items[parent].Id)).ToList();
        }
    }

    /// <summary>Rows from pasted text. If it's what Listful copied, its own Markdown
    /// is used instead, which keeps done and collapsed states.</summary>
    private static List<TaskItem> PastedBlock(string text, string? tasksMarkdown) =>
        TaskClipboard.Items(tasksMarkdown ?? text);

    private void FocusLastVisible(IntRange range)
    {
        var ids = range.Indices().Select(i => Outline.Items[i].Id).ToHashSet();
        var last = VisibleItems.LastOrDefault(i => ids.Contains(i.Id));
        if (ids.Contains(last.Id)) RequestFocus(last.Id);
    }

    // MARK: - Focus

    /// <summary>Whether a focus request should leave the caret where the field has it
    /// (for moves, which shouldn't jump the caret to the end).</summary>
    public bool FocusRequestKeepsCaret { get; private set; }

    public void RequestFocus(Guid id, int? caret = null, bool keepCaret = false)
    {
        SelectionAnchor = null;
        SelectionHead = null;
        FocusedID = id;
        FocusRequestID = id;
        FocusRequestCaret = caret;
        FocusRequestKeepsCaret = keepCaret;
        RaiseChanged();
    }

    /// <summary>Called by a row's text field once it actually has keyboard focus.</summary>
    public void DidFocus(Guid id)
    {
        var hadSelection = HasSelection;
        SelectionAnchor = null;
        SelectionHead = null;
        FocusedID = id;
        if (FocusRequestID == id) FocusRequestID = null;
        if (hadSelection) RaiseChanged();
    }

    public void FocusFirstRow()
    {
        var rows = VisibleItems;
        if (rows.Count > 0) RequestFocus(rows[0].Id);
    }

    public void FocusPrevious(Guid id)
    {
        if (Neighbour(id, -1) is Guid target) RequestFocus(target);
    }

    public void FocusNext(Guid id)
    {
        if (Neighbour(id, 1) is Guid target) RequestFocus(target);
    }

    private Guid? Neighbour(Guid id, int offset)
    {
        var rows = VisibleItems;
        var i = rows.FindIndex(r => r.Id == id);
        if (i < 0 || i + offset < 0 || i + offset >= rows.Count) return null;
        return rows[i + offset].Id;
    }

    // MARK: - Loading and saving

    /// <summary>Discards in-memory state and re-reads the file (e.g. after editing it elsewhere).</summary>
    public void Reload()
    {
        pendingSave?.Cancel();
        pendingSave = null;
        Load();
    }

    /// <summary>
    /// Reads the file, straight away if it's on this computer. One that's only in
    /// the cloud is read off the UI thread, since the read waits for the
    /// download; meanwhile the list shows as downloading.
    /// </summary>
    private void Load()
    {
        loadTask?.Cancel();
        loadTask = null;
        if (!store.NeedsDownload)
        {
            FinishLoading(ReadResult(store));
            return;
        }
        LoadState = new LoadStateValue.Downloading();
        RaiseChanged();
        var cancel = new CancellationTokenSource();
        loadTask = cancel;
        _ = LoadInBackground(cancel.Token);
    }

    private async Task LoadInBackground(CancellationToken token)
    {
        var result = await Task.Run(() => ReadResult(store));
        if (token.IsCancellationRequested) return;
        loadTask = null;
        FinishLoading(result);
    }

    private static (string? Text, Exception? Error) ReadResult(MarkdownFileStore store)
    {
        try { return (store.ReadText(), null); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, e); }
    }

    private void FinishLoading((string? Text, Exception? Error) result)
    {
        if (result.Error is Exception error)
        {
            Outline = new Outline();
            LoadState = new LoadStateValue.Failed($"Couldn't read “{Path.GetFileName(FilePath)}”: {error.Message}");
        }
        else
        {
            syncedText = result.Text;
            Outline = result.Text is string text ? MarkdownCodec.Decode(text) : new Outline();
            LoadState = new LoadStateValue.Loaded();
        }
        // Always offer a row to type into; it isn't written until edited.
        if (Outline.IsEmpty) Outline.AppendItem();
        ShowsListTitle = Outline.Title != null;
        FocusedID = null;
        FocusRequestID = null;
        RaiseChanged();
    }

    /// <summary>
    /// Picks up changes made to the file elsewhere. With no unsaved edits
    /// the list just shows the new contents; otherwise saving now merges
    /// the two versions. The file is read off the UI thread, since a sync client
    /// can stall a read; if this computer saved meanwhile, the read is stale and dropped.
    /// </summary>
    public void CheckDisk()
    {
        if (!IsLoaded)
        {
            if (loadTask == null) Load();
            return;
        }
        _ = CheckDiskAsync();
    }

    private async Task CheckDiskAsync()
    {
        var before = syncedText;
        var text = await Task.Run(() => ReadResult(store).Text);
        if (text == null || syncedText != before || text == syncedText) return;
        if (pendingSave != null) SaveNow(); else Adopt(text);
    }

    /// <summary>
    /// Shows <paramref name="text"/>, the file's current contents, in place of the outline.
    /// Rows keep their identities where they match, so the row being edited
    /// keeps its text field and focus.
    /// </summary>
    private void Adopt(string text)
    {
        syncedText = text;
        var updated = MarkdownCodec.Decode(text);
        if (updated.IsEmpty) updated.AppendItem();
        updated.AdoptIdentities(Outline);
        if (updated == Outline) return;
        Outline = updated;
        if (updated.Title != null) ShowsListTitle = true;
        if (FocusedID is Guid f && Outline.IndexOf(f) == null) FocusedID = null;
        if (FocusRequestID is Guid r && Outline.IndexOf(r) == null) FocusRequestID = null;
        RaiseChanged();
    }

    private void Mutate(Action<Outline> change, bool notify = true)
    {
        change(Outline);
        ScheduleSave();
        if (notify) RaiseChanged();
    }

    /// <summary>Whether there are edits not yet written.</summary>
    public bool HasUnsavedChanges => pendingSave != null;

    private void ScheduleSave()
    {
        if (!IsLoaded) return;
        pendingSave?.Cancel();
        var cancel = new CancellationTokenSource();
        pendingSave = cancel;
        _ = SaveLater(cancel);
    }

    private async Task SaveLater(CancellationTokenSource cancel)
    {
        try { await Task.Delay(SaveDelay, cancel.Token); }
        catch (TaskCanceledException) { return; }
        if (cancel.IsCancellationRequested || pendingSave != cancel) return;
        SaveNow();
    }

    /// <summary>Drops unsaved changes, e.g. when the file has vanished and saving
    /// would recreate it at a path the user moved it away from.</summary>
    public void DiscardChanges()
    {
        pendingSave?.Cancel();
        pendingSave = null;
    }

    /// <summary>
    /// Writes immediately if there are unsaved changes. If the file was
    /// changed elsewhere since it was read, the two versions are merged; if
    /// they clash, the other version wins and this one is kept as a copy.
    /// </summary>
    public void SaveNow()
    {
        if (pendingSave is not CancellationTokenSource pending) return;
        pending.Cancel();
        pendingSave = null;
        var mine = MarkdownCodec.Encode(Outline);
        try
        {
            switch (FileSync.Save(mine, store, syncedText))
            {
                case SaveOutcome.Saved(var text):
                    syncedText = text;
                    break;
                case SaveOutcome.Merged(var text):
                    Adopt(text);
                    break;
                case SaveOutcome.Conflict(var disk, var copy):
                    Adopt(disk);
                    ErrorMessage = $"“{FileName}” was changed somewhere else while you were editing it here, "
                        + "and the changes clash. It now shows the other version; yours is saved beside it "
                        + $"as “{TaskLibrary.DisplayName(copy)}”.";
                    break;
                case SaveOutcome.Gone(var copy):
                    if (copy != null)
                        ErrorMessage = $"“{FileName}” was renamed or deleted somewhere else while you were editing it here. "
                            + $"Your latest changes are saved as “{TaskLibrary.DisplayName(copy)}”.";
                    break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"Couldn't save {Path.GetFileName(FilePath)}: {e.Message}";
        }
    }
}
