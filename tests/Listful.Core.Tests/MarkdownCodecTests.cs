namespace Listful.Tests;

public class MarkdownCodecTests
{
    private const string Sample =
        "- [ ] Groceries <!-- listful: collapsed -->\n" +
        "  - [x] Milk\n" +
        "  - [ ] Bread\n" +
        "- [x] Old thing <!-- listful: decrufted -->\n" +
        "  - [ ] Leftover\n" +
        "- [ ] Empty parent marker ignored\n";

    [Fact]
    public void DecodesStatesDepthsAndFlags()
    {
        var o = MarkdownCodec.Decode(Sample);
        Assert.Equal(["Groceries", "Milk", "Bread", "Old thing", "Leftover", "Empty parent marker ignored"], o.Items.Select(i => i.Title));
        Assert.Equal([0, 1, 1, 0, 1, 0], o.Items.Select(i => i.Depth));
        Assert.Equal([TaskState.Open, TaskState.Done, TaskState.Open, TaskState.Decrufted, TaskState.Open, TaskState.Open], o.Items.Select(i => i.State));
        Assert.Equal([true, false, false, false, false, false], o.Items.Select(i => i.IsCollapsed));
    }

    [Fact]
    public void RoundTripsCanonicalText() => Assert.Equal(Sample, MarkdownCodec.Encode(MarkdownCodec.Decode(Sample)));

    [Fact]
    public void ReadsCRLF() =>
        Assert.Equal(Sample, MarkdownCodec.Encode(MarkdownCodec.Decode(Sample.Replace("\n", "\r\n"))));

    [Fact]
    public void CollapsedFlagOnlyWrittenWhenThereAreChildren()
    {
        var o = new Outline(items: [new TaskItem("solo", isCollapsed: true)]);
        Assert.Equal("- [ ] solo\n", MarkdownCodec.Encode(o));
        o.InsertItem(0);
        Assert.Equal("- [ ] solo\n- [ ] \n", MarkdownCodec.Encode(o));
    }

    [Fact]
    public void ReadsMarkersFromBeforeTheRename()
    {
        var o = MarkdownCodec.Decode("- [ ] a <!-- taskmgr: collapsed -->\n  - [x] b <!-- taskmgr: decrufted -->\n");
        Assert.Equal(["a", "b"], o.Items.Select(i => i.Title));
        Assert.True(o.Items[0].IsCollapsed);
        Assert.Equal(TaskState.Decrufted, o.Items[1].State);
        Assert.Equal("- [ ] a <!-- listful: collapsed -->\n  - [x] b <!-- listful: decrufted -->\n", MarkdownCodec.Encode(o));
    }

    [Fact]
    public void EmptyOutlineEncodesToEmptyString()
    {
        Assert.Equal("", MarkdownCodec.Encode(new Outline()));
        Assert.True(MarkdownCodec.Decode("").IsEmpty);
    }

    [Fact]
    public void AcceptsHandWrittenVariants()
    {
        var text = "Intro line\n* plain bullet\n\t+ [X] tab-indented done\n    - four-space child\n-";
        var o = MarkdownCodec.Decode(text);
        Assert.Equal(["Intro line", "plain bullet", "tab-indented done", "four-space child", ""], o.Items.Select(i => i.Title));
        Assert.Equal([0, 0, 1, 1, 0], o.Items.Select(i => i.Depth));
        Assert.Equal([TaskState.Open, TaskState.Open, TaskState.Done, TaskState.Open, TaskState.Open], o.Items.Select(i => i.State));
    }

    // MARK: Headings and titles

    private const string Headed =
        "# Weekend\n\n" +
        "- [ ] Loose task\n\n" +
        "## Chores <!-- listful: collapsed -->\n\n" +
        "- [ ] Laundry\n" +
        "  - [x] Sort colours\n\n" +
        "### Garden\n\n" +
        "- [ ] Mow\n\n" +
        "# Fun\n\n" +
        "- [x] Cinema <!-- listful: decrufted -->\n";

    [Fact]
    public void DecodesTitleAndHeadings()
    {
        var o = MarkdownCodec.Decode(Headed);
        Assert.Equal("Weekend", o.Title);
        Assert.Equal(["Loose task", "Chores", "Laundry", "Sort colours", "Garden", "Mow", "Fun", "Cinema"], o.Items.Select(i => i.Title));
        Assert.Equal([null, 2, null, null, 3, null, 1, null], o.Items.Select(i => i.HeadingLevel));
        Assert.Equal([0, 0, 0, 1, 0, 0, 0, 0], o.Items.Select(i => i.Depth));
        Assert.True(o.Items[1].IsCollapsed);
    }

    [Fact]
    public void RoundTripsTitleAndHeadings() => Assert.Equal(Headed, MarkdownCodec.Encode(MarkdownCodec.Decode(Headed)));

    [Fact]
    public void TitleOnlyComesFromALevelOneHeadingOnTheFirstLine()
    {
        Assert.Equal("Top", MarkdownCodec.Decode("\n\n# Top\n- [ ] a\n").Title);
        Assert.Null(MarkdownCodec.Decode("## Not title\n").Title);
        Assert.Null(MarkdownCodec.Decode("- [ ] a\n# Later\n").Title);
        Assert.Equal(1, MarkdownCodec.Decode("- [ ] a\n# Later\n").Items[1].HeadingLevel);
        Assert.Equal("Top", MarkdownCodec.TitleIn("\n# Top <!-- listful: collapsed -->\n- [ ] a"));
        Assert.Null(MarkdownCodec.TitleIn("- [ ] a\n# Later"));
        Assert.Null(MarkdownCodec.TitleIn(""));
    }

    [Fact]
    public void TitleOnlyOutlineEncodes() => Assert.Equal("# Empty\n", MarkdownCodec.Encode(new Outline("Empty")));

    [Fact]
    public void HeadingSyntaxEdgeCases()
    {
        Assert.Equal((2, "Title"), MarkdownCodec.ParseHeading("## Title ##"));
        Assert.Equal((1, "C#"), MarkdownCodec.ParseHeading("# C#"));
        Assert.Equal((1, ""), MarkdownCodec.ParseHeading("#"));
        Assert.Null(MarkdownCodec.ParseHeading("#tag"));
        Assert.Null(MarkdownCodec.ParseHeading("#### Deep"));

        // Not headings: deeper levels, indented, or inside a task's text.
        var o = MarkdownCodec.Decode("- [ ] first\n#### Deep\n  # indented\n- [ ] # literal\n");
        Assert.All(o.Items, i => Assert.False(i.IsHeading));
        Assert.Equal(["first", "#### Deep", "# indented", "# literal"], o.Items.Select(i => i.Title));
    }

    [Fact]
    public void HeadingResetsIndentation() =>
        Assert.Equal([0, 1, 0, 0, 1], MarkdownCodec.Decode("- [ ] a\n  - [ ] b\n## H\n  - [ ] c\n    - [ ] d\n").Items.Select(i => i.Depth));

    [Fact]
    public void IrregularIndentationNestsRelatively()
    {
        var text = "- a\n   - b\n         - c\n   - d\n - e";
        Assert.Equal([0, 1, 2, 1, 1], MarkdownCodec.Decode(text).Items.Select(i => i.Depth));
    }

    [Fact]
    public void FileStoreRoundTripsAndTreatsMissingFileAsEmpty()
    {
        using var dir = new TempFolder();
        var store = new MarkdownFileStore(Path.Combine(dir.Path, "nested", "Tasks.md"));

        Assert.True(store.Load().IsEmpty);

        store.Save(MarkdownCodec.Decode(Sample));
        Assert.Equal(Sample, MarkdownCodec.Encode(store.Load()));
        // UTF-8 without a BOM, LF line endings, as the Mac writes.
        var bytes = File.ReadAllBytes(store.FilePath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    // MARK: - Heading attribute blocks

    [Fact]
    public void HeadingAttributeBlocksAreHiddenButKept()
    {
        var text = "# Project {#top}\n\n## Setup {#setup .wide}\n\n- [ ] Install\n\n### Notes {-}\n\n- [ ] Read\n";
        var outline = MarkdownCodec.Decode(text);
        Assert.Equal("Project", outline.Title);
        Assert.Equal("{#top}", outline.TitleAttributes);
        Assert.Equal(["Setup", "Install", "Notes", "Read"], outline.Items.Select(i => i.Title));
        Assert.Equal("{#setup .wide}", outline.Items[0].HeadingAttributes);
        Assert.Equal("{-}", outline.Items[2].HeadingAttributes);
        Assert.Equal(text, MarkdownCodec.Encode(outline));
        Assert.Equal("Project", MarkdownCodec.TitleIn(text));
    }

    [Fact]
    public void RenamingAHeadingKeepsItsAttributes()
    {
        var outline = MarkdownCodec.Decode("## Setup {#setup}\n");
        outline.SetTitle("Getting started", 0);
        Assert.Equal("## Getting started {#setup}\n", MarkdownCodec.Encode(outline));
    }

    [Fact]
    public void OnlyATrailingBlockAfterTextIsAnAttributeBlock()
    {
        Assert.Equal(("{#only}", (string?)null), MarkdownCodec.SplitAttributes("{#only}"));
        Assert.Equal(("Use {braces} here", (string?)null), MarkdownCodec.SplitAttributes("Use {braces} here"));
        Assert.Equal(("Set {a}", (string?)"{#b}"), MarkdownCodec.SplitAttributes("Set {a} {#b}"));
        Assert.Equal(("Empty", (string?)"{}"), MarkdownCodec.SplitAttributes("Empty {}"));
        // Tasks keep their braces.
        Assert.Equal("Task {#x}", MarkdownCodec.Decode("- [ ] Task {#x}\n").Items[0].Title);
    }
}
