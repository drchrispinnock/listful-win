using static Listful.Tests.TestSupport;

namespace Listful.Tests;

/// <summary>Drag-and-drop moves: <c>MoveDepths</c>, <c>DropTarget</c> and <c>Move</c>.</summary>
public class MoveTests
{
    [Fact]
    public void SubtreeMovesToAnotherListAtTopLevel()
    {
        var subtree = MakeOutline("a:0", "b:1", "c:2", "d:1", "e:0").SubtreeItems(1);
        Assert.Equal(["b", "c"], subtree.Select(i => i.Title));
        var target = MakeOutline("x:0", "y:1");
        target.AppendSubtree(subtree);
        Assert.Equal(["x:0", "y:1", "b:0", "c:1"], Shape(target));
    }

    [Fact]
    public void SubtreeGoesBeforeATrailingBlankRow()
    {
        var target = MakeOutline("x:0", ":0");
        target.AppendSubtree(MakeOutline("b:0").SubtreeItems(0));
        Assert.Equal(["x:0", "b:0", ":0"], Shape(target));
    }

    [Fact]
    public void MoveDownCarriesSubtree()
    {
        var o = MakeOutline("a:0", "b:1", "c:0", "d:0");
        Assert.Equal(1, o.Move(0, 3, 0));
        Assert.Equal(["c:0", "a:0", "b:1", "d:0"], Shape(o));
    }

    [Fact]
    public void MoveUpCarriesSubtree()
    {
        var o = MakeOutline("a:0", "b:0", "c:0", "d:1");
        Assert.Equal(0, o.Move(2, 0, 0));
        Assert.Equal(["c:0", "d:1", "a:0", "b:0"], Shape(o));
    }

    [Fact]
    public void MoveToEnd()
    {
        var o = MakeOutline("a:0", "b:1", "c:0");
        Assert.Equal(1, o.Move(0, 3, 1));
        Assert.Equal(["c:0", "a:1", "b:2"], Shape(o));
    }

    [Fact]
    public void MoveInPlaceChangesDepth()
    {
        var o = MakeOutline("a:0", "b:0", "c:1");
        // Before itself or just after its own subtree are the same gap.
        Assert.Equal(1, o.Move(1, 1, 1));
        Assert.Equal(["a:0", "b:1", "c:2"], Shape(o));
        Assert.Equal(1, o.Move(1, 3, 0));
        Assert.Equal(["a:0", "b:0", "c:1"], Shape(o));
    }

    [Fact]
    public void DepthsRunFromNextItemToOneBelowPrevious()
    {
        var o = MakeOutline("a:0", "b:1", "c:2", "d:1", "e:0", "x:0");
        Assert.Equal(new ClosedIntRange(1, 3), o.MoveDepths(5, 3));
        Assert.Equal(new ClosedIntRange(0, 0), o.MoveDepths(5, 0));
        Assert.Equal(new ClosedIntRange(0, 1), o.MoveDepths(0, 6));
    }

    [Fact]
    public void CannotMoveIntoOwnSubtreeOrMoveHeadings()
    {
        var o = MakeOutline("a:0", "b:1", "c:1", "#H", "d:0");
        Assert.Null(o.MoveDepths(0, 2));
        Assert.Null(o.MoveDepths(3, 0));
        Assert.Null(o.Move(0, 2, 1));
        Assert.Null(o.Move(4, 0, 1));
        Assert.Equal(["a:0", "b:1", "c:1", "#H", "d:0"], Shape(o));
    }

    [Fact]
    public void NothingNestsUnderAHeading()
    {
        var o = MakeOutline("a:0", "#H", "b:0", "c:0");
        Assert.Equal(new ClosedIntRange(0, 0), o.MoveDepths(3, 2));
        Assert.Equal(new ClosedIntRange(0, 1), o.MoveDepths(3, 1));
    }

    [Fact]
    public void MovingAcrossHeadingsChangesSection()
    {
        var o = MakeOutline("#A", "a:0", "#B", "b:0");
        o.SetCollapsed(true, 2);
        Assert.Equal(3, o.Move(1, 4, 0));
        Assert.Equal(["#A", "#B", "b:0", "a:0"], Shape(o));
        Assert.False(o.Items[1].IsCollapsed);
    }

    [Fact]
    public void DropUnderCollapsedTaskExpandsIt()
    {
        var o = MakeOutline("a:0", "b:1", "c:2", "d:0");
        o.SetCollapsed(true, 0);
        var target = o.DropTarget(3, 0, null);
        Assert.Equal(4, target?.Destination);
        // c (hidden, depth 2) would allow up to 3; the visible a caps it at 1.
        Assert.Equal(new ClosedIntRange(0, 1), target?.Depths);
        Assert.Equal(3, o.Move(3, 4, 1));
        Assert.Equal(["a:0", "b:1", "c:2", "d:1"], Shape(o));
        Assert.False(o.Items[0].IsCollapsed);
    }

    [Fact]
    public void DropTargetAtTop()
    {
        var target = MakeOutline("a:0", "b:0", "c:1").DropTarget(1, null, 0);
        Assert.Equal(0, target?.Destination);
        Assert.Equal(new ClosedIntRange(0, 0), target?.Depths);
    }
}
