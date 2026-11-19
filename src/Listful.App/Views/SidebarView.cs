using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Listful.App.Views;

/// <summary>
/// The sidebar (upstream's <c>SidebarView</c>): the lists folder's lists and subfolders,
/// repeating checklists with their copies, linked folders and linked files. Built as plain rows
/// redrawn from the model, rather than a TreeView, so each row is laid out as upstream's.
/// Clicking a folder's row folds it; F2 renames and Delete deletes (or, for a linked file, removes
/// it from the sidebar). Files and folders dropped from File Explorer are linked.
/// </summary>
internal sealed partial class SidebarView : UserControl
{
    private readonly LibraryModel library;
    private readonly MainWindow window;
    private readonly StackPanel rows = new() { Padding = new Thickness(8, 8, 8, 8) };
    /// <summary>Each list row by path, so a dragged task can find the list under the pointer.</summary>
    private readonly Dictionary<string, Border> listRows = new(PathUtility.Comparer);
    /// <summary>Rows that open a list, top to bottom, for Up and Down.</summary>
    private readonly List<string> order = [];
    private string? dropHighlight;

    public SidebarView(LibraryModel library, MainWindow window)
    {
        this.library = library;
        this.window = window;
        IsTabStop = true;
        UseSystemFocusVisuals = false;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        // A bar at the foot of the sidebar: + makes a new list; its menu has the rest.
        var add = new SplitButton
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Flyout = AddMenu(),
            Padding = new Thickness(8, 4, 8, 4),
        };
        ToolTipService.SetToolTip(add, "New task list (the arrow has more options)");
        add.Click += async (_, _) => await window.NewList();
        var bar = new Grid { Padding = new Thickness(10, 6, 10, 8) };
        bar.Children.Add(add);
        Grid.SetRow(bar, 1);
        grid.Children.Add(bar);
        Content = grid;

        AllowDrop = true;
        DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Link;
            e.DragUIOverride.Caption = "Add to sidebar";
        };
        Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            library.Link(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)));
        };
        KeyDown += SidebarKeyDown;
    }

    private MenuFlyout AddMenu()
    {
        var menu = new MenuFlyout();
        AddItems(menu.Items);
        return menu;
    }

    private void AddItems(IList<MenuFlyoutItemBase> items)
    {
        items.Add(Item("New Task List…", async () => await window.NewList()));
        items.Add(Item("New Repeating Checklist…", async () => await window.NewRepeating()));
        items.Add(Item("New Folder…", async () => await window.NewFolder()));
        items.Add(new MenuFlyoutSeparator());
        items.Add(Item("Add Existing File…", async () => await window.AddExistingFiles()));
        items.Add(Item("Add Existing Folder…", async () => await window.AddExistingFolder()));
    }

    private static MenuFlyoutItem Item(string text, Action action, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    // MARK: - Drawing

    public void Render()
    {
        rows.Children.Clear();
        listRows.Clear();
        order.Clear();

        Section(PathUtility.LastPathComponent(library.FolderPath), first: true);
        foreach (var path in library.UngroupedLists) ListRow(path, library.DisplayName(path), "", 0);
        foreach (var group in library.Subfolders) FolderGroupRows(group);

        if (library.Templates.Count > 0)
        {
            Section("Repeating");
            foreach (var template in library.Templates) RepeatingRows(template);
        }
        if (library.LinkedFolders.Count > 0)
        {
            Section("Other Folders");
            foreach (var group in library.LinkedFolders) FolderGroupRows(group);
        }
        if (library.LinkedFiles.Count > 0)
        {
            Section("Other Files");
            foreach (var path in library.LinkedFiles)
            {
                var missing = library.MissingFiles.Contains(path);
                var row = ListRow(path, library.DisplayName(path), missing ? "" : "", 0,
                    subtitle: missing ? "Not found" : Services.Shell.Abbreviated(PathUtility.Parent(path)));
                if (missing) row.Opacity = 0.6;
            }
        }
        if (library.Lists.Count == 0 && library.Subfolders.Count == 0 && library.Templates.Count == 0)
            rows.Children.Add(new TextBlock
            {
                Text = "No task lists yet",
                Margin = new Thickness(10, 4, 0, 0),
                Style = (Style)Application.Current.Resources["SecondaryText"],
            });
    }

    private void Section(string title, bool first = false) => rows.Children.Add(new TextBlock
    {
        Text = title,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        Margin = new Thickness(10, first ? 4 : 16, 0, 4),
        TextTrimming = TextTrimming.CharacterEllipsis,
    });

    /// <summary>A subfolder or linked folder, with its lists beneath it.</summary>
    private void FolderGroupRows(LibraryModel.FolderGroup group)
    {
        var expanded = !library.CollapsedFolders.Contains(group.Path);
        var subtitle = group.IsLinked ? (group.IsMissing ? "Not found" : Services.Shell.Abbreviated(PathUtility.Parent(group.Path))) : null;
        var row = Row(group.Name, group.IsMissing ? "" : "", 0, subtitle, chevron: expanded);
        ToolTipService.SetToolTip(row, group.Path);
        row.Tapped += (_, _) => ToggleFolder(group.Path);
        row.ContextFlyout = FolderMenu(group);
        rows.Children.Add(row);
        if (!expanded) return;
        foreach (var path in group.Lists) ListRow(path, library.DisplayName(path), "", 1);
        if (group.Lists.Count == 0)
            rows.Children.Add(new TextBlock
            {
                Text = group.IsMissing ? "Not found" : "No task lists",
                Margin = new Thickness(54, 2, 0, 4),
                Style = (Style)Application.Current.Resources["SecondaryText"],
            });
    }

    private void ToggleFolder(string path)
    {
        if (!library.CollapsedFolders.Remove(path)) library.CollapsedFolders.Add(path);
        Render();
    }

    /// <summary>A template with its copies beneath it: the newest, earlier ones with tasks left,
    /// and the finished ones folded into "Earlier".</summary>
    private void RepeatingRows(string template)
    {
        var expanded = !library.CollapsedTemplates.Contains(template);
        var row = ListRow(template, library.DisplayName(template), "", 0, chevron: expanded, onChevron: () =>
        {
            if (!library.CollapsedTemplates.Remove(template)) library.CollapsedTemplates.Add(template);
            Render();
        });
        ToolTipService.SetToolTip(row, "Template: edit it to change future copies");
        if (!expanded) return;
        var (shown, earlier) = library.CopyGroups(template);
        foreach (var copy in shown) ListRow(copy, library.CopyLabel(copy, template), "", 1);
        if (earlier.Count == 0) return;
        var showEarlier = library.ExpandedEarlier.Contains(template);
        var earlierRow = Row($"Earlier ({earlier.Count})", null, 1, null, chevron: showEarlier, secondary: true);
        earlierRow.Tapped += (_, _) =>
        {
            if (!library.ExpandedEarlier.Remove(template)) library.ExpandedEarlier.Add(template);
            Render();
        };
        rows.Children.Add(earlierRow);
        if (!showEarlier) return;
        foreach (var copy in earlier) ListRow(copy, library.CopyLabel(copy, template), "", 2, secondary: true);
    }

    /// <summary>A row that opens a list when clicked.</summary>
    private Border ListRow(string path, string name, string glyph, int level, string? subtitle = null,
        bool? chevron = null, Action? onChevron = null, bool secondary = false)
    {
        var row = Row(name, glyph, level, subtitle, chevron, secondary, onChevron);
        var selected = PathUtility.SamePath(path, library.Selection);
        if (selected) row.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        if (PathUtility.SamePath(path, dropHighlight)) Highlight(row);
        row.Tapped += (_, _) =>
        {
            library.Selection = path;
            Focus(FocusState.Programmatic);
        };
        row.DoubleTapped += (_, _) => window.FocusOutline();
        row.ContextFlyout = ListMenu(path);
        if (subtitle == null || !library.IsLinked(path)) ToolTipService.SetToolTip(row, path);
        rows.Children.Add(row);
        listRows[path] = row;
        order.Add(path);
        return row;
    }

    private Border Row(string name, string? glyph, int level, string? subtitle, bool? chevron = null, bool secondary = false, Action? onChevron = null)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (chevron is bool open)
        {
            var icon = new FontIcon { Glyph = open ? "" : "", FontSize = 9, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            var hit = new Border { Child = icon, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            if (onChevron != null)
                hit.Tapped += (_, e) =>
                {
                    e.Handled = true;
                    onChevron();
                };
            grid.Children.Add(hit);
        }
        if (glyph != null)
        {
            var icon = new FontIcon { Glyph = glyph, FontSize = 15 };
            if (secondary) icon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources[secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"],
        });
        if (subtitle != null)
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        Grid.SetColumn(text, glyph == null ? 1 : 2);
        if (glyph == null) Grid.SetColumnSpan(text, 2);
        grid.Children.Add(text);
        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(6 + level * 16, 5, 8, 5),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        row.PointerEntered += (_, _) =>
        {
            if (row.Background is SolidColorBrush { Color.A: 0 }) row.Background = (Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"];
        };
        row.PointerExited += (_, _) =>
        {
            if (ReferenceEquals(row.Background, Application.Current.Resources["SubtleFillColorTertiaryBrush"])) row.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        };
        return row;
    }

    private static void Highlight(Border row) => row.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    // MARK: - Dragging a task onto a list

    /// <summary>The list whose row is under the pointer, if any.</summary>
    public string? ListAt(PointerRoutedEventArgs e)
    {
        foreach (var (path, row) in listRows)
        {
            if (library.MissingFiles.Contains(path)) continue;
            var point = e.GetCurrentPoint(row).Position;
            if (point.X >= 0 && point.Y >= 0 && point.X < row.ActualWidth && point.Y < row.ActualHeight) return path;
        }
        return null;
    }

    /// <summary>Outlines the list a task is being dragged onto.</summary>
    public void ShowDropTarget(string? path)
    {
        if (PathUtility.SamePath(path, dropHighlight) || path == dropHighlight) return;
        if (dropHighlight != null && listRows.TryGetValue(dropHighlight, out var old)) old.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dropHighlight = path;
        if (path != null && listRows.TryGetValue(path, out var row)) Highlight(row);
    }

    // MARK: - Menus

    private MenuFlyout ListMenu(string path)
    {
        var menu = new MenuFlyout();
        var missing = library.MissingFiles.Contains(path);
        if (library.IsLinked(path))
        {
            menu.Items.Add(Item("Show in File Explorer", () => Services.Shell.ShowInExplorer(path), !missing));
            if (missing) menu.Items.Add(Item("Locate…", async () => await window.Relocate(path)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Remove from Sidebar", () => library.Unlink(path)));
        }
        else
        {
            menu.Items.Add(Item("Rename…", async () => await window.Rename(path, isFolder: false)));
            menu.Items.Add(Item("Show in File Explorer", () => Services.Shell.ShowInExplorer(path)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Delete…", async () => await window.ConfirmTrash(path, isFolder: false)));
        }
        return menu;
    }

    private MenuFlyout FolderMenu(LibraryModel.FolderGroup group)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("New Task List in Folder…", async () => await window.NewList(group.Path), !group.IsMissing));
        if (!group.IsLinked) menu.Items.Add(Item("Rename…", async () => await window.Rename(group.Path, isFolder: true)));
        menu.Items.Add(Item("Show in File Explorer", () => Services.Shell.ShowInExplorer(group.Path), !group.IsMissing));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(group.IsLinked
            ? Item("Remove from Sidebar", () => library.UnlinkFolder(group.Path))
            : Item("Delete…", async () => await window.ConfirmTrash(group.Path, isFolder: true)));
        return menu;
    }

    // MARK: - Keys

    private async void SidebarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)) return;
        var index = library.Selection == null ? -1 : order.FindIndex(p => PathUtility.SamePath(p, library.Selection));
        switch (e.Key)
        {
            case VirtualKey.Up when index > 0:
                e.Handled = true;
                library.Selection = order[index - 1];
                break;
            case VirtualKey.Down when index + 1 < order.Count:
                e.Handled = true;
                library.Selection = order[index + 1];
                break;
            case VirtualKey.Enter or VirtualKey.Right:
                e.Handled = true;
                window.FocusOutline();
                break;
            case VirtualKey.F2 when library.Selection is string selected && !library.IsLinked(selected):
                e.Handled = true;
                await window.Rename(selected, isFolder: false);
                break;
            case VirtualKey.Delete when library.Selection is string selected:
                e.Handled = true;
                // Delete never touches a linked file; it only leaves the sidebar.
                if (library.IsLinked(selected)) library.Unlink(selected);
                else await window.ConfirmTrash(selected, isFolder: false);
                break;
        }
    }
}
