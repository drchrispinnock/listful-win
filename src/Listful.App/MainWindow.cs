using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Listful.App.Views;
using Windows.System;
using Windows.UI.Core;

namespace Listful.App;

/// <summary>
/// The single window (upstream's <c>ContentView</c> in <c>Window("Listful")</c>, with its
/// commands): a menu bar, the sidebar of task lists beside the open list.
/// <para>
/// Windows differences: menus are a MenuBar in the window, and Cmd shortcuts are Ctrl ones
/// (Cmd+Option is Ctrl+Alt). The shortcuts are handled here, before the focused row's text field
/// sees them, so they work while typing; the menu items only show them.
/// </para>
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly LibraryModel library;
    private readonly Preferences preferences;
    private readonly SidebarView sidebar;
    private readonly ContentControl detail = new()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };
    private readonly Grid root = new();
    private readonly ColumnDefinition sidebarColumn;
    private OutlineView? outline;
    private TaskDocument? watchedDocument;
    private ToggleMenuFlyoutItem? showDecruftedItem;
    private ToggleMenuFlyoutItem? showSidebarItem;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime today = DateTime.Today;
    private CancellationTokenSource? copiesWait;

    public MainWindow(Preferences preferences)
    {
        this.preferences = preferences;
        Title = "Listful";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Listful.ico"));
        SystemBackdrop = new MicaBackdrop();
        RestoreBounds();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 520;
            presenter.PreferredMinimumHeight = 300;
        }

        library = new LibraryModel(preferences);
        sidebar = new SidebarView(library, this);

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildMenuBar());

        var body = new Grid();
        sidebarColumn = new ColumnDefinition { Width = new GridLength(preferences.GetDouble(Preferences.SidebarWidthKey) ?? 240) };
        body.ColumnDefinitions.Add(sidebarColumn);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(sidebar);
        var splitter = Splitter();
        Grid.SetColumn(splitter, 1);
        body.Children.Add(splitter);
        var detailBackground = new Border
        {
            Child = detail,
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(8, 0, 0, 0),
        };
        Grid.SetColumn(detailBackground, 2);
        body.Children.Add(detailBackground);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;
        root.PreviewKeyDown += Shortcut;

        library.Changed += LibraryChanged;
        library.DocumentChanged += DocumentChanged;
        preferences.Changed += key =>
        {
            if (key == Preferences.ShowDecruftedKey && showDecruftedItem != null) showDecruftedItem.IsChecked = preferences.ShowDecrufted;
        };

        root.Loaded += async (_, _) =>
        {
            Dialogs.Root = root.XamlRoot;
            sidebar.Render();
            DocumentChanged();
            ShowError();
            if (!library.HasChosenFolder) await ChooseFolder(firstRun: true);
        };

        // Linked files live in folders that aren't watched, so re-check them whenever the user
        // comes back to the app; the open list is checked too, in case its watcher missed a change.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                library.Document?.SaveNow();
                return;
            }
            copiesWait?.Cancel();
            library.Activated();
        };
        // A new day can start a repeating checklist's next period while the app is in front.
        // Copies are made then only if someone is using this computer: with Listful running on
        // several computers sharing the folder, the idle ones would each make the same copy before
        // the first one's had synced. (Upstream also waits after a wake from sleep; here the
        // window's reactivation covers that.)
        clock.Tick += (_, _) =>
        {
            if (DateTime.Today == today) return;
            today = DateTime.Today;
            MakeDueCopiesWhenInUse();
        };
        clock.Start();
        Closed += (_, _) =>
        {
            SaveBounds();
            library.Dispose();
        };
    }

    // MARK: - Window size

    private void RestoreBounds()
    {
        var saved = preferences.GetString(Preferences.WindowBoundsKey)?.Split(',').Select(s => int.TryParse(s, out var n) ? n : (int?)null).ToArray();
        if (saved is { Length: 4 } && saved.All(n => n != null) && saved[2] >= 400 && saved[3] >= 300)
        {
            var rect = new Windows.Graphics.RectInt32(saved[0]!.Value, saved[1]!.Value, saved[2]!.Value, saved[3]!.Value);
            var area = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest).WorkArea;
            if (rect.X < area.X + area.Width - 40 && rect.Y < area.Y + area.Height - 40 && rect.X + rect.Width > area.X + 40 && rect.Y >= area.Y - 10)
            {
                AppWindow.MoveAndResize(rect);
                return;
            }
        }
        var scale = DisplayArea.Primary.WorkArea.Height > 1000 ? 1.25 : 1.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(900 * scale), (int)(720 * scale)));
    }

    private void SaveBounds()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            var (p, s) = (AppWindow.Position, AppWindow.Size);
            preferences.Set(Preferences.WindowBoundsKey, $"{p.X},{p.Y},{s.Width},{s.Height}");
        }
    }

    /// <summary>A handle between the sidebar and the list that resizes the sidebar.</summary>
    private Border Splitter()
    {
        var splitter = new Border { Width = 6, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        double? start = null;
        var startWidth = 0.0;
        splitter.PointerEntered += (_, _) => SetCursor(splitter, InputSystemCursorShape.SizeWestEast);
        splitter.PointerPressed += (_, e) =>
        {
            start = e.GetCurrentPoint(root).Position.X;
            startWidth = sidebarColumn.ActualWidth;
            splitter.CapturePointer(e.Pointer);
            e.Handled = true;
        };
        splitter.PointerMoved += (_, e) =>
        {
            if (start is not double x) return;
            var width = Math.Clamp(startWidth + e.GetCurrentPoint(root).Position.X - x, 160, 420);
            sidebarColumn.Width = new GridLength(width);
        };
        splitter.PointerReleased += (_, e) =>
        {
            if (start == null) return;
            start = null;
            splitter.ReleasePointerCapture(e.Pointer);
            preferences.Set(Preferences.SidebarWidthKey, sidebarColumn.ActualWidth);
        };
        return splitter;
    }

    private static void SetCursor(UIElement element, InputSystemCursorShape shape) =>
        typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(element, InputSystemCursor.Create(shape));

    // MARK: - Model changes

    private void LibraryChanged()
    {
        sidebar.Render();
        UpdateTitle();
        ShowError();
        if (library.Document == null) ShowPlaceholder();
    }

    private bool showingError;

    /// <summary>Upstream's "Task lists" and "Task file error" alerts.</summary>
    private async void ShowError()
    {
        if (showingError || root.XamlRoot == null) return;
        if (library.ErrorMessage is string message)
        {
            showingError = true;
            library.ErrorMessage = null;
            await Dialogs.ShowError("Task lists", message);
            showingError = false;
            ShowError();
        }
        else if (library.Document?.ErrorMessage is string fileMessage)
        {
            showingError = true;
            library.Document.ErrorMessage = null;
            await Dialogs.ShowError("Task file error", fileMessage);
            showingError = false;
            ShowError();
        }
    }

    private void DocumentChanged()
    {
        if (watchedDocument != null) watchedDocument.Changed -= DocumentContentChanged;
        watchedDocument = library.Document;
        if (watchedDocument != null) watchedDocument.Changed += DocumentContentChanged;
        outline = null;
        ShowDetail();
        UpdateTitle();
        sidebar.Render();
    }

    private TaskDocument.LoadStateValue? shownState;

    private void DocumentContentChanged()
    {
        if (watchedDocument == null) return;
        if (watchedDocument.LoadState != shownState) ShowDetail();
        sidebar.ShowDropTarget(watchedDocument.Drag?.TargetList);
        UpdateTitle();
        ShowError();
    }

    /// <summary>The list's title in the sidebar and window follow the title field as it's typed.</summary>
    private string? shownTitle;

    private void UpdateTitle()
    {
        var name = library.Document?.DisplayTitle;
        Title = name == null ? "Listful" : $"{name} – Listful";
        if (name != shownTitle)
        {
            if (shownTitle != null && name != null) sidebar.Render();
            shownTitle = name;
        }
    }

    private void ShowDetail()
    {
        var document = library.Document;
        shownState = document?.LoadState;
        switch (document?.LoadState)
        {
            case TaskDocument.LoadStateValue.Downloading:
                outline = null;
                detail.Content = Placeholder("", "Downloading…", $"“{document.FileName}” will open once it's on this computer.");
                break;
            case TaskDocument.LoadStateValue.Failed(var message):
                outline = null;
                detail.Content = Placeholder("", "Can't Open Task List", message, ("Try Again", document.Reload));
                break;
            case TaskDocument.LoadStateValue.Loaded:
                if (outline?.Document == document) return;
                outline = new OutlineView(document, preferences, sidebar.ListAt);
                detail.Content = outline;
                outline.Loaded += (_, _) => outline?.FocusInitialRow();
                break;
            default:
                ShowPlaceholder();
                break;
        }
    }

    private void ShowPlaceholder()
    {
        outline = null;
        if (library.Selection is string missing && library.MissingFiles.Contains(missing))
        {
            detail.Content = Placeholder("", "File Not Found", $"{missing} has been moved or deleted.",
                ("Locate…", async () => await Relocate(missing)), ("Remove from Sidebar", () => library.Unlink(missing)));
            return;
        }
        detail.Content = Placeholder("", "No Task List",
            library.Lists.Count == 0 ? "Create a task list to get started." : "Select a task list in the sidebar.",
            ("New Task List", async () => await NewList()), ("Add Existing File…", async () => await AddExistingFiles()));
    }

    /// <summary>Upstream's ContentUnavailableView.</summary>
    private static StackPanel Placeholder(string glyph, string title, string description, params (string Label, Action Action)[] actions)
    {
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 420, Padding = new Thickness(24) };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 40, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Style = (Style)Application.Current.Resources["SecondaryText"] });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (label, action) in actions)
        {
            var button = new Button { Content = label };
            button.Click += (_, _) => action();
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        return panel;
    }

    public void FocusOutline()
    {
        if (outline == null) return;
        outline.FocusInitialRow();
    }

    // MARK: - Commands

    public async Task NewList(string? folder = null)
    {
        var (prompt, suggested) = library.PromptNewList(folder);
        if (await Dialogs.AskName(library.NamePromptTitle(prompt), suggested, "Create") is string name)
            library.CommitNamePrompt(prompt, name);
    }

    public async Task NewFolder()
    {
        var (prompt, suggested) = library.PromptNewFolder();
        if (await Dialogs.AskName(library.NamePromptTitle(prompt), suggested, "Create") is string name)
            library.CommitNamePrompt(prompt, name);
    }

    public async Task Rename(string path, bool isFolder)
    {
        var (prompt, current) = library.PromptRename(path, isFolder);
        if (await Dialogs.AskName(library.NamePromptTitle(prompt), current, "Rename") is string name)
            library.CommitNamePrompt(prompt, name);
    }

    /// <summary>Asks before deleting: a whole list is too much to lose to a stray
    /// Delete key press, even though the Recycle Bin can give it back.</summary>
    public async Task ConfirmTrash(string path, bool isFolder)
    {
        var (title, message) = library.TrashQuestion(path, isFolder);
        if (await Dialogs.Confirm(title, message, "Delete")) library.Trash(path, isFolder);
    }

    public async Task NewRepeating()
    {
        if (await Dialogs.NewRepeating(library.SuggestedRepeatingName()) is var (name, schedule))
            library.CreateRepeating(name, schedule);
    }

    public async Task AddExistingFiles()
    {
        var files = await Services.Pickers.ChooseFiles(this, multiple: true);
        if (files.Count > 0) library.Link(files);
    }

    public async Task AddExistingFolder()
    {
        if (await Services.Pickers.ChooseFolder(this) is string folder) library.LinkFolder(folder);
    }

    public async Task Relocate(string missing)
    {
        if ((await Services.Pickers.ChooseFiles(this, multiple: false)).FirstOrDefault() is string file)
            library.Relocate(missing, file);
    }

    private async Task ChooseFolder(bool firstRun)
    {
        var chosen = await Dialogs.ChooseListsFolder(this, library.FolderPath, firstRun);
        if (chosen == null)
        {
            // First run, folder picker cancelled: keep the default, but remember it was offered.
            if (firstRun) library.SwitchFolder(library.FolderPath);
            return;
        }
        try
        {
            Directory.CreateDirectory(chosen);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowError("Task lists", $"Couldn't use {chosen}: {e.Message}");
            return;
        }
        library.SwitchFolder(chosen);
    }

    private async Task ShowSettings() => await Dialogs.ShowSettings(library, this, () => ChooseFolder(firstRun: false));

    /// <summary>Runs a command on the selected rows if there are any, otherwise on the current task.</summary>
    private void OnSelectionOrFocused(Action<TaskDocument> selection, Action<TaskDocument, Guid> focused)
    {
        if (library.Document is not TaskDocument document) return;
        if (document.HasSelection) selection(document);
        else OnFocused(focused);
    }

    /// <summary>Runs a command on the open list's current task, if there is one.</summary>
    private void OnFocused(Action<TaskDocument, Guid> action)
    {
        if (library.Document is TaskDocument document && document.FocusedID is Guid id) action(document, id);
    }

    private void ToggleSidebar()
    {
        var hidden = sidebar.Visibility == Visibility.Collapsed;
        sidebar.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        sidebarColumn.Width = hidden ? new GridLength(preferences.GetDouble(Preferences.SidebarWidthKey) ?? 240) : new GridLength(0);
        if (showSidebarItem != null) showSidebarItem.IsChecked = hidden;
    }

    private void ToggleShowDecrufted() => preferences.ShowDecrufted = !preferences.ShowDecrufted;

    private async void MakeDueCopiesWhenInUse()
    {
        copiesWait?.Cancel();
        var cancel = new CancellationTokenSource();
        copiesWait = cancel;
        while (!cancel.IsCancellationRequested)
        {
            if (Services.Shell.SecondsSinceInput < 60)
            {
                await library.MakeDueCopies();
                return;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), cancel.Token); }
            catch (TaskCanceledException) { return; }
        }
    }

    // MARK: - Menu bar

    /// <summary>A command: its menu item and, if it has one, its shortcut.</summary>
    private sealed record Command(VirtualKeyModifiers Modifiers, VirtualKey Key, Action Run);

    private readonly List<Command> shortcuts = [];

    private MenuFlyoutItem MenuItem(string text, Action action, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None, VirtualKey key = VirtualKey.None, string? shortcutText = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        if (key != VirtualKey.None)
        {
            shortcuts.Add(new Command(modifiers, key, action));
            item.KeyboardAcceleratorTextOverride = shortcutText ?? ShortcutText(modifiers, key);
        }
        else if (shortcutText != null)
        {
            item.KeyboardAcceleratorTextOverride = shortcutText;
        }
        return item;
    }

    private static string ShortcutText(VirtualKeyModifiers modifiers, VirtualKey key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
        if (modifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(key switch
        {
            VirtualKey.Up => "Up",
            VirtualKey.Down => "Down",
            VirtualKey.Enter => "Enter",
            (VirtualKey)188 => ",",
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)key - (int)VirtualKey.Number0).ToString(),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }

    private MenuBar BuildMenuBar()
    {
        const VirtualKeyModifiers ctrl = VirtualKeyModifiers.Control;
        const VirtualKeyModifiers ctrlShift = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift;
        const VirtualKeyModifiers ctrlAlt = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu;
        var bar = new MenuBar();

        var file = new MenuBarItem { Title = "File" };
        file.Items.Add(MenuItem("New Task List…", async () => await NewList(), ctrl, VirtualKey.N));
        file.Items.Add(MenuItem("New Repeating Checklist…", async () => await NewRepeating(), ctrlShift, VirtualKey.N));
        file.Items.Add(MenuItem("New Folder…", async () => await NewFolder()));
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(MenuItem("Add Existing File…", async () => await AddExistingFiles(), ctrl, VirtualKey.O));
        file.Items.Add(MenuItem("Add Existing Folder…", async () => await AddExistingFolder(), ctrlShift, VirtualKey.O));
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(MenuItem("Reload from Disk", () => library.Document?.Reload(), ctrl, VirtualKey.R));
        file.Items.Add(MenuItem("Save", () => library.Document?.SaveNow(), ctrl, VirtualKey.S));
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(MenuItem("Settings…", async () => await ShowSettings(), ctrl, (VirtualKey)188));
        file.Items.Add(new MenuFlyoutSeparator());
        file.Items.Add(MenuItem("Exit", Close, shortcutText: "Alt+F4"));
        bar.Items.Add(file);

        var edit = new MenuBarItem { Title = "Edit" };
        edit.Items.Add(MenuItem("Cut", () => EditCommand(o => o.Cut(), t => t.CutSelectionToClipboard()), shortcutText: "Ctrl+X"));
        edit.Items.Add(MenuItem("Copy", () => EditCommand(o => o.Copy(), t => t.CopySelectionToClipboard()), shortcutText: "Ctrl+C"));
        edit.Items.Add(MenuItem("Paste", () => EditCommand(o => _ = o.Paste(), t => t.PasteFromClipboard()), shortcutText: "Ctrl+V"));
        edit.Items.Add(new MenuFlyoutSeparator());
        edit.Items.Add(MenuItem("Select All Tasks", () => library.Document?.SelectAll(), shortcutText: "Esc, Ctrl+A"));
        bar.Items.Add(edit);

        var task = new MenuBarItem { Title = "Task" };
        task.Items.Add(MenuItem("Toggle Done", () => OnSelectionOrFocused(d => d.ToggleSelectionDone(), (d, id) => d.ToggleDone(id)), ctrl, VirtualKey.Enter));
        task.Items.Add(new MenuFlyoutSeparator());
        task.Items.Add(MenuItem("Collapse", () => OnFocused((d, id) => d.SetCollapsed(true, id)), ctrl, VirtualKey.Up));
        task.Items.Add(MenuItem("Expand", () => OnFocused((d, id) => d.SetCollapsed(false, id)), ctrl, VirtualKey.Down));
        task.Items.Add(new MenuFlyoutSeparator());
        task.Items.Add(MenuItem("Indent", () => OnSelectionOrFocused(d => d.IndentSelection(), (d, id) => d.Indent(id)), shortcutText: "Tab"));
        task.Items.Add(MenuItem("Outdent", () => OnSelectionOrFocused(d => d.OutdentSelection(), (d, id) => d.Outdent(id)), shortcutText: "Shift+Tab"));
        task.Items.Add(MenuItem("Move Up", () => OnFocused((d, id) => d.MoveUp(id)), ctrlShift, VirtualKey.Up));
        task.Items.Add(MenuItem("Move Down", () => OnFocused((d, id) => d.MoveDown(id)), ctrlShift, VirtualKey.Down));
        task.Items.Add(MenuItem("Sort A–Z", () => library.Document?.Sort(library.Document.FocusedID), ctrlAlt, VirtualKey.S));
        task.Items.Add(new MenuFlyoutSeparator());
        task.Items.Add(MenuItem("Heading 1", () => OnFocused((d, id) => d.MakeHeading(1, id)), ctrlAlt, VirtualKey.Number1));
        task.Items.Add(MenuItem("Heading 2", () => OnFocused((d, id) => d.MakeHeading(2, id)), ctrlAlt, VirtualKey.Number2));
        task.Items.Add(MenuItem("Heading 3", () => OnFocused((d, id) => d.MakeHeading(3, id)), ctrlAlt, VirtualKey.Number3));
        task.Items.Add(MenuItem("Task", () => OnFocused((d, id) => d.MakeTask(id)), ctrlAlt, VirtualKey.Number0));
        bar.Items.Add(task);

        var view = new MenuBarItem { Title = "View" };
        view.Items.Add(MenuItem("Decruft Done Tasks", () => library.Document?.Decruft(), ctrlShift, VirtualKey.D));
        showDecruftedItem = new ToggleMenuFlyoutItem { Text = "Show Decrufted Tasks", IsChecked = preferences.ShowDecrufted, KeyboardAcceleratorTextOverride = "Ctrl+Shift+H" };
        showDecruftedItem.Click += (_, _) => preferences.ShowDecrufted = showDecruftedItem.IsChecked;
        shortcuts.Add(new Command(ctrlShift, VirtualKey.H, ToggleShowDecrufted));
        view.Items.Add(showDecruftedItem);
        view.Items.Add(new MenuFlyoutSeparator());
        showSidebarItem = new ToggleMenuFlyoutItem { Text = "Show Sidebar", IsChecked = true, KeyboardAcceleratorTextOverride = "Ctrl+Shift+L" };
        showSidebarItem.Click += (_, _) => ToggleSidebar();
        shortcuts.Add(new Command(ctrlShift, VirtualKey.L, ToggleSidebar));
        view.Items.Add(showSidebarItem);
        bar.Items.Add(view);

        var help = new MenuBarItem { Title = "Help" };
        help.Items.Add(MenuItem("Keyboard Shortcuts", async () => await ShowShortcuts()));
        help.Items.Add(MenuItem("About Listful", async () => await Dialogs.ShowAbout()));
        bar.Items.Add(help);
        return bar;
    }

    /// <summary>Edit menu commands: on the selected rows if there are any, else the focused text field.</summary>
    private void EditCommand(Action<OutlineView> onRows, Action<TextBox> onText)
    {
        if (library.Document?.HasSelection == true && outline != null) onRows(outline);
        else if (FocusManager.GetFocusedElement(root.XamlRoot) is TextBox box) onText(box);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Runs the command whose shortcut this is, ahead of the focused control.</summary>
    private void Shortcut(object sender, KeyRoutedEventArgs e)
    {
        var modifiers = VirtualKeyModifiers.None;
        if (IsDown(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
        if (IsDown(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
        if (IsDown(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
        if (modifiers == VirtualKeyModifiers.None) return;
        var key = e.Key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 ? VirtualKey.Number0 + (e.Key - VirtualKey.NumberPad0) : e.Key;
        if (shortcuts.FirstOrDefault(c => c.Modifiers == modifiers && c.Key == key) is Command command)
        {
            e.Handled = true;
            command.Run();
        }
    }

    private static Task ShowShortcuts()
    {
        var text = string.Join("\n", new[]
        {
            "Enter\tNew task (splits the task at the caret)",
            "Tab / Shift+Tab\tIndent / outdent",
            "Up / Down\tPrevious / next task",
            "Backspace at start\tJoin with the task above",
            "Esc\tSelect the task (then Shift+Up/Down, Ctrl+C/X/V, Delete)",
            "Ctrl+Enter\tToggle done",
            "Ctrl+Up / Ctrl+Down\tCollapse / expand",
            "Ctrl+Shift+Up / Down\tMove up / down",
            "Ctrl+Alt+1, 2, 3, 0\tHeading 1, 2, 3, task",
            "Ctrl+Alt+S\tSort A–Z",
            "Ctrl+Shift+D\tDecruft done tasks",
            "Ctrl+Shift+H\tShow decrufted tasks",
            "# , ## , ###\tAt the start of a task: make it a heading",
            "Drag a tick box\tMove the task (sideways to nest it, onto a list in the sidebar to move it there)",
        });
        return Dialogs.Show(new ContentDialog
        {
            Title = "Keyboard Shortcuts",
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            },
            CloseButtonText = "OK",
        });
    }
}
