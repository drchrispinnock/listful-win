using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;

namespace Listful.App.Views;

/// <summary>
/// The open list (upstream's <c>OutlineView</c>): the schedule bar for a template, the list's
/// title, and its rows, top to bottom. It redraws when the document changes, reusing each row's
/// view so focus stays put. While rows are selected it holds keyboard focus itself, so keys act
/// on the rows (upstream's <c>SelectionKeyHandler</c>).
/// </summary>
internal sealed partial class OutlineView : UserControl
{
    public const double BodySize = 15;

    public TaskDocument Document { get; }
    public Preferences Preferences { get; }
    /// <summary>The sidebar list under a point in the window's coordinates, for dragging a task onto it.</summary>
    private readonly Func<PointerRoutedEventArgs, string?> sidebarTarget;

    private readonly StackPanel rows = new() { Spacing = 0 };
    private readonly Dictionary<Guid, TaskRowView> rowViews = [];
    private readonly ScrollViewer scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Canvas overlay = new() { IsHitTestVisible = false };
    private readonly StackPanel header = new() { Spacing = 0 };
    private readonly TextBox titleField = new()
    {
        Style = (Style)Application.Current.Resources["RowTextBox"],
        FontSize = 30,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 10),
    };
    private readonly Button newTask = new() { Content = "New Task", Margin = new Thickness(0, 8, 0, 0) };
    private bool updatingTitle;

    public OutlineView(TaskDocument document, Preferences preferences, Func<PointerRoutedEventArgs, string?> sidebarTarget)
    {
        Document = document;
        Preferences = preferences;
        this.sidebarTarget = sidebarTarget;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var content = new Grid { Padding = new Thickness(24, 16, 24, 40) };
        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(rows);
        stack.Children.Add(newTask);
        content.Children.Add(stack);
        content.Children.Add(overlay);
        scroller.Content = content;
        Content = scroller;

        titleField.TextChanged += (_, _) =>
        {
            if (!updatingTitle) Document.SetListTitle(titleField.Text);
        };
        titleField.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Down)
            {
                e.Handled = true;
                Document.FocusFirstRow();
            }
        };
        ToolTipService.SetToolTip(titleField, "List title (the # line at the top of the file)");
        newTask.Click += (_, _) => Document.AppendTask();
        // Clicking below the rows types into the last one, or a new one.
        content.Tapped += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, content)) return;
            var visible = Document.VisibleItems;
            if (visible.Count > 0 && visible[^1].Title.Length == 0 && !visible[^1].IsHeading) Document.RequestFocus(visible[^1].Id);
            else Document.AppendTask();
        };

        KeyDown += SelectionKeyDown;
        LostFocus += (_, _) =>
        {
            // Leaving for something other than a row ends the selection, as upstream's key handler does.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this) && Document.HasSelection
                    && !Document.SelectionFocusRequested)
                    Document.ClearSelection();
            });
        };

        Document.Changed += Render;
        Preferences.Changed += PreferencesChanged;
        Unloaded += (_, _) =>
        {
            Document.Changed -= Render;
            Preferences.Changed -= PreferencesChanged;
        };
        Loaded += (_, _) => Render();
    }

    private void PreferencesChanged(string key)
    {
        if (key is Preferences.ShowTickKey or Preferences.ShowStrikethroughKey or Preferences.ShowDecruftedKey) Render();
    }

    /// <summary>Shows the document as it now is.</summary>
    public void Render()
    {
        if (XamlRoot == null) return;
        RenderHeader();

        var visible = Document.VisibleItems;
        var selected = Document.SelectedIDs.ToHashSet();
        var wanted = new List<TaskRowView>(visible.Count);
        foreach (var item in visible)
        {
            if (!rowViews.TryGetValue(item.Id, out var view))
            {
                view = new TaskRowView(this, item);
                rowViews[item.Id] = view;
            }
            view.Update(item, selected.Contains(item.Id));
            wanted.Add(view);
        }
        var ids = visible.Select(i => i.Id).ToHashSet();
        foreach (var gone in rowViews.Keys.Where(k => !ids.Contains(k)).ToList()) rowViews.Remove(gone);

        // Rearrange only what changed: taking a field out of the tree drops its focus (to the
        // menu bar), so rows are inserted and removed one by one and the rest stay put.
        if (!rows.Children.Cast<UIElement>().SequenceEqual(wanted))
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as TextBox;
            var focusedRow = wanted.FirstOrDefault(r => ReferenceEquals(r.Field, focused));
            var caret = focused?.SelectionStart;
            var keep = wanted.ToHashSet();
            for (var i = rows.Children.Count - 1; i >= 0; i--)
                if (!keep.Contains((TaskRowView)rows.Children[i])) rows.Children.RemoveAt(i);
            var moved = false;
            for (var i = 0; i < wanted.Count; i++)
            {
                if (i < rows.Children.Count && ReferenceEquals(rows.Children[i], wanted[i])) continue;
                var at = rows.Children.IndexOf(wanted[i]);
                if (at >= 0)
                {
                    rows.Children.RemoveAt(at);
                    if (ReferenceEquals(wanted[i], focusedRow)) moved = true;
                }
                rows.Children.Insert(i, wanted[i]);
            }
            // Keep the caret in a row that moved, unless something else asks for focus.
            if (moved && focusedRow != null && Document.FocusRequestID == null && !Document.HasSelection)
                focusedRow.TakeFocus(caret, keepCaret: false);
        }
        newTask.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DrawDropIndicator();
        ApplyFocusRequests();
    }

    private readonly ContentControl scheduleHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private RepeatSchedule? shownSchedule;

    /// <summary>The schedule bar and title field stay in the tree and are shown or hidden, so the
    /// title keeps its focus while it is being typed.</summary>
    private void RenderHeader()
    {
        if (header.Children.Count == 0)
        {
            header.Children.Add(scheduleHost);
            header.Children.Add(titleField);
        }
        var schedule = Document.Outline.Schedule;
        if (!Equals(schedule, shownSchedule) || (schedule != null) != (scheduleHost.Content != null))
        {
            shownSchedule = schedule?.Copy();
            scheduleHost.Content = schedule == null ? null : ScheduleBar(schedule);
        }
        scheduleHost.Visibility = schedule == null ? Visibility.Collapsed : Visibility.Visible;
        titleField.Visibility = Document.ShowsListTitle ? Visibility.Visible : Visibility.Collapsed;
        updatingTitle = true;
        var title = Document.Outline.Title ?? "";
        if (titleField.Text != title) titleField.Text = title;
        updatingTitle = false;
    }

    /// <summary>Shown above a template's tasks: what it is, and how often it repeats.</summary>
    private Border ScheduleBar(RepeatSchedule schedule)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = "", FontSize = 16, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock { Text = schedule.Summary(System.Globalization.CultureInfo.CurrentCulture) });
        text.Children.Add(new TextBlock
        {
            Text = schedule.CarriesOver
                ? "Template. Each copy takes over the last one's unfinished tasks."
                : "Template. Each copy starts fresh.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var edit = new Button { Content = "Edit…", VerticalAlignment = VerticalAlignment.Center };
        edit.Click += async (_, _) =>
        {
            if (Document.Outline.Schedule is RepeatSchedule current && await Dialogs.EditSchedule(current) is RepeatSchedule updated)
                Document.CommitSchedule(updated);
        };
        Grid.SetColumn(edit, 2);
        grid.Children.Add(edit);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 12),
            CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
        };
    }

    /// <summary>Carries out a pending request for a row's field, or for the selection, to take focus.</summary>
    private void ApplyFocusRequests()
    {
        if (Document.SelectionFocusRequested)
        {
            Document.SelectionFocusRequested = false;
            Focus(FocusState.Programmatic);
            if (Document.SelectionHead is Guid head && rowViews.TryGetValue(head, out var headView))
                headView.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            return;
        }
        if (Document.FocusRequestID is not Guid id || !rowViews.TryGetValue(id, out var view)) return;
        var caret = Document.FocusRequestCaret;
        var keep = Document.FocusRequestKeepsCaret;
        // Straight away, so keys typed after Return land in the new row: lay a new row out first,
        // so it can take focus. A row that still isn't loaded takes it when it is.
        if (!view.Field.IsLoaded) UpdateLayout();
        view.TakeFocus(caret, keep);
        Document.DidFocus(id);
    }

    public void RowFocused(TaskRowView row) => Document.DidFocus(row.Item.Id);

    /// <summary>Focuses the row being worked on, or the first, when the list is opened.</summary>
    public void FocusInitialRow()
    {
        if (Document.FocusRequestID != null) return;
        if (Document.FocusedID is Guid id && Document.Outline.IndexOf(id) != null) Document.RequestFocus(id);
        else if (Document.VisibleItems.Count > 0) Document.RequestFocus(Document.VisibleItems[0].Id, 0);
    }

    // MARK: - Dragging

    /// <summary>Follows a drag of a row's bullet: over the sidebar's lists, or a gap among the rows.</summary>
    public void UpdateDrag(Guid id, PointerRoutedEventArgs e, double translationX)
    {
        var target = sidebarTarget(e);
        var y = e.GetCurrentPoint(rows).Position.Y;
        var frames = new Dictionary<Guid, (double Top, double Bottom)>();
        foreach (var (rowId, view) in rowViews)
        {
            if (view.Parent != rows) continue;
            var top = view.TransformToVisual(rows).TransformPoint(new Windows.Foundation.Point(0, 0)).Y - view.Margin.Top;
            frames[rowId] = (top, top + view.ActualHeight + view.Margin.Top + view.Margin.Bottom);
        }
        Document.UpdateDrag(id, y, translationX, TaskRowView.IndentWidth, frames, target);
        // Near the edges, scroll so the drag can reach rows out of view.
        var inView = e.GetCurrentPoint(scroller).Position.Y;
        if (inView < 24) scroller.ChangeView(null, scroller.VerticalOffset - 12, null, true);
        else if (inView > scroller.ActualHeight - 24) scroller.ChangeView(null, scroller.VerticalOffset + 12, null, true);
    }

    /// <summary>Where a dragged row would land: a line at the gap, indented to the
    /// depth it would take, starting where that depth's bullet would be.</summary>
    private void DrawDropIndicator()
    {
        overlay.Children.Clear();
        if (Document.Drag is not { TargetList: null } drag) return;
        var origin = rows.TransformToVisual(overlay).TransformPoint(new Windows.Foundation.Point(0, 0));
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var left = origin.X + drag.Depth * TaskRowView.IndentWidth + TaskRowView.BulletOffset;
        var top = origin.Y + drag.GapY;
        var circle = new Ellipse { Width = 8, Height = 8, Stroke = accent, StrokeThickness = 2 };
        Canvas.SetLeft(circle, left);
        Canvas.SetTop(circle, top - 4);
        var line = new Rectangle { Height = 2, Width = Math.Max(0, rows.ActualWidth - drag.Depth * TaskRowView.IndentWidth - TaskRowView.BulletOffset - 8), Fill = accent };
        Canvas.SetLeft(line, left + 8);
        Canvas.SetTop(line, top - 1);
        overlay.Children.Add(circle);
        overlay.Children.Add(line);
    }

    // MARK: - Selected rows

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Keys while rows are selected (upstream's <c>SelectionKeyHandler</c>).</summary>
    private async void SelectionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!Document.HasSelection) return;
        var shift = IsDown(VirtualKey.Shift);
        var control = IsDown(VirtualKey.Control);
        e.Handled = true;
        switch (e.Key)
        {
            case VirtualKey.Up when shift: Document.ExtendSelection(-1); break;
            case VirtualKey.Down when shift: Document.ExtendSelection(1); break;
            case VirtualKey.Up when !control: Document.MoveSelection(-1); break;
            case VirtualKey.Down when !control: Document.MoveSelection(1); break;
            case VirtualKey.Enter when !control:
            case VirtualKey.Escape:
                Document.EditSelection();
                break;
            case VirtualKey.Tab:
                if (shift) Document.OutdentSelection(); else Document.IndentSelection();
                break;
            case VirtualKey.Delete:
            case VirtualKey.Back:
                Document.DeleteSelection();
                break;
            case VirtualKey.C when control: Copy(); break;
            case VirtualKey.X when control: Cut(); break;
            case VirtualKey.V when control: await Paste(); break;
            case VirtualKey.A when control: Document.SelectAll(); break;
            default:
                e.Handled = false;
                break;
        }
    }

    public void Copy()
    {
        if (Document.CopySelection() is var (plain, markdown)) Services.TaskClipboardService.Write(plain, markdown);
    }

    public void Cut()
    {
        Copy();
        Document.DeleteSelection();
    }

    public async Task Paste()
    {
        var (text, markdown) = await Services.TaskClipboardService.Read();
        if (text != null) Document.PasteOverSelection(text, markdown);
    }
}
