namespace Listful.Tests;

/// <summary>Windows only: upstream's TaskDocument and LibraryModel are in the app target, untested.
/// Here they are in Core, so their behaviour is checked headless.</summary>
public sealed class TaskDocumentTests : IDisposable
{
    private readonly TempFolder folder = new();
    private string ListPath => folder.File("List.md");

    public void Dispose() => folder.Dispose();

    private TaskDocument Open(string text)
    {
        File.WriteAllText(ListPath, text);
        return new TaskDocument(ListPath, Preferences.Temporary(), watch: false);
    }

    [Fact]
    public void EditsAreSavedAndReturnSplitsRows()
    {
        using var document = Open("# Shopping\n\n- [ ] milk\n");
        Assert.True(document.ShowsListTitle);
        var milk = document.Outline.Items[0].Id;
        document.ReturnKey(milk, new IntRange(4, 4));
        Assert.Equal(document.Outline.Items[1].Id, document.FocusRequestID);
        document.SetTitle("eggs", document.Outline.Items[1].Id);
        document.ToggleDone(milk);
        Assert.True(document.HasUnsavedChanges);
        document.SaveNow();
        Assert.Equal("# Shopping\n\n- [x] milk\n- [ ] eggs\n", File.ReadAllText(ListPath));
    }

    [Fact]
    public void TypingHashesMakesAHeading()
    {
        using var document = Open("- [ ] a\n");
        document.SetTitle("## Later", document.Outline.Items[0].Id);
        Assert.Equal(2, document.Outline.Items[0].HeadingLevel);
        Assert.Equal("Later", document.Outline.Items[0].Title);
    }

    [Fact]
    public void DeleteAtStartJoinsTheRowAbove()
    {
        using var document = Open("- [ ] ab\n- [ ] cd\n");
        document.DeleteAtStart(document.Outline.Items[1].Id);
        Assert.Equal(["ab" + "cd"], document.Outline.Items.Select(i => i.Title));
        Assert.Equal(2, document.FocusRequestCaret);
    }

    [Fact]
    public void SelectionCopiesAndDeletesBlocks()
    {
        using var document = Open("- [ ] a\n  - [ ] a1\n- [ ] b\n- [ ] c\n");
        document.SetCollapsed(true, document.Outline.Items[0].Id);
        var rows = document.VisibleItems;
        document.Select(rows[0].Id);
        document.ExtendSelection(1);
        Assert.Equal([rows[0].Id, rows[1].Id], document.SelectedIDs);
        var copied = document.CopySelection()!.Value;
        Assert.Equal("- [ ] a <!-- listful: collapsed -->\n  - [ ] a1\n- [ ] b\n", copied.Markdown);
        Assert.Equal("- [ ] a\n  - [ ] a1\n- [ ] b\n", copied.PlainText);
        document.DeleteSelection();
        Assert.Equal(["c"], document.Outline.Items.Select(i => i.Title));
        Assert.False(document.HasSelection);
    }

    [Fact]
    public void PastedListfulMarkdownKeepsStates()
    {
        using var document = Open("- [ ] \n");
        document.PasteRows("x\ny", "- [x] x\n- [ ] y\n", document.Outline.Items[0].Id);
        Assert.Equal([TaskState.Done, TaskState.Open], document.Outline.Items.Select(i => i.State));
    }

    [Fact]
    public void DecruftHidesOrDeletes()
    {
        var preferences = Preferences.Temporary();
        File.WriteAllText(ListPath, "- [x] a\n- [ ] b\n");
        using var document = new TaskDocument(ListPath, preferences, watch: false);
        document.Decruft();
        Assert.Equal(["b"], document.VisibleItems.Select(i => i.Title));
        Assert.Equal(2, document.Outline.Count);
        preferences.DeleteDecrufted = true;
        document.Decruft();
        Assert.Equal(["b"], document.Outline.Items.Select(i => i.Title));
    }

    [Fact]
    public void DragMovesARowToTheGap()
    {
        using var document = Open("- [ ] a\n- [ ] b\n- [ ] c\n");
        var ids = document.Outline.Items.Select(i => i.Id).ToList();
        var frames = new Dictionary<Guid, (double, double)> { [ids[0]] = (0, 20), [ids[1]] = (20, 40), [ids[2]] = (40, 60) };
        // Drag c up to between a and b, and a little to the right: a child of a.
        document.UpdateDrag(ids[2], 15, 25, 22, frames, null);
        Assert.Equal(1, document.Drag!.Destination);
        Assert.Equal(1, document.Drag.Depth);
        document.EndDrag();
        Assert.Equal(["a:0", "c:1", "b:0"], TestSupport.Shape(document.Outline));
    }

    [Fact]
    public async Task MoveToListAppendsTheTaskThere()
    {
        using var document = Open("- [ ] a\n  - [ ] a1\n- [ ] b\n");
        var other = folder.File("Other.md");
        File.WriteAllText(other, "- [ ] x\n");
        await document.MoveToList(document.Outline.Items[0].Id, other);
        Assert.Equal("- [ ] x\n- [ ] a\n  - [ ] a1\n", File.ReadAllText(other));
        Assert.Equal(["b"], document.Outline.Items.Select(i => i.Title));
    }

    [Fact]
    public void SavingMergesChangesMadeElsewhere()
    {
        using var document = Open("- [ ] a\n- [ ] b\n");
        File.WriteAllText(ListPath, "- [ ] a\n- [x] b\n");
        document.ToggleDone(document.Outline.Items[0].Id);
        document.SaveNow();
        Assert.Equal("- [x] a\n- [x] b\n", File.ReadAllText(ListPath));
        Assert.Equal(TaskState.Done, document.Outline.Items[1].State);
    }
}

public sealed class LibraryModelTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly Preferences preferences = Preferences.Temporary();

    public LibraryModelTests() => preferences.Set(Preferences.FolderKey, folder.Path);

    public void Dispose() => folder.Dispose();

    private LibraryModel Make() => new(preferences, watch: false);

    [Fact]
    public void CreatesRenamesAndDeletesLists()
    {
        using var model = Make();
        Assert.Null(model.Selection);
        var (prompt, suggested) = model.PromptNewList();
        Assert.Equal("Untitled", suggested);
        model.CommitNamePrompt(prompt, "Groceries");
        Assert.Equal(folder.File("Groceries.md"), model.Selection);
        Assert.NotNull(model.Document);

        var (rename, current) = model.PromptRename(model.Selection!, isFolder: false);
        Assert.Equal("Groceries", current);
        model.CommitNamePrompt(rename, "Shopping");
        Assert.Equal(folder.File("Shopping.md"), model.Selection);
        Assert.Equal(["Shopping"], model.Lists.Select(TaskLibrary.DisplayName));

        model.Trash(model.Selection!, isFolder: false);
        Assert.Empty(model.Lists);
        Assert.Null(model.Selection);
    }

    [Fact]
    public void RestoresTheSelectionRelativeToTheFolder()
    {
        new TaskLibrary(folder.Path).CreateList("A");
        var b = new TaskLibrary(folder.Path).CreateList("B");
        using (var model = Make()) model.Selection = b;
        Assert.Equal("B.md", preferences.GetString(Preferences.SelectedListKey));
        using var reopened = Make();
        Assert.Equal(b, reopened.Selection);
    }

    [Fact]
    public void LinksFilesAndFoldersFromElsewhere()
    {
        using var elsewhere = new TempFolder();
        var file = elsewhere.File("TODO.md");
        File.WriteAllText(file, "# Things\n- [ ] x\n");
        using var model = Make();
        Assert.True(model.Link([file]));
        Assert.Equal([file], model.LinkedFiles);
        Assert.Equal(file, model.Selection);

        Directory.CreateDirectory(elsewhere.File("Work"));
        File.WriteAllText(Path.Combine(elsewhere.File("Work"), "Plan.md"), "");
        model.LinkFolder(elsewhere.File("Work"));
        Assert.Single(model.LinkedFolders);
        Assert.Equal(["Plan"], model.LinkedFolders[0].Lists.Select(TaskLibrary.DisplayName));

        model.Unlink(file);
        Assert.Empty(model.LinkedFiles);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task RepeatingChecklistsMakeTheirCopies()
    {
        using var model = Make();
        model.Now = () => new DateTime(2026, 11, 3, 9, 0, 0);
        model.Culture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        model.CreateRepeating("Routine", new RepeatSchedule());
        Assert.Equal(["Routine"], model.Templates.Select(TaskLibrary.DisplayName));
        model.Document!.SetTitle("Stretch", model.Document.Outline.Items[0].Id);
        await model.MakeDueCopies();
        Assert.Equal(["Routine 2026-11-03"], model.Lists.Select(TaskLibrary.DisplayName));
        Assert.Empty(model.UngroupedLists);
        Assert.Equal("Tue 3 Nov", model.CopyLabel(model.Lists[0], model.Templates[0]));
    }
}
