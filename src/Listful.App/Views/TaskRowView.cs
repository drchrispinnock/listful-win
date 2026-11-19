using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI.Core;

namespace Listful.App.Views;

/// <summary>
/// One row of the outline (upstream's <c>TaskRow</c> and <c>TaskTextField</c>): a disclosure
/// chevron, the tick box or bullet (click to toggle done, drag to move), and the title's text
/// field, whose outliner keys (Return, Tab, Shift+Tab, arrows, Backspace at the start, Escape)
/// go to the document. Rows are reused across redraws, keyed by the item's id, so the field
/// being edited keeps its focus.
/// </summary>
internal sealed partial class TaskRowView : Grid
{
    public const double IndentWidth = 22;
    /// <summary>From a row's indented edge to its bullet: the disclosure column and spacing.</summary>
    public const double BulletOffset = 22;

    private readonly OutlineView owner;
    public TaskItem Item { get; private set; }
    public readonly TextBox Field = new() { Style = (Style)Application.Current.Resources["RowTextBox"] };
    /// <summary>Shows the title struck through (TextBox can't), or a heading's placeholder, while the field isn't being edited.</summary>
    private readonly TextBlock overlay = new() { TextWrapping = TextWrapping.Wrap, IsHitTestVisible = false, Padding = new Thickness(0, 2, 0, 2) };
    private readonly Border chevron = new() { Width = 16, Height = 16, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly FontIcon chevronIcon = new() { FontSize = 9 };
    private readonly Grid bullet = new() { Width = 18, Height = 18, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Border selectionBackground = new() { CornerRadius = new CornerRadius(5), Margin = new Thickness(-4, 0, -4, 0) };
    private bool isSelected;
    private bool hasFocus;
    /// <summary>Set by a Shift-click, so the field taking focus from that click doesn't end the selection.</summary>
    private bool shiftClicked;

    public TaskRowView(OutlineView owner, TaskItem item)
    {
        this.owner = owner;
        Item = item;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnSpacing = 4;

        SetColumnSpan(selectionBackground, 3);
        Children.Add(selectionBackground);

        chevron.Child = chevronIcon;
        chevron.VerticalAlignment = VerticalAlignment.Top;
        chevron.Margin = new Thickness(0, 5, 0, 0);
        chevron.Tapped += (_, e) =>
        {
            e.Handled = true;
            owner.Document.ToggleCollapsed(Item.Id);
        };
        Children.Add(chevron);

        bullet.VerticalAlignment = VerticalAlignment.Top;
        bullet.Margin = new Thickness(0, 3, 0, 0);
        SetColumn(bullet, 1);
        Children.Add(bullet);
        bullet.PointerPressed += BulletPressed;
        bullet.PointerMoved += BulletMoved;
        bullet.PointerReleased += BulletReleased;
        bullet.PointerCaptureLost += (_, _) =>
        {
            if (dragStart == null) return;
            dragStart = null;
            if (dragging) owner.Document.CancelDrag();
            dragging = false;
        };

        SetColumn(Field, 2);
        SetColumn(overlay, 2);
        Children.Add(Field);
        Children.Add(overlay);
        Field.TextChanged += (_, _) =>
        {
            if (updating) return;
            owner.Document.SetTitle(Field.Text, Item.Id);
            UpdateOverlay();
        };
        Field.GotFocus += (_, _) =>
        {
            hasFocus = true;
            UpdateOverlay();
            if (shiftClicked)
            {
                shiftClicked = false;
                return;
            }
            owner.RowFocused(this);
        };
        Field.LostFocus += (_, _) =>
        {
            hasFocus = false;
            UpdateOverlay();
        };
        Field.PreviewKeyDown += FieldKeyDown;
        Field.Paste += FieldPaste;
        Field.AddHandler(PointerPressedEvent, new PointerEventHandler(FieldPressed), handledEventsToo: true);

        Update(item, isSelected: false);
    }

    private bool updating;

    /// <summary>Shows <paramref name="item"/>'s current state.</summary>
    public void Update(TaskItem item, bool isSelected)
    {
        Item = item;
        this.isSelected = isSelected;
        var document = owner.Document;
        var preferences = owner.Preferences;
        Margin = new Thickness(item.Depth * IndentWidth, TopPadding(item), 0, 2);

        updating = true;
        if (Field.Text != item.Title) Field.Text = item.Title;
        updating = false;

        var size = item.HeadingLevel switch { 1 => 24.0, 2 => 20.0, 3 => 17.0, _ => OutlineView.BodySize };
        var weight = item.HeadingLevel switch { 1 or 2 => FontWeights.Bold, 3 => FontWeights.SemiBold, _ => FontWeights.Normal };
        Field.FontSize = overlay.FontSize = size;
        Field.FontWeight = overlay.FontWeight = weight;

        var canCollapse = document.CanCollapse(item.Id);
        chevron.Visibility = canCollapse ? Visibility.Visible : Visibility.Collapsed;
        chevronIcon.Glyph = item.IsCollapsed ? "" : "";  // ChevronRight, ChevronDown
        chevronIcon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        chevron.Margin = new Thickness(0, item.IsHeading ? size * 0.55 - 3 : 5, 0, 0);
        ToolTipService.SetToolTip(chevron, item.IsCollapsed ? "Expand" : "Collapse");

        bullet.Visibility = item.IsHeading ? Visibility.Collapsed : Visibility.Visible;
        DrawBullet(item, canCollapse, preferences.ShowTick);
        ToolTipService.SetToolTip(bullet, item.State == TaskState.Open ? "Mark done; drag to move" : "Mark open; drag to move");

        var moving = document.Drag?.MovingIDs.Contains(item.Id) == true;
        Opacity = moving ? 0.35 : item.State == TaskState.Decrufted ? 0.45 : 1;
        selectionBackground.Background = isSelected
            ? (Brush)Application.Current.Resources["AccentFillColorSelectedTextBackgroundBrush"]
            : null;
        selectionBackground.Opacity = 0.3;
        UpdateOverlay();
    }

    private static double TopPadding(TaskItem item) => item.HeadingLevel switch { 1 => 16, 2 => 12, 3 => 8, _ => 0 };

    /// <summary>
    /// A tick box, or a plain bullet when ticks are turned off in Settings. A collapsed parent's
    /// circle has a dot in it, so hidden subtasks show (upstream's circle.circle).
    /// </summary>
    private void DrawBullet(TaskItem item, bool canCollapse, bool showTick)
    {
        bullet.Children.Clear();
        var collapsedParent = item.IsCollapsed && canCollapse;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        if (!showTick)
        {
            bullet.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = secondary });
            if (collapsedParent) bullet.Children.Add(new Ellipse { Width = 13, Height = 13, Stroke = secondary, StrokeThickness = 1.2 });
            return;
        }
        if (item.State == TaskState.Open)
        {
            bullet.Children.Add(new Ellipse { Width = 15, Height = 15, Stroke = secondary, StrokeThickness = 1.3 });
            if (collapsedParent) bullet.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = secondary });
            return;
        }
        bullet.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = accent });
        bullet.Children.Add(new FontIcon
        {
            Glyph = "",  // CheckMark
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
        });
    }

    /// <summary>Strikethrough (and the dimmer text that goes with it) while the field isn't being edited;
    /// while it is, the plain text, so the caret and selection show.</summary>
    public void UpdateOverlay()
    {
        var struck = !Item.IsHeading && Item.State != TaskState.Open && owner.Preferences.ShowStrikethrough;
        var placeholder = Item.IsHeading && Field.Text.Length == 0;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        Field.Foreground = struck ? secondary : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        if (placeholder)
        {
            overlay.Text = "Heading";
            overlay.TextDecorations = Windows.UI.Text.TextDecorations.None;
            overlay.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            overlay.Visibility = Visibility.Visible;
            Field.Opacity = 1;
        }
        else if (struck && !hasFocus)
        {
            overlay.Text = Field.Text;
            overlay.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
            overlay.Foreground = secondary;
            overlay.Visibility = Visibility.Visible;
            Field.Opacity = 0;
        }
        else
        {
            overlay.Visibility = Visibility.Collapsed;
            Field.Opacity = 1;
        }
    }

    /// <summary>Takes keyboard focus, with the caret at <paramref name="caret"/> (UTF-16 offset; null is the end).</summary>
    public void TakeFocus(int? caret, bool keepCaret)
    {
        // A row just added isn't in the tree yet; focus it once it is.
        if (!Field.IsLoaded || XamlRoot == null)
        {
            void Once(object s, RoutedEventArgs e)
            {
                Field.Loaded -= Once;
                TakeFocus(caret, keepCaret);
            }
            Field.Loaded += Once;
            return;
        }
        var length = Field.Text.Length;
        if (!keepCaret || !ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), Field))
        {
            Field.SelectionStart = Math.Min(caret ?? length, length);
            Field.SelectionLength = 0;
        }
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), Field))
            Field.Focus(FocusState.Programmatic);
        Field.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
    }

    // MARK: - Keys

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void FieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var document = owner.Document;
        var id = Item.Id;
        var shift = IsDown(VirtualKey.Shift);
        var control = IsDown(VirtualKey.Control);
        var alt = IsDown(VirtualKey.Menu);
        if (control || alt) return;  // Shortcuts are the window's.
        switch (e.Key)
        {
            case VirtualKey.Enter when !shift:
                e.Handled = true;
                document.ReturnKey(id, new IntRange(Field.SelectionStart, Field.SelectionStart + Field.SelectionLength));
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                if (shift) document.Outdent(id); else document.Indent(id);
                break;
            case VirtualKey.Up when shift:
                e.Handled = true;
                document.ExtendSelection(-1);
                break;
            case VirtualKey.Down when shift:
                e.Handled = true;
                document.ExtendSelection(1);
                break;
            case VirtualKey.Up when IsOnFirstLine():
                e.Handled = true;
                document.FocusPrevious(id);
                break;
            case VirtualKey.Down when IsOnLastLine():
                e.Handled = true;
                document.FocusNext(id);
                break;
            case VirtualKey.Back when Field.SelectionStart == 0 && Field.SelectionLength == 0:
                e.Handled = true;
                document.DeleteAtStart(id);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                document.Select(id);
                break;
        }
    }

    /// <summary>Whether the caret is on the field's first visual line, so Up leaves the row.</summary>
    private bool IsOnFirstLine()
    {
        if (Field.SelectionLength > 0 || Field.Text.Length == 0) return true;
        var caret = Field.GetRectFromCharacterIndex(Math.Min(Field.SelectionStart, Field.Text.Length - 1), Field.SelectionStart >= Field.Text.Length);
        var first = Field.GetRectFromCharacterIndex(0, false);
        return caret.Top <= first.Top + 1;
    }

    private bool IsOnLastLine()
    {
        if (Field.SelectionLength > 0 || Field.Text.Length == 0) return true;
        var caret = Field.GetRectFromCharacterIndex(Math.Min(Field.SelectionStart, Field.Text.Length - 1), Field.SelectionStart >= Field.Text.Length);
        var last = Field.GetRectFromCharacterIndex(Field.Text.Length - 1, true);
        return caret.Top >= last.Top - 1;
    }

    /// <summary>Text with line breaks pasted in becomes tasks; one line pastes into the title as usual.</summary>
    private async void FieldPaste(object sender, TextControlPasteEventArgs e)
    {
        e.Handled = true;
        var (text, markdown) = await Services.TaskClipboardService.Read();
        if (text == null) return;
        if (text.Contains('\n') || text.Contains('\r') || markdown != null && TaskClipboard.Items(markdown).Count > 1)
        {
            owner.Document.PasteRows(text, markdown, Item.Id);
            return;
        }
        var start = Field.SelectionStart;
        var single = text.Replace("\r", "").Replace("\n", "");
        Field.Text = Field.Text[..start] + single + Field.Text[(start + Field.SelectionLength)..];
        Field.SelectionStart = start + single.Length;
    }

    /// <summary>Shift-click on a row: extend the selection of rows to it.</summary>
    private void FieldPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) || hasFocus) return;
        shiftClicked = true;
        owner.Document.ExtendSelection(Item.Id);
    }

    // MARK: - Dragging the bullet

    private Windows.Foundation.Point? dragStart;
    private bool dragging;

    private void BulletPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(bullet).Properties.IsLeftButtonPressed) return;
        e.Handled = true;
        dragStart = e.GetCurrentPoint(owner).Position;
        dragging = false;
        bullet.CapturePointer(e.Pointer);
    }

    private void BulletMoved(object sender, PointerRoutedEventArgs e)
    {
        if (dragStart is not Windows.Foundation.Point start) return;
        e.Handled = true;
        var point = e.GetCurrentPoint(owner).Position;
        var dx = point.X - start.X;
        var dy = point.Y - start.Y;
        // A small wobble is still a click.
        if (!dragging && Math.Abs(dx) <= 3 && Math.Abs(dy) <= 3) return;
        dragging = true;
        owner.UpdateDrag(Item.Id, e, dx);
    }

    private void BulletReleased(object sender, PointerRoutedEventArgs e)
    {
        if (dragStart == null) return;
        e.Handled = true;
        dragStart = null;
        bullet.ReleasePointerCapture(e.Pointer);
        if (dragging)
        {
            dragging = false;
            owner.Document.EndDrag();
        }
        else
        {
            owner.Document.ToggleDone(Item.Id);
        }
    }
}
