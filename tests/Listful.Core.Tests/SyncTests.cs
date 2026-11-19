using static Listful.Tests.TestSupport;

namespace Listful.Tests;

public sealed class FileSyncTests : IDisposable
{
    private readonly TempFolder folder = new();
    private MarkdownFileStore Store => new(folder.File("List.md"));

    public void Dispose() => folder.Dispose();

    [Fact]
    public void NewFileIsWritten()
    {
        Assert.Equal(new SaveOutcome.Saved("- [ ] a\n"), FileSync.Save("- [ ] a\n", Store, null));
        Assert.Equal("- [ ] a\n", Store.ReadText());
    }

    [Fact]
    public void UnchangedFileIsOverwritten()
    {
        Store.WriteText("- [ ] a\n");
        Assert.Equal(new SaveOutcome.Saved("- [x] a\n"), FileSync.Save("- [x] a\n", Store, "- [ ] a\n"));
        Assert.Equal("- [x] a\n", Store.ReadText());
    }

    [Fact]
    public void ChangesOnBothSidesMerge()
    {
        Store.WriteText("- [ ] a\n- [x] b\n");  // ticked elsewhere
        Assert.Equal(new SaveOutcome.Merged("- [x] a\n- [x] b\n"), FileSync.Save("- [x] a\n- [ ] b\n", Store, "- [ ] a\n- [ ] b\n"));
        Assert.Equal("- [x] a\n- [x] b\n", Store.ReadText());
    }

    [Fact]
    public void ClashKeepsTheirsAndCopiesMine()
    {
        Store.WriteText("- [ ] theirs\n");
        var outcome = FileSync.Save("- [ ] mine\n", Store, "- [ ] base\n", new DateTime(1970, 1, 1));
        var conflict = Assert.IsType<SaveOutcome.Conflict>(outcome);
        Assert.Equal("- [ ] theirs\n", conflict.Disk);
        Assert.Equal("- [ ] theirs\n", Store.ReadText());
        Assert.Equal("- [ ] mine\n", new MarkdownFileStore(conflict.Copy).ReadText());
    }

    [Fact]
    public void GoneFileIsNeverRecreated()
    {
        Assert.Equal(new SaveOutcome.Gone(null), FileSync.Save("- [ ] a\n", Store, "- [ ] a\n"));
        Assert.Null(Store.ReadText());

        var edited = Assert.IsType<SaveOutcome.Gone>(FileSync.Save("- [ ] b\n", Store, "- [ ] a\n"));
        Assert.NotNull(edited.Copy);
        Assert.Null(Store.ReadText());
        Assert.Equal("- [ ] b\n", new MarkdownFileStore(edited.Copy).ReadText());
    }

    [Fact]
    public void ReadsThroughABOM()
    {
        File.WriteAllText(Store.FilePath, "- [ ] a\n", new System.Text.UTF8Encoding(true));
        Assert.Equal("- [ ] a\n", Store.ReadText());
    }
}

public class TextMergeTests
{
    private const string Base = "- [ ] a\n- [ ] b\n- [ ] c\n- [ ] d\n";

    private static TextMerge.Result Merged(string text) => new TextMerge.Result.Merged(text);

    [Fact]
    public void OneSideChangedTakesThatSide()
    {
        var changed = "- [x] a\n- [ ] b\n- [ ] c\n- [ ] d\n";
        Assert.Equal(Merged(changed), TextMerge.Merge(Base, changed, Base));
        Assert.Equal(Merged(changed), TextMerge.Merge(Base, Base, changed));
    }

    [Fact]
    public void ChangesToDifferentLinesAreBothKept() =>
        Assert.Equal(Merged("- [x] a\n- [ ] b\n- [ ] c\n- [ ] d changed\n"),
            TextMerge.Merge(Base, "- [x] a\n- [ ] b\n- [ ] c\n- [ ] d\n", "- [ ] a\n- [ ] b\n- [ ] c\n- [ ] d changed\n"));

    [Fact]
    public void InsertionsAndDeletionsElsewhereAreBothKept() =>
        Assert.Equal(Merged("- [ ] a\n- [ ] new mine\n- [ ] b\n- [ ] d\n"),
            TextMerge.Merge(Base, "- [ ] a\n- [ ] new mine\n- [ ] b\n- [ ] c\n- [ ] d\n", "- [ ] a\n- [ ] b\n- [ ] d\n"));

    [Fact]
    public void InsertionsAtTheSamePlaceKeepBothTheirsFirst() =>
        Assert.Equal(Merged(Base + "- [ ] from there\n- [ ] from here\n"),
            TextMerge.Merge(Base, Base + "- [ ] from here\n", Base + "- [ ] from there\n"));

    [Fact]
    public void IdenticalChangesOnBothSidesMergeOnce()
    {
        var both = "- [ ] a\n- [x] b\n- [ ] c\n- [ ] d\n";
        var mine = both + "- [ ] e\n";
        Assert.Equal(Merged(mine), TextMerge.Merge(Base, mine, both));
    }

    [Fact]
    public void DifferentChangesToTheSameLineConflict() =>
        Assert.IsType<TextMerge.Result.Conflict>(TextMerge.Merge(Base,
            "- [ ] a\n- [ ] b here\n- [ ] c\n- [ ] d\n", "- [ ] a\n- [ ] b there\n- [ ] c\n- [ ] d\n"));

    [Fact]
    public void EditingALineTheOtherSideDeletedConflicts() =>
        Assert.IsType<TextMerge.Result.Conflict>(TextMerge.Merge(Base,
            "- [ ] a\n- [ ] b edited\n- [ ] c\n- [ ] d\n", "- [ ] a\n- [ ] c\n- [ ] d\n"));
}

public class AdoptIdentitiesTests
{
    [Fact]
    public void UnchangedAndEditedRowsKeepTheirIDs()
    {
        var old = MakeOutline("a:0", "b:1", "c:0");
        var updated = MakeOutline("a:0", "b edited:1", "c:0");
        updated.AdoptIdentities(old);
        Assert.Equal(old.Items.Select(i => i.Id), updated.Items.Select(i => i.Id));
    }

    [Fact]
    public void InsertedRowsGetNewIDsAndOthersKeepTheirs()
    {
        var old = MakeOutline("a:0", "c:0");
        var updated = MakeOutline("a:0", "b:0", "c:0");
        updated.AdoptIdentities(old);
        Assert.Equal(old.Items[0].Id, updated.Items[0].Id);
        Assert.Equal(old.Items[1].Id, updated.Items[2].Id);
        Assert.DoesNotContain(updated.Items[1].Id, old.Items.Select(i => i.Id));
    }

    [Fact]
    public void RemovedRowsDontShiftIdentities()
    {
        var old = MakeOutline("a:0", "b:0", "c:0");
        var updated = MakeOutline("a:0", "c:0");
        updated.AdoptIdentities(old);
        Assert.Equal([old.Items[0].Id, old.Items[2].Id], updated.Items.Select(i => i.Id));
    }
}

public class ConflictCopyTests
{
    [Fact]
    public void NamesCopyBesideTheListAndAvoidsClashes()
    {
        using var folder = new TempFolder();
        var list = folder.File("Groceries.md");
        var shown = new DateTime(2026, 10, 19, 14, 5, 0);

        var first = TaskLibrary.ConflictCopyPath(list, shown);
        Assert.Equal("Groceries (conflict 2026-10-19 14-05).md", Path.GetFileName(first));
        File.WriteAllText(first, "");
        Assert.Equal("Groceries (conflict 2026-10-19 14-05) 2.md", Path.GetFileName(TaskLibrary.ConflictCopyPath(list, shown)));
    }
}
