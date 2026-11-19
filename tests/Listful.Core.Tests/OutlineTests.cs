using static Listful.Tests.TestSupport;

namespace Listful.Tests;

public class OutlineTests
{
    [Fact]
    public void InitClampsDepths() =>
        Assert.Equal(["a:0", "b:1", "c:1"], Shape(MakeOutline("a:2", "b:5", "c:1")));

    [Fact]
    public void SubtreeAndParent()
    {
        var o = MakeOutline("a:0", "b:1", "c:2", "d:1", "e:0");
        Assert.Equal(4, o.SubtreeEnd(0));
        Assert.Equal(3, o.SubtreeEnd(1));
        Assert.Equal(1, o.ParentIndex(2));
        Assert.Equal(0, o.ParentIndex(3));
        Assert.Null(o.ParentIndex(0));
        Assert.Equal(1, o.PreviousSiblingIndex(3));
        Assert.Null(o.PreviousSiblingIndex(1));
        Assert.True(o.HasChildren(1));
        Assert.False(o.HasChildren(3));
    }

    [Fact]
    public void IndentMovesSubtreeUnderPreviousSibling()
    {
        var o = MakeOutline("a:0", "b:0", "c:1", "d:0");
        Assert.True(o.Indent(1));
        Assert.Equal(["a:0", "b:1", "c:2", "d:0"], Shape(o));
    }

    [Fact]
    public void IndentWithoutPreviousSiblingFails()
    {
        var o = MakeOutline("a:0", "b:1");
        Assert.False(o.Indent(0));
        Assert.False(o.Indent(1));
        Assert.Equal(["a:0", "b:1"], Shape(o));
    }

    [Fact]
    public void IndentExpandsCollapsedParent()
    {
        var o = MakeOutline("a:0", "x:1", "b:0");
        o.SetCollapsed(true, 0);
        o.Indent(2);
        Assert.False(o.Items[0].IsCollapsed);
        Assert.Equal(["a:0", "x:1", "b:1"], Shape(o));
    }

    [Fact]
    public void OutdentPlacesAfterParentSubtree()
    {
        var o = MakeOutline("a:0", "b:1", "c:2", "d:1", "e:0");
        Assert.Equal(2, o.Outdent(1));
        Assert.Equal(["a:0", "d:1", "b:0", "c:1", "e:0"], Shape(o));
    }

    [Fact]
    public void ReturnOnEmptyNestedTaskOutdents()
    {
        var o = MakeOutline("a:0", "b:1", ":1");
        var r = o.ReturnKey(2);
        Assert.Equal(2, r.Index);
        Assert.Null(r.Caret);
        Assert.Equal(["a:0", "b:1", ":0"], Shape(o));
    }

    [Fact]
    public void ReturnOnEmptyTopLevelTaskInserts()
    {
        var o = MakeOutline("a:0", ":0");
        Assert.Equal(2, o.ReturnKey(1).Index);
        Assert.Equal(["a:0", ":0", ":0"], Shape(o));
    }

    [Fact]
    public void ReturnOnNonEmptyNestedTaskInserts()
    {
        var o = MakeOutline("a:0", "b:1");
        Assert.Equal(2, o.ReturnKey(1).Index);
        Assert.Equal(["a:0", "b:1", ":1"], Shape(o));
    }

    [Fact]
    public void ReturnMidLineSplitsTask()
    {
        var o = MakeOutline("hello world:0", "x:0");
        var r = o.ReturnKey(0, new IntRange(5, 5));
        Assert.Equal(1, r.Index);
        Assert.Equal(0, r.Caret);
        Assert.Equal(["hello:0", " world:0", "x:0"], Shape(o));
    }

    [Fact]
    public void ReturnMidLineOfParentSplitsIntoFirstChild()
    {
        var o = MakeOutline("abcd:0", "c:1");
        Assert.Equal(1, o.ReturnKey(0, new IntRange(2, 2)).Index);
        Assert.Equal(["ab:0", "cd:1", "c:1"], Shape(o));
    }

    [Fact]
    public void ReturnDeletesSelectionBeforeSplitting()
    {
        var o = MakeOutline("abcdef:0");
        Assert.Equal(1, o.ReturnKey(0, new IntRange(2, 4)).Index);
        Assert.Equal(["ab:0", "ef:0"], Shape(o));
    }

    [Fact]
    public void ReturnAtStartInsertsEmptyTaskAbove()
    {
        var o = MakeOutline("a:0", "b:1", "c:2");
        var r = o.ReturnKey(1, new IntRange(0, 0));
        Assert.Equal(2, r.Index);
        Assert.Equal(0, r.Caret);
        Assert.Equal(["a:0", ":1", "b:1", "c:2"], Shape(o));
    }

    [Fact]
    public void ReturnSplitsByUTF16Offset()
    {
        var o = MakeOutline("🙂ab:0");
        o.ReturnKey(0, new IntRange(3, 3));
        Assert.Equal(["🙂a:0", "b:0"], Shape(o));
    }

    [Fact]
    public void OutdentTopLevelFails() => Assert.Null(MakeOutline("a:0").Outdent(0));

    [Fact]
    public void InsertAfterLeafAddsSibling()
    {
        var o = MakeOutline("a:0", "b:1", "c:0");
        Assert.Equal(2, o.InsertItem(1));
        Assert.Equal(["a:0", "b:1", ":1", "c:0"], Shape(o));
    }

    [Fact]
    public void InsertAfterExpandedParentAddsFirstChild()
    {
        var o = MakeOutline("a:0", "b:1");
        Assert.Equal(1, o.InsertItem(0));
        Assert.Equal(["a:0", ":1", "b:1"], Shape(o));
    }

    [Fact]
    public void InsertAfterCollapsedParentAddsSiblingAfterSubtree()
    {
        var o = MakeOutline("a:0", "b:1", "c:0");
        o.SetCollapsed(true, 0);
        Assert.Equal(2, o.InsertItem(0));
        Assert.Equal(["a:0", "b:1", ":0", "c:0"], Shape(o));
    }

    [Fact]
    public void MoveUpAndDownSwapSiblingSubtrees()
    {
        var o = MakeOutline("a:0", "a1:1", "b:0", "b1:1", "c:0");
        Assert.Equal(0, o.MoveUp(2));
        Assert.Equal(["b:0", "b1:1", "a:0", "a1:1", "c:0"], Shape(o));
        Assert.Equal(3, o.MoveDown(2));
        Assert.Equal(["b:0", "b1:1", "c:0", "a:0", "a1:1"], Shape(o));
        Assert.Null(o.MoveDown(3));
        Assert.Null(o.MoveUp(1));
    }

    [Fact]
    public void RemoveSubtree()
    {
        var o = MakeOutline("a:0", "b:1", "c:0");
        o.RemoveSubtree(0);
        Assert.Equal(["c:0"], Shape(o));
    }

    [Fact]
    public void ToggleDoneCyclesThroughStates()
    {
        var o = MakeOutline("a:0");
        o.ToggleDone(0);
        Assert.Equal(TaskState.Done, o.Items[0].State);
        o.DecruftDone();
        Assert.Equal(TaskState.Decrufted, o.Items[0].State);
        o.ToggleDone(0);
        Assert.Equal(TaskState.Open, o.Items[0].State);
    }

    [Fact]
    public void DecruftOnlyAffectsDone()
    {
        var o = MakeOutline("a:0", "b:0", "c:0");
        o.ToggleDone(0);
        o.ToggleDone(2);
        Assert.Equal(2, o.DecruftDone());
        Assert.Equal([TaskState.Decrufted, TaskState.Open, TaskState.Decrufted], o.Items.Select(i => i.State));
    }

    [Fact]
    public void JoinAppendsTitleToPreviousRow()
    {
        var o = MakeOutline("ab:0", "cd:0", "e:0");
        Assert.Equal(2, o.Join(1, 0));
        Assert.Equal(["abcd:0", "e:0"], Shape(o));
    }

    [Fact]
    public void JoinMovesSubtasksToTheEndOfThePreviousRowsSubtasks()
    {
        var o = MakeOutline("a:0", "b:1", "c:0", "d:1", "e:2", "f:0");
        o.Join(2, 1);
        Assert.Equal(["a:0", "bc:1", "d:2", "e:3", "f:0"], Shape(o));
    }

    [Fact]
    public void JoinIntoParentKeepsSubtasksInPlace()
    {
        var o = MakeOutline("a:0", "b:1", "c:2", "d:1");
        o.Join(1, 0);
        Assert.Equal(["ab:0", "c:1", "d:1"], Shape(o));
    }

    [Fact]
    public void JoinIntoCollapsedRowAddsSubtasksAfterItsOwnAndExpandsIt()
    {
        var o = MakeOutline("a:0", "x:1", "b:0", "c:1");
        o.SetCollapsed(true, 0);
        o.Join(2, 0);
        Assert.Equal(["ab:0", "x:1", "c:1"], Shape(o));
        Assert.False(o.Items[0].IsCollapsed);
    }

    [Fact]
    public void JoinIntoHeadingKeepsSubtasksTopLevel()
    {
        var o = MakeOutline("#H", "b:0", "c:1", "d:2");
        o.Join(1, 0);
        Assert.Equal(["#Hb", "c:0", "d:1"], Shape(o));
    }

    [Fact]
    public void DeleteDoneRemovesDoneAndDecruftedTasks()
    {
        var o = MakeOutline("a:0", "b:1", "c:0", "#H", "d:0", "e:0");
        o.ToggleDone(1);
        o.ToggleDone(4);
        o.DecruftDone();
        o.ToggleDone(2);
        Assert.Equal(3, o.DeleteDone());
        Assert.Equal(["a:0", "#H", "e:0"], Shape(o));
    }

    [Fact]
    public void DeleteDoneKeepsOpenTasksUnderDeletedOnes()
    {
        var o = MakeOutline("a:0", "b:1", "c:2", "d:3", "e:2", "f:1", "g:0");
        o.ToggleDone(1);
        o.ToggleDone(3);
        o.DeleteDone();
        Assert.Equal(["a:0", "c:1", "e:1", "f:1", "g:0"], Shape(o));
    }

    [Fact]
    public void VisibilityHidesCollapsedChildren()
    {
        var o = MakeOutline("a:0", "b:1", "c:2", "d:0");
        o.SetCollapsed(true, 0);
        Assert.Equal([0, 3], o.VisibleIndices(false));
        o.SetCollapsed(false, 0);
        o.SetCollapsed(true, 1);
        Assert.Equal([0, 1, 3], o.VisibleIndices(false));
    }

    [Fact]
    public void VisibilityHidesDecruftedSubtreesUnlessShown()
    {
        var o = MakeOutline("a:0", "b:1", "c:0");
        o.ToggleDone(0);
        o.DecruftDone();
        Assert.Equal([2], o.VisibleIndices(false));
        Assert.Equal([0, 1, 2], o.VisibleIndices(true));
    }

    // MARK: Headings

    [Fact]
    public void HeadingsAreTopLevelAndResetDepth()
    {
        var o = new Outline(items:
        [
            new TaskItem("a", depth: 0),
            new TaskItem("H", TaskState.Done, depth: 3, headingLevel: 9),
            new TaskItem("b", depth: 1),
        ]);
        Assert.Equal(["a:0", "###H", "b:0"], Shape(o));
        Assert.Equal(TaskState.Open, o.Items[1].State);
        Assert.False(o.HasChildren(1));
    }

    [Fact]
    public void SectionsEndAtSameOrHigherHeading()
    {
        var o = MakeOutline("#A", "a:0", "##B", "b:0", "###C", "c:0", "##D", "#E");
        Assert.Equal(7, o.SectionEnd(0));
        Assert.Equal(6, o.SectionEnd(2));
        Assert.Equal(6, o.SectionEnd(4));
        Assert.True(o.CanCollapse(0));
        Assert.False(o.CanCollapse(6));
        Assert.False(o.CanCollapse(7));
    }

    [Fact]
    public void CollapsedHeadingHidesItsSection()
    {
        var o = MakeOutline("#A", "a:0", "a1:1", "##B", "b:0", "#C", "c:0");
        o.SetCollapsed(true, 0);
        Assert.Equal([0, 5, 6], o.VisibleIndices(false));
        o.SetCollapsed(false, 0);
        o.SetCollapsed(true, 3);
        Assert.Equal([0, 1, 2, 3, 5, 6], o.VisibleIndices(false));
    }

    [Fact]
    public void HeadingsCantBeIndentedDoneOrParents()
    {
        var o = MakeOutline("a:0", "#H", "b:0");
        Assert.False(o.Indent(1));
        Assert.False(o.Indent(2));
        o.ToggleDone(1);
        Assert.Equal(TaskState.Open, o.Items[1].State);
        Assert.Equal(["a:0", "#H", "b:0"], Shape(o));
    }

    [Fact]
    public void TasksMoveAcrossHeadingsButHeadingsDont()
    {
        var o = MakeOutline("a:0", "#H", "b:0", "b1:1");
        Assert.Equal(1, o.MoveUp(2));
        Assert.Equal(["a:0", "b:0", "b1:1", "#H"], Shape(o));
        Assert.Null(o.MoveUp(3));
        Assert.Equal(2, o.MoveDown(1));
        Assert.Equal(["a:0", "#H", "b:0", "b1:1"], Shape(o));
    }

    [Fact]
    public void MovingIntoCollapsedSectionExpandsIt()
    {
        var o = MakeOutline("#A", "x:0", "y:0", "#B", "z:0");
        o.SetCollapsed(true, 0);
        o.MoveUp(4);
        Assert.Equal(["#A", "x:0", "y:0", "z:0", "#B"], Shape(o));
        Assert.False(o.Items[0].IsCollapsed);
    }

    [Fact]
    public void InsertingAfterCollapsedHeadingExpandsIt()
    {
        var o = MakeOutline("#A", "x:0");
        o.SetCollapsed(true, 0);
        Assert.Equal(1, o.InsertItem(0));
        Assert.Equal(["#A", ":0", "x:0"], Shape(o));
        Assert.False(o.Items[0].IsCollapsed);
    }

    [Fact]
    public void MakeHeadingOutdentsAndPromotesChildren()
    {
        var o = MakeOutline("p:0", "t:1", "c:2", "g:3", "s:1");
        o.ToggleDone(1);
        var index = o.MakeHeading(2, 1);
        Assert.Equal(2, index);
        Assert.Equal(["p:0", "s:1", "##t", "c:0", "g:1"], Shape(o));
        Assert.Equal(TaskState.Open, o.Items[index].State);

        o.MakeTask(index);
        Assert.Equal(["p:0", "s:1", "t:0", "c:0", "g:1"], Shape(o));
    }
}
