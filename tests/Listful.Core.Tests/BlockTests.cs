using static Listful.Tests.TestSupport;

namespace Listful.Tests;

public class BlockTests
{
    [Fact]
    public void BlockRangeTakesInHiddenSubtasksAndSections()
    {
        var o = MakeOutline("a:0", "b:1", "c:0", "#H", "d:0", "#K");
        o.SetCollapsed(true, 0);
        Assert.Equal(new IntRange(0, 2), o.BlockRange(0, 0));
        Assert.Equal(new IntRange(2, 4), o.BlockRange(2, 3));
        o.SetCollapsed(true, 3);
        Assert.Equal(new IntRange(2, 5), o.BlockRange(2, 3));
    }

    [Fact]
    public void BlockItemsAreRebasedToTopLevel() =>
        Assert.Equal(["b:0", "c:1", "d:0"], Shape(new Outline(items: MakeOutline("a:0", "b:1", "c:2", "d:1").BlockItems(new(1, 4)))));

    [Fact]
    public void RemoveBlockMovesOrphansOut()
    {
        var o = MakeOutline("a:0", "b:1", "c:2", "d:0");
        o.RemoveBlock(new(0, 2));
        Assert.Equal(["c:0", "d:0"], Shape(o));
    }

    [Fact]
    public void InsertBlockGoesAfterACollapsedRowsSubtree()
    {
        var o = MakeOutline("x:0", "a:1", "a2:2", "b:0");
        o.SetCollapsed(true, 1);
        Assert.Equal(new IntRange(3, 5), o.InsertBlock(MakeOutline("p:0", "q:1").Items, 1));
        Assert.Equal(["x:0", "a:1", "a2:2", "p:1", "q:2", "b:0"], Shape(o));
    }

    [Fact]
    public void InsertBlockGoesFirstUnderAnExpandedParent()
    {
        var o = MakeOutline("a:0", "b:1");
        o.InsertBlock(MakeOutline("p:0").Items, 0);
        Assert.Equal(["a:0", "p:1", "b:1"], Shape(o));
    }

    [Fact]
    public void InsertBlockReplacesAnEmptyRow()
    {
        var o = MakeOutline("a:0", ":1");
        Assert.Equal(new IntRange(1, 3), o.InsertBlock(MakeOutline("p:0", "q:0").Items, 1));
        Assert.Equal(["a:0", "p:1", "q:1"], Shape(o));
    }

    [Fact]
    public void InsertBlockGivesNewIDs()
    {
        var o = MakeOutline("a:0");
        o.InsertBlock(o.BlockItems(new(0, 1)), 0);
        Assert.NotEqual(o.Items[0].Id, o.Items[1].Id);
    }

    [Fact]
    public void ReplaceBlockTakesTheFirstReplacedRowsDepth()
    {
        var o = MakeOutline("a:0", "b:1", "c:2", "d:0");
        Assert.Equal(new IntRange(1, 4), o.ReplaceBlock(new(1, 3), MakeOutline("p:0", "q:1", "r:0").Items));
        Assert.Equal(["a:0", "p:1", "q:2", "r:1", "d:0"], Shape(o));
    }

    [Fact]
    public void SetDoneMarksTasksButNotHeadings()
    {
        var o = MakeOutline("#H", "a:0", "b:0");
        o.SetDone(true, [0, 1, 2]);
        Assert.Equal([TaskState.Open, TaskState.Done, TaskState.Done], o.Items.Select(i => i.State));
        o.SetDone(false, [1]);
        Assert.Equal([TaskState.Open, TaskState.Open, TaskState.Done], o.Items.Select(i => i.State));
    }
}

public class TaskClipboardTests
{
    [Fact]
    public void OneRowCopiesAsItsText() => Assert.Equal("Buy milk", TaskClipboard.PlainText(MakeOutline("Buy milk:0").Items));

    [Fact]
    public void PlainTextLeavesOutListfulsMarkers()
    {
        var o = MakeOutline("a:0", "b:1");
        o.ToggleDone(1);
        o.DecruftDone();
        Assert.Equal("- [ ] a\n  - [x] b <!-- listful: decrufted -->\n", TaskClipboard.Markdown(o.Items));
        Assert.Equal("- [ ] a\n  - [x] b\n", TaskClipboard.PlainText(o.Items));
    }

    [Fact]
    public void PastedTextBecomesTasks()
    {
        var items = TaskClipboard.Items("# Shopping\n\nmilk\n  - [x] eggs\n\n- bread\n");
        Assert.Equal(["#Shopping", "milk:0", "eggs:1", "bread:0"], Shape(new Outline(items: items)));
        Assert.Equal(TaskState.Done, items[2].State);
    }
}
