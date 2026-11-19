using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Listful.App.Views;

/// <summary>
/// Upstream's alerts and sheets as ContentDialogs. WinUI shows one dialog at a time, so they
/// queue: a second waits for the first to close.
/// </summary>
internal static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The window's root, which dialogs are shown over. Set by the main window.</summary>
    public static XamlRoot? Root { get; set; }

    public static async Task<ContentDialogResult> Show(ContentDialog dialog)
    {
        if (Root == null) return ContentDialogResult.None;
        await Gate.WaitAsync();
        try
        {
            dialog.XamlRoot = Root;
            dialog.RequestedTheme = (Root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default;
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>An error alert with OK (upstream's "Task lists" / "Task file error" alerts).</summary>
    public static Task ShowError(string title, string message) => Show(new ContentDialog
    {
        Title = title,
        Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        CloseButtonText = "OK",
        DefaultButton = ContentDialogButton.Close,
    });

    /// <summary>Asks for a name; null if cancelled.</summary>
    public static async Task<string?> AskName(string title, string initial, string primary)
    {
        var box = new TextBox { Text = initial, PlaceholderText = "Name" };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        var result = await Show(dialog);
        return result == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>Asks before something destructive; Cancel is the default, so Enter doesn't confirm by accident.</summary>
    public static async Task<bool> Confirm(string title, string message, string action) =>
        await Show(new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }) == ContentDialogResult.Primary;

    // MARK: - Repeating checklists

    /// <summary>File ▸ New Repeating Checklist…: names the template and sets its schedule. Null if cancelled.</summary>
    public static async Task<(string Name, RepeatSchedule Schedule)?> NewRepeating(string suggestedName)
    {
        var schedule = new RepeatSchedule();
        var name = new TextBox { Header = "Name", Text = suggestedName };
        var form = new StackPanel { Spacing = 12, MinWidth = 380 };
        form.Children.Add(name);
        form.Children.Add(ScheduleForm(schedule));
        form.Children.Add(new TextBlock
        {
            Text = "You'll fill in the checklist next. Listful makes a fresh copy of it, named with the date, each time it's due.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["SecondaryText"],
        });
        name.Loaded += (_, _) =>
        {
            name.Focus(FocusState.Programmatic);
            name.SelectAll();
        };
        var result = await Show(new ContentDialog
        {
            Title = "New Repeating Checklist",
            Content = form,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        return result == ContentDialogResult.Primary ? (name.Text, schedule) : null;
    }

    /// <summary>Changes an open template's schedule. Null if cancelled.</summary>
    public static async Task<RepeatSchedule?> EditSchedule(RepeatSchedule current)
    {
        var schedule = current.Copy();
        var result = await Show(new ContentDialog
        {
            Title = "Repeating Checklist",
            Content = ScheduleForm(schedule),
            PrimaryButtonText = "Done",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        return result == ContentDialogResult.Primary ? schedule : null;
    }

    private static string FrequencyTitle(RepeatSchedule.Frequency frequency) => frequency switch
    {
        RepeatSchedule.Frequency.Daily => "Every day",
        RepeatSchedule.Frequency.Weekdays => "Every weekday",
        RepeatSchedule.Frequency.Weekly => "Weekly",
        RepeatSchedule.Frequency.Monthly => "Monthly",
        RepeatSchedule.Frequency.Quarterly => "Quarterly",
        _ => "Yearly",
    };

    private static string Ordinal(int n) =>
        n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>The controls for a repeating checklist's schedule (upstream's <c>ScheduleForm</c>):
    /// how often, on which days, and whether unfinished tasks carry over. Edits <paramref name="schedule"/>.</summary>
    private static StackPanel ScheduleForm(RepeatSchedule schedule)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var panel = new StackPanel { Spacing = 12 };
        var frequencies = Enum.GetValues<RepeatSchedule.Frequency>();
        var frequency = new ComboBox { Header = "Repeats", MinWidth = 200 };
        foreach (var f in frequencies) frequency.Items.Add(FrequencyTitle(f));
        frequency.SelectedIndex = Array.IndexOf(frequencies, schedule.FrequencyValue);
        panel.Children.Add(frequency);

        var options = new StackPanel { Spacing = 12 };
        panel.Children.Add(options);

        ComboBox DayPicker(string header, int value, Action<int> set)
        {
            var box = new ComboBox { Header = header, MinWidth = 200 };
            for (var d = 1; d <= 31; d++) box.Items.Add(Ordinal(d));
            box.Items.Add("Last day");
            box.SelectedIndex = value == 0 ? 31 : Math.Clamp(value, 1, 31) - 1;
            box.SelectionChanged += (_, _) => set(box.SelectedIndex == 31 ? 0 : box.SelectedIndex + 1);
            return box;
        }

        void ShowOptions()
        {
            options.Children.Clear();
            switch (schedule.FrequencyValue)
            {
                case RepeatSchedule.Frequency.Weekly:
                    var days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                    var first = (int)culture.DateTimeFormat.FirstDayOfWeek + 1;
                    for (var k = 0; k < 7; k++)
                    {
                        var weekday = (first - 1 + k) % 7 + 1;
                        var toggle = new Microsoft.UI.Xaml.Controls.Primitives.ToggleButton
                        {
                            Content = culture.DateTimeFormat.ShortestDayNames[weekday - 1],
                            IsChecked = schedule.WeekdaysValue.Contains(weekday),
                            MinWidth = 40,
                        };
                        ToolTipService.SetToolTip(toggle, culture.DateTimeFormat.DayNames[weekday - 1]);
                        toggle.Click += (_, _) =>
                        {
                            // At least one day stays chosen.
                            if (toggle.IsChecked == true) schedule.WeekdaysValue.Add(weekday);
                            else if (schedule.WeekdaysValue.Count > 1) schedule.WeekdaysValue.Remove(weekday);
                            else toggle.IsChecked = true;
                        };
                        days.Children.Add(toggle);
                    }
                    var on = new StackPanel { Spacing = 4 };
                    on.Children.Add(new TextBlock { Text = "On" });
                    on.Children.Add(days);
                    options.Children.Add(on);
                    break;
                case RepeatSchedule.Frequency.Monthly:
                    options.Children.Add(DayPicker("On", schedule.DayOfMonth, d => schedule.DayOfMonth = d));
                    break;
                case RepeatSchedule.Frequency.Yearly:
                    var month = new ComboBox { Header = "Month", MinWidth = 200 };
                    for (var m = 0; m < 12; m++) month.Items.Add(culture.DateTimeFormat.MonthNames[m]);
                    month.SelectedIndex = Math.Clamp(schedule.Month, 1, 12) - 1;
                    month.SelectionChanged += (_, _) => schedule.Month = month.SelectedIndex + 1;
                    options.Children.Add(month);
                    options.Children.Add(DayPicker("Day", schedule.Day, d => schedule.Day = d));
                    break;
            }
        }
        frequency.SelectionChanged += (_, _) =>
        {
            schedule.FrequencyValue = frequencies[frequency.SelectedIndex];
            ShowOptions();
        };
        ShowOptions();

        var carry = new CheckBox { Content = "Carry over unfinished tasks", IsChecked = schedule.CarriesOver };
        carry.Click += (_, _) => schedule.CarriesOver = carry.IsChecked == true;
        panel.Children.Add(carry);
        panel.Children.Add(new TextBlock
        {
            Text = "Each new copy starts with the previous copy's open tasks, which are moved out of it.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, -8, 0, 0),
            Style = (Style)Application.Current.Resources["SecondaryText"],
        });
        return panel;
    }

    // MARK: - Lists folder

    /// <summary>
    /// Windows-only: where the lists live. Upstream defaults to iCloud Drive › Listful; Windows has
    /// no iCloud, so the user picks a folder, typically one OneDrive or Dropbox syncs (which the
    /// Mac and other PCs can be pointed at too). Returns the chosen folder, or null to keep the default.
    /// </summary>
    public static async Task<string?> ChooseListsFolder(Window owner, string current, bool firstRun)
    {
        var choices = new List<(string Label, string Path)>();
        if (Services.Shell.OneDriveFolder is string oneDrive) choices.Add(("OneDrive", Path.Combine(oneDrive, "Listful")));
        if (Services.Shell.DropboxFolder is string dropbox) choices.Add(("Dropbox", Path.Combine(dropbox, "Listful")));
        choices.Add(("Documents", LibraryModel.DefaultFolderPath));

        var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
        panel.Children.Add(new TextBlock
        {
            Text = "Listful keeps each task list as a Markdown file in a folder. To use your lists on other "
                + "computers, or with Listful on a Mac, put them in a folder that OneDrive or Dropbox syncs, "
                + "and choose the same folder there.",
            TextWrapping = TextWrapping.Wrap,
        });
        var radios = new RadioButtons();
        foreach (var (label, path) in choices)
            radios.Items.Add(new RadioButton { Content = $"{label}  ({Services.Shell.Abbreviated(path)})", Tag = path });
        var other = new RadioButton { Content = "Another folder…", Tag = "" };
        radios.Items.Add(other);
        var currentIndex = choices.FindIndex(c => PathUtility.SamePath(c.Path, current));
        // On first run, a synced folder comes first and is suggested; later, the one in use.
        radios.SelectedIndex = !firstRun && currentIndex >= 0 ? currentIndex : 0;
        panel.Children.Add(radios);

        var result = await Show(new ContentDialog
        {
            Title = firstRun ? "Where should Listful keep your task lists?" : "Task Lists Folder",
            Content = panel,
            PrimaryButtonText = firstRun ? "Continue" : "Use Folder",
            CloseButtonText = firstRun ? null : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (result != ContentDialogResult.Primary) return null;
        if (radios.SelectedItem is RadioButton { Tag: string chosen } && chosen.Length > 0) return chosen;
        if (ReferenceEquals(radios.SelectedItem, other)) return await Services.Pickers.ChooseFolder(owner);
        return choices[0].Path;
    }

    // MARK: - Settings

    /// <summary>Upstream's Settings window, as a dialog (Ctrl+,).</summary>
    public static async Task ShowSettings(LibraryModel library, Services.UpdaterService updater, Func<Task> chooseFolder)
    {
        var preferences = library.Preferences;
        var panel = new StackPanel { Spacing = 16, MinWidth = 420 };

        TextBlock Header(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };

        var completion = new StackPanel { Spacing = 4 };
        completion.Children.Add(Header("Show completion"));
        var tick = new CheckBox { Content = "Tick", IsChecked = preferences.ShowTick };
        var strike = new CheckBox { Content = "Strikethrough", IsChecked = preferences.ShowStrikethrough };
        // At least one stays on so done tasks always look different.
        void Sync()
        {
            tick.IsEnabled = !(preferences.ShowTick && !preferences.ShowStrikethrough);
            strike.IsEnabled = !(preferences.ShowStrikethrough && !preferences.ShowTick);
        }
        tick.Click += (_, _) => { preferences.ShowTick = tick.IsChecked == true; Sync(); };
        strike.Click += (_, _) => { preferences.ShowStrikethrough = strike.IsChecked == true; Sync(); };
        Sync();
        completion.Children.Add(tick);
        completion.Children.Add(strike);
        panel.Children.Add(completion);

        var decruft = new StackPanel { Spacing = 4 };
        decruft.Children.Add(Header("Decruft"));
        var delete = new CheckBox { Content = "Delete decrufted tasks", IsChecked = preferences.DeleteDecrufted };
        delete.Click += (_, _) => preferences.DeleteDecrufted = delete.IsChecked == true;
        decruft.Children.Add(delete);
        decruft.Children.Add(new TextBlock
        {
            Text = "Decruft Done Tasks deletes done tasks from the list instead of hiding them. Open tasks nested under them are kept.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 0, 0, 0),
            Style = (Style)Application.Current.Resources["SecondaryText"],
        });
        panel.Children.Add(decruft);

        var folder = new StackPanel { Spacing = 6 };
        folder.Children.Add(Header("Task lists"));
        var path = new TextBlock { Text = library.FolderPath, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        folder.Children.Add(path);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var show = new Button { Content = "Show in File Explorer" };
        show.Click += (_, _) => Services.Shell.ShowInExplorer(library.FolderPath);
        var choose = new Button { Content = "Choose…" };
        buttons.Children.Add(show);
        buttons.Children.Add(choose);
        folder.Children.Add(buttons);
        panel.Children.Add(folder);

        if (updater.IsAvailable)
        {
            var updates = new StackPanel { Spacing = 4 };
            updates.Children.Add(Header("Updates"));
            var automatic = new CheckBox { Content = "Automatically check for updates", IsChecked = updater.AutomaticallyChecks };
            automatic.Click += (_, _) => updater.AutomaticallyChecks = automatic.IsChecked == true;
            updates.Children.Add(automatic);
            panel.Children.Add(updates);
        }

        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = panel,
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        var wantsFolder = false;
        choose.Click += (_, _) =>
        {
            wantsFolder = true;
            dialog.Hide();
        };
        await Show(dialog);
        if (wantsFolder) await chooseFolder();
    }

    /// <summary>Help ▸ About Listful.</summary>
    public static Task ShowAbout()
    {
        var version = typeof(Dialogs).Assembly.GetName().Version;
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new Microsoft.UI.Xaml.Controls.Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Listful.png"))),
            Width = 96,
            Height = 96,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
        panel.Children.Add(new TextBlock { Text = $"Version {version?.ToString(2)}" });
        panel.Children.Add(new TextBlock
        {
            Text = "An outliner for task lists, in plain Markdown files you own. No subscription.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = "© 2026 Chris Pinnock", Style = (Style)Application.Current.Resources["SecondaryText"] });
        return Show(new ContentDialog { Title = "Listful", Content = panel, CloseButtonText = "OK", DefaultButton = ContentDialogButton.Close });
    }
}
