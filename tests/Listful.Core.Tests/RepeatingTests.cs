using System.Globalization;

namespace Listful.Tests;

internal static class RepeatingSupport
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>3 pm on a date given as "yyyy-MM-dd".</summary>
    public static DateTime Day(string text)
    {
        var parts = text.Split('-').Select(int.Parse).ToArray();
        return new DateTime(parts[0], parts[1], parts[2], 15, 0, 0);
    }

    public static string Key(RepeatSchedule schedule, string text) => schedule.PeriodKey(schedule.Occurrence(Day(text)));
}

public class RepeatScheduleTests
{
    private static string Key(RepeatSchedule s, string on) => RepeatingSupport.Key(s, on);
    private static DateTime Day(string text) => RepeatingSupport.Day(text);
    private static readonly RepeatSchedule.Frequency Daily = RepeatSchedule.Frequency.Daily;

    [Fact]
    public void DailyPeriodIsTheDay() => Assert.Equal("2026-11-03", Key(new RepeatSchedule(Daily), "2026-11-03"));

    [Fact]
    public void WeekdaysSkipTheWeekend()
    {
        var schedule = new RepeatSchedule(RepeatSchedule.Frequency.Weekdays);
        Assert.Equal("2026-11-06", Key(schedule, "2026-11-06")); // Friday
        Assert.Equal("2026-11-06", Key(schedule, "2026-11-08")); // Sunday
        Assert.Equal("2026-11-09", Key(schedule, "2026-11-09")); // Monday
    }

    [Fact]
    public void WeeklyPeriodStartsOnTheChosenDays()
    {
        var schedule = new RepeatSchedule(RepeatSchedule.Frequency.Weekly, weekdays: [2, 4]); // Mon, Wed
        Assert.Equal("2026-11-02", Key(schedule, "2026-11-02"));
        Assert.Equal("2026-11-02", Key(schedule, "2026-11-03"));
        Assert.Equal("2026-11-04", Key(schedule, "2026-11-04"));
        Assert.Equal("2026-11-04", Key(schedule, "2026-11-08"));
    }

    [Fact]
    public void MonthlyDayIsClampedToShortMonths()
    {
        var schedule = new RepeatSchedule(RepeatSchedule.Frequency.Monthly, dayOfMonth: 31);
        Assert.Equal(Day("2026-02-28").Date, schedule.Occurrence(Day("2026-02-28")));
        Assert.Equal("2026-02", Key(schedule, "2026-03-30"));
        Assert.Equal("2026-03", Key(schedule, "2026-03-31"));
    }

    [Fact]
    public void MonthlyBeforeTheDayIsLastMonth()
    {
        var schedule = new RepeatSchedule(RepeatSchedule.Frequency.Monthly, dayOfMonth: 15);
        Assert.Equal("2025-12", Key(schedule, "2026-01-14"));
        Assert.Equal("2026-01", Key(schedule, "2026-01-15"));
    }

    [Fact]
    public void LastDayOfMonth()
    {
        var schedule = new RepeatSchedule(RepeatSchedule.Frequency.Monthly, dayOfMonth: 0);
        Assert.Equal(Day("2028-02-29").Date, schedule.Occurrence(Day("2028-02-29")));
        Assert.Equal("2028-01", Key(schedule, "2028-02-28"));
    }

    [Fact]
    public void QuartersAndYears()
    {
        Assert.Equal("2026-Q4", Key(new RepeatSchedule(RepeatSchedule.Frequency.Quarterly), "2026-11-03"));
        Assert.Equal("2026-Q1", Key(new RepeatSchedule(RepeatSchedule.Frequency.Quarterly), "2026-01-01"));
        var yearly = new RepeatSchedule(RepeatSchedule.Frequency.Yearly, month: 4, day: 6);
        Assert.Equal("2025", Key(yearly, "2026-04-05"));
        Assert.Equal("2026", Key(yearly, "2026-04-06"));
    }

    [Fact]
    public void Labels()
    {
        var occurrence = Day("2026-11-03").Date;
        var culture = RepeatingSupport.Culture;
        Assert.Equal("Tue 3 Nov", new RepeatSchedule(Daily).PeriodLabel(occurrence, culture));
        Assert.Equal("November 2026", new RepeatSchedule(RepeatSchedule.Frequency.Monthly).PeriodLabel(occurrence, culture));
        Assert.Equal("Q4 2026", new RepeatSchedule(RepeatSchedule.Frequency.Quarterly).PeriodLabel(occurrence, culture));
        // Windows only: US English puts the month first.
        Assert.Equal("Tue, Nov 3", new RepeatSchedule(Daily).PeriodLabel(occurrence, CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Summaries()
    {
        var culture = RepeatingSupport.Culture;
        Assert.Equal("Repeats weekly on Mon and Wed", new RepeatSchedule(RepeatSchedule.Frequency.Weekly, weekdays: [4, 2]).Summary(culture));
        Assert.Equal("Repeats monthly on the 22nd", new RepeatSchedule(RepeatSchedule.Frequency.Monthly, dayOfMonth: 22).Summary(culture));
        Assert.Equal("Repeats yearly on 3 November", new RepeatSchedule(RepeatSchedule.Frequency.Yearly, month: 11, day: 3).Summary(culture));
    }

    [Fact]
    public void KeysAreRecognisedByFrequency()
    {
        Assert.True(new RepeatSchedule(Daily).IsPeriodKey("2026-11-03"));
        Assert.False(new RepeatSchedule(Daily).IsPeriodKey("2026-11"));
        Assert.True(new RepeatSchedule(RepeatSchedule.Frequency.Quarterly).IsPeriodKey("2026-Q4"));
        Assert.False(new RepeatSchedule(RepeatSchedule.Frequency.Yearly).IsPeriodKey("notes"));
    }

    [Fact]
    public void MarkerRoundTrips()
    {
        RepeatSchedule[] schedules =
        [
            new(Daily, carriesOver: true),
            new(RepeatSchedule.Frequency.Weekly, weekdays: [2, 4, 6]),
            new(RepeatSchedule.Frequency.Monthly, dayOfMonth: 0, carriesOver: true),
            new(RepeatSchedule.Frequency.Yearly, month: 11, day: 3),
        ];
        foreach (var schedule in schedules)
        {
            var outline = new Outline("Routine", [new TaskItem("Stretch")], schedule);
            var text = MarkdownCodec.Encode(outline);
            var decoded = MarkdownCodec.Decode(text);
            Assert.Equal(schedule, decoded.Schedule);
            Assert.Equal("Routine", decoded.Title);
            Assert.Equal(["Stretch"], decoded.Items.Select(i => i.Title));
            Assert.Equal(text, MarkdownCodec.Encode(decoded));
        }
    }

    [Fact]
    public void TemplateFileFormat()
    {
        var outline = new Outline("Routine", [new TaskItem("Stretch")],
            new RepeatSchedule(RepeatSchedule.Frequency.Weekly, weekdays: [4, 2], carriesOver: true));
        Assert.Equal("# Routine\n<!-- listful: repeat=weekly:mon,wed carry-over -->\n\n- [ ] Stretch\n", MarkdownCodec.Encode(outline));
        Assert.Equal("Routine", MarkdownCodec.TitleIn("<!-- listful: repeat=daily -->\n# Routine\n"));
    }
}

public class CarryOverTests
{
    private static (string Copy, string? Previous) Copy(string template, string? previous = null)
    {
        var made = RepeatingChecklists.MakeCopy(
            MarkdownCodec.Decode(template), "Routine", "Tue 3 Nov",
            previous == null ? null : MarkdownCodec.Decode(previous));
        return (MarkdownCodec.Encode(made.Copy), made.Previous == null ? null : MarkdownCodec.Encode(made.Previous));
    }

    [Fact]
    public void CopyResetsTasksAndDropsTheSchedule()
    {
        var made = Copy("# Routine\n<!-- listful: repeat=daily -->\n- [x] Stretch\n- [ ] Plan");
        Assert.Equal("# Routine — Tue 3 Nov\n\n- [ ] Stretch\n- [ ] Plan\n", made.Copy);
        Assert.Null(made.Previous);
    }

    [Fact]
    public void BlankTasksAreNeitherCopiedNorCarried()
    {
        var made = Copy("- [ ] Rise\n  - [ ] \n- [ ] \n", "- [ ] Call Bob\n- [ ] \n");
        Assert.Equal("# Routine — Tue 3 Nov\n\n- [ ] Call Bob\n- [ ] Rise\n", made.Copy);
        Assert.Equal("- [ ] \n", made.Previous);
    }

    [Fact]
    public void UnfinishedTasksMoveToTheTop()
    {
        var made = Copy("- [ ] Stretch\n",
            "# Routine — Mon 2 Nov\n- [x] Stretch\n- [ ] Call Bob\n  - [x] Find number\n- [ ] Email Ann");
        Assert.Equal("# Routine — Tue 3 Nov\n\n- [ ] Call Bob\n  - [x] Find number\n- [ ] Email Ann\n- [ ] Stretch\n", made.Copy);
        Assert.Equal("# Routine — Mon 2 Nov\n\n- [x] Stretch\n", made.Previous);
    }

    [Fact]
    public void OpenSubtasksOfDoneTasksMoveOnTheirOwn()
    {
        var made = Copy("- [ ] Stretch\n", "- [x] Project\n  - [x] Draft\n  - [ ] Review");
        Assert.Contains("- [ ] Review\n- [ ] Stretch", made.Copy);
        Assert.Equal("- [x] Project\n  - [x] Draft\n", made.Previous);
    }

    [Fact]
    public void TemplateTasksAreNotDuplicated()
    {
        var made = Copy("- [ ] Stretch\n- [ ] Plan\n", "- [ ] Stretch\n  - [ ] Hamstrings\n- [x] Plan");
        Assert.Equal("# Routine — Tue 3 Nov\n\n- [ ] Stretch\n  - [ ] Hamstrings\n- [ ] Plan\n", made.Copy);
        Assert.Equal("- [x] Plan\n", made.Previous);
    }

    [Fact]
    public void TasksKeepTheirHeadings()
    {
        var made = Copy(
            "## Morning\n\n- [ ] Stretch\n\n## Evening\n\n- [ ] Read",
            "## Evening\n\n- [ ] Tidy desk\n\n## Errands\n\n- [ ] Post office");
        Assert.Equal(
            "# Routine — Tue 3 Nov\n\n## Morning\n\n- [ ] Stretch\n\n## Evening\n\n- [ ] Tidy desk\n- [ ] Read\n\n## Errands\n\n- [ ] Post office\n",
            made.Copy);
        Assert.Equal("## Evening\n\n## Errands\n", made.Previous);
    }
}

public sealed class MakeDueCopiesTests : IDisposable
{
    private readonly TempFolder folder = new();
    private TaskLibrary Lists => new(folder.Path);
    private TaskLibrary Templates => new(Lists.TemplatesFolderPath);

    public void Dispose() => folder.Dispose();

    private List<RepeatingChecklists.Result> Run(string on) =>
        RepeatingChecklists.MakeDueCopies(Templates, Lists, RepeatingSupport.Day(on), RepeatingSupport.Culture);

    private string? Read(string name) => new MarkdownFileStore(Lists.PathForName(name)).ReadText();

    [Fact]
    public void MakesEachPeriodsCopyOnceAndCarriesOver()
    {
        Directory.CreateDirectory(Templates.FolderPath);
        Templates.CreateList("Routine", new Outline("Routine", [new TaskItem("Stretch")],
            new RepeatSchedule(RepeatSchedule.Frequency.Daily, carriesOver: true)));

        Assert.Equal([Lists.PathForName("Routine 2026-11-02")], Run("2026-11-02").Select(r => r.Copy));
        Assert.Empty(Run("2026-11-02"));

        var monday = Lists.PathForName("Routine 2026-11-02");
        File.WriteAllText(monday, "# Routine — Mon 2 Nov\n\n- [x] Stretch\n- [ ] Call Bob\n");

        // Wednesday after a day off: one copy, carrying over from Monday's.
        var results = Run("2026-11-04");
        Assert.Equal([monday], results.Select(r => r.Previous));
        Assert.Equal("# Routine — Wed 4 Nov\n\n- [ ] Call Bob\n- [ ] Stretch\n", Read("Routine 2026-11-04"));
        Assert.Equal("# Routine — Mon 2 Nov\n\n- [x] Stretch\n", Read("Routine 2026-11-02"));
        Assert.Null(Read("Routine 2026-11-03"));
    }

    [Fact]
    public void CopiesAreFoundNewestFirst()
    {
        var paths = new[] { "Routine 2026-11-02", "Routine 2026-11-03", "Routine notes", "Routines 2026-11-04", "Routine 2026-Q4", "Other 2026-11-03" }
            .Select(Lists.PathForName);
        Assert.Equal(["Routine 2026-Q4", "Routine 2026-11-03", "Routine 2026-11-02"],
            RepeatingChecklists.Copies("Routine", paths).Select(TaskLibrary.DisplayName));
    }

    [Fact]
    public void SyncDuplicatesCountAsCopiesAfterTheirOriginal()
    {
        var paths = new[] { "Routine 2026-11-03 2", "Routine 2026-11-03", "Routine 2026-11-02", "Routine 2026-11-03 notes" }
            .Select(Lists.PathForName).ToList();
        Assert.Equal(["Routine 2026-11-03", "Routine 2026-11-03 2", "Routine 2026-11-02"],
            RepeatingChecklists.Copies("Routine", paths).Select(TaskLibrary.DisplayName));
        Assert.Equal(("2026-11-03", 2), RepeatingChecklists.CopyKey(paths[0], "Routine"));
    }

    [Fact]
    public void ADuplicateFoldsIntoItsOriginal()
    {
        var original = MarkdownCodec.Decode("# Routine\n\n- [x] Stretch\n- [ ] Call Bob\n  - [ ] Ask about Friday\n\n## Evening\n\n- [ ] Read\n");
        var duplicate = MarkdownCodec.Decode("# Routine\n\n- [ ] Stretch\n- [ ] Call Bob\n  - [x] Ask about Friday\n  - [ ] Book table\n- [ ] \n\n## Evening\n\n- [x] Read\n- [ ] Walk\n\n## Notes\n\n- [ ] Idea\n");
        Assert.Equal(
            "# Routine\n\n- [x] Stretch\n- [ ] Call Bob\n  - [x] Ask about Friday\n  - [ ] Book table\n\n## Evening\n\n- [x] Read\n- [ ] Walk\n\n## Notes\n\n- [ ] Idea\n",
            MarkdownCodec.Encode(RepeatingChecklists.Merging(duplicate, original)));
        // An untouched duplicate changes nothing.
        var untouched = MarkdownCodec.Decode("# Routine\n\n- [ ] Stretch\n- [ ] Call Bob\n  - [ ] Ask about Friday\n\n## Evening\n\n- [ ] Read\n");
        Assert.Equal(original, RepeatingChecklists.Merging(untouched, original));
    }

    [Fact]
    public void MergeDuplicatesFoldsAndTrashes()
    {
        Directory.CreateDirectory(Templates.FolderPath);
        Templates.CreateList("Routine", new Outline("Routine", [new TaskItem("Stretch")], new RepeatSchedule()));
        File.WriteAllText(Lists.PathForName("Routine 2026-11-03"), "- [ ] Stretch\n");
        File.WriteAllText(Lists.PathForName("Routine 2026-11-03 2"), "- [x] Stretch\n");
        var trashed = RepeatingChecklists.MergeDuplicates(Templates, Lists);
        Assert.Equal([Lists.PathForName("Routine 2026-11-03 2")], trashed);
        Assert.Equal("- [x] Stretch\n", Read("Routine 2026-11-03"));
        Assert.Equal(["Routine 2026-11-03"], Lists.Lists().Select(TaskLibrary.DisplayName));
    }

    [Fact]
    public void BlankRowsDontCountAsOpen()
    {
        Assert.False(RepeatingChecklists.HasOpenTasks(MarkdownCodec.Decode("## Done\n\n- [x] Stretch\n- [ ] \n")));
        Assert.True(RepeatingChecklists.HasOpenTasks(MarkdownCodec.Decode("- [x] Stretch\n  - [ ] Hamstrings\n")));
    }

    [Fact]
    public void EmptyTemplatesAreSkipped()
    {
        Directory.CreateDirectory(Templates.FolderPath);
        Templates.CreateList("New", new Outline("New", [new TaskItem("")], new RepeatSchedule()));
        Templates.CreateList("Plain", new Outline(items: [new TaskItem("No schedule")]));
        Assert.Empty(Run("2026-11-02"));
        Assert.Empty(Lists.Lists());
    }
}
