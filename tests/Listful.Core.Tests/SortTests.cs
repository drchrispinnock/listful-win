namespace Listful.Tests;

public class SortTests
{
    private static Outline Parse(string text) => MarkdownCodec.Decode(text);
    private static string[] Titles(Outline outline) => outline.Items.Select(i => i.Title).ToArray();

    [Fact]
    public void SortsSiblingsAToZWithNumbersByValue()
    {
        var o = Parse("- [ ] b\n- [ ] 10 ten\n- [ ] A\n- [ ] 2 two\n- [ ] \n");
        Assert.True(o.SortTasks(0));
        Assert.Equal(["2 two", "10 ten", "A", "b", ""], Titles(o));
    }

    [Fact]
    public void SubtasksMoveWithTheirParents()
    {
        var o = Parse("- [ ] b\n  - [ ] b1\n- [x] a\n  - [ ] a1\n    - [ ] a11\n- [ ] c\n");
        Assert.True(o.SortTasks(0));  // "b" has subtasks, but sorts its own level
        Assert.Equal(["a", "a1", "a11", "b", "b1", "c"], Titles(o));
        Assert.Equal(TaskState.Done, o.Items[0].State);
        Assert.Equal([0, 1, 2, 0, 1, 0], o.Items.Select(i => i.Depth));
    }

    [Fact]
    public void CollapsedParentsSortTheirLevel()
    {
        // As in a real list: almost every task has hidden subtasks.
        var o = Parse(
            "- [ ] Password <!-- listful: collapsed -->\n  - [ ] Videos\n  - [ ] Ramp\n" +
            "- [ ] Domain <!-- listful: collapsed -->\n  - [ ] x\n- [ ] MTA");
        Assert.True(o.SortTasks(0));
        Assert.Equal(["Domain", "x", "MTA", "Password", "Videos", "Ramp"], Titles(o));
    }

    [Fact]
    public void ASubtaskSortsItsSiblings()
    {
        var o = Parse("- [ ] z\n- [ ] Parent\n  - [ ] c\n  - [ ] a\n    - [ ] deep\n  - [ ] b\n");
        Assert.True(o.SortTasks(2));
        Assert.Equal(["z", "Parent", "a", "deep", "b", "c"], Titles(o));
    }

    [Fact]
    public void TopLevelSortsStayBetweenHeadings()
    {
        var o = Parse("- [ ] y\n- [ ] x\n\n## H\n\n- [ ] b\n- [ ] a\n\n### Sub\n\n- [ ] d\n- [ ] c\n");
        Assert.True(o.SortTasks(4));  // "b", under H
        Assert.Equal(["y", "x", "H", "a", "b", "Sub", "d", "c"], Titles(o));
        Assert.True(o.SortTasks(0));
        Assert.Equal(["x", "y", "H", "a", "b", "Sub", "d", "c"], Titles(o));
        Assert.True(o.SortTasks(5));  // the heading "Sub" sorts its section
        Assert.Equal(["x", "y", "H", "a", "b", "Sub", "c", "d"], Titles(o));
    }

    [Fact]
    public void NothingToSort()
    {
        var o = Parse("## H\n\n## Empty\n\n- [ ] only\n");
        Assert.Null(o.SortRange(0));
        Assert.Null(o.SortRange(2));
        Assert.False(o.SortTasks(2));
        var sorted = Parse("- [ ] a\n- [ ] b\n");
        Assert.Equal(new IntRange(0, 2), sorted.SortRange(0));
        Assert.False(sorted.SortTasks(0));
    }

    [Fact]
    public void EqualTitlesKeepTheirOrder()
    {
        var o = Parse("- [ ] b\n- [ ] a\n  - [ ] first\n- [ ] a\n  - [ ] second\n");
        o.SortTasks(0);
        Assert.Equal(["a", "first", "a", "second", "b"], Titles(o));
    }
}
