using System.Globalization;
using System.Text.RegularExpressions;

namespace Listful;

/// <summary>
/// How often a repeating checklist's template is copied into a fresh list,
/// and whether a new copy takes over the previous copy's unfinished tasks.
/// <para>Stored in the template file as a marker line after its title:</para>
/// <code>
/// # Morning Routine
/// &lt;!-- listful: repeat=weekly:mon,wed carry-over --&gt;
/// </code>
/// Every frequency's options are kept (so switching frequency in the editor
/// and back doesn't lose them), but only the current frequency's are written
/// or compared.
/// <para>
/// Windows difference: upstream takes a <c>Calendar</c> (time zone, first weekday); here dates are
/// local <see cref="DateTime"/>s, read by their <c>.Date</c>, and the week starts on the culture's
/// first day.
/// </para>
/// </summary>
public sealed record RepeatSchedule
{
    public enum Frequency { Daily, Weekdays, Weekly, Monthly, Quarterly, Yearly }

    public Frequency FrequencyValue { get; set; }
    /// <summary>For weekly: Swift <c>Calendar</c> weekday numbers, 1 = Sunday … 7 = Saturday.</summary>
    public HashSet<int> WeekdaysValue { get; set; }
    /// <summary>For monthly: 1–31, clamped to the month's length; 0 is the last day.</summary>
    public int DayOfMonth { get; set; }
    /// <summary>For yearly: the month (1–12) and day (1–31, clamped as for monthly).</summary>
    public int Month { get; set; }
    public int Day { get; set; }
    /// <summary>Whether a new copy takes the previous copy's unfinished tasks, removing them from it.</summary>
    public bool CarriesOver { get; set; }

    public RepeatSchedule(
        Frequency frequency = Frequency.Daily,
        IEnumerable<int>? weekdays = null,
        int dayOfMonth = 1,
        int month = 1,
        int day = 1,
        bool carriesOver = false)
    {
        FrequencyValue = frequency;
        WeekdaysValue = weekdays?.ToHashSet() ?? [2];
        DayOfMonth = dayOfMonth;
        Month = month;
        Day = day;
        CarriesOver = carriesOver;
    }

    /// <summary>A copy that can be changed without changing this one.</summary>
    public RepeatSchedule Copy() => new(FrequencyValue, WeekdaysValue, DayOfMonth, Month, Day, CarriesOver);

    public bool Equals(RepeatSchedule? other) => other is not null && MarkerWords.SequenceEqual(other.MarkerWords);
    public override int GetHashCode() => string.Join(" ", MarkerWords).GetHashCode();

    // MARK: - Periods

    /// <summary>
    /// The start (midnight) of the period containing <paramref name="date"/>: the most recent
    /// day on or before it that the schedule makes a copy.
    /// </summary>
    public DateTime Occurrence(DateTime date)
    {
        var today = date.Date;
        int year = today.Year, thisMonth = today.Month;
        switch (FrequencyValue)
        {
            case Frequency.Daily:
                return today;
            case Frequency.Weekdays:
            case Frequency.Weekly:
                var days = FrequencyValue == Frequency.Weekdays ? [2, 3, 4, 5, 6] : EffectiveWeekdays;
                for (var back = 0; back < 7; back++)
                {
                    var day = today.AddDays(-back);
                    if (days.Contains(WeekdayNumber(day))) return day;
                }
                return today;
            case Frequency.Monthly:
                var candidate = DateFor(year, thisMonth, DayOfMonth);
                if (candidate <= today) return candidate;
                var previous = new DateTime(year, thisMonth, 1).AddMonths(-1);
                return DateFor(previous.Year, previous.Month, DayOfMonth);
            case Frequency.Quarterly:
                return DateFor(year, (thisMonth - 1) / 3 * 3 + 1, 1);
            default:
                var yearly = DateFor(year, Month, Day);
                return yearly <= today ? yearly : DateFor(year - 1, Month, Day);
        }
    }

    /// <summary>Names the period starting at <paramref name="occurrence"/> in copies' file names.
    /// Keys of one frequency sort in date order.</summary>
    public string PeriodKey(DateTime occurrence) => FrequencyValue switch
    {
        Frequency.Daily or Frequency.Weekdays or Frequency.Weekly =>
            occurrence.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Frequency.Monthly => occurrence.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        Frequency.Quarterly => $"{occurrence.Year:D4}-Q{(occurrence.Month - 1) / 3 + 1}",
        _ => $"{occurrence.Year:D4}",
    };

    /// <summary>Whether <paramref name="key"/> has the shape <see cref="PeriodKey"/> gives for this frequency.</summary>
    public bool IsPeriodKey(string key)
    {
        var pattern = FrequencyValue switch
        {
            Frequency.Daily or Frequency.Weekdays or Frequency.Weekly => @"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z",
            Frequency.Monthly => @"^[0-9]{4}-[0-9]{2}\z",
            Frequency.Quarterly => @"^[0-9]{4}-Q[1-4]\z",
            _ => @"^[0-9]{4}\z",
        };
        return Regex.IsMatch(key, pattern);
    }

    /// <summary>
    /// Names the period starting at <paramref name="occurrence"/> in copies' titles, such as
    /// "Tue 3 Nov", "November 2026" or "Q4 2026". (Upstream asks the locale for the
    /// <c>EEEdMMM</c> and <c>MMMMyyyy</c> templates; .NET has no templates, so the culture's
    /// month-day pattern decides whether the day goes before or after the month.)
    /// </summary>
    public string PeriodLabel(DateTime occurrence, CultureInfo culture)
    {
        switch (FrequencyValue)
        {
            case Frequency.Daily or Frequency.Weekdays or Frequency.Weekly:
                var dayFirst = culture.DateTimeFormat.MonthDayPattern.TrimStart().StartsWith('d');
                return occurrence.ToString(dayFirst ? "ddd d MMM" : "ddd, MMM d", culture);
            case Frequency.Monthly:
                return occurrence.ToString(culture.DateTimeFormat.YearMonthPattern, culture);
            case Frequency.Quarterly:
                return $"Q{(occurrence.Month - 1) / 3 + 1} {occurrence.Year}";
            default:
                return $"{occurrence.Year}";
        }
    }

    /// <summary>A sentence for the template's header, such as "Repeats weekly on Mon and Wed".</summary>
    public string Summary(CultureInfo culture)
    {
        switch (FrequencyValue)
        {
            case Frequency.Daily: return "Repeats every day";
            case Frequency.Weekdays: return "Repeats every weekday";
            case Frequency.Weekly:
                var symbols = culture.DateTimeFormat.AbbreviatedDayNames;
                var first = (int)culture.DateTimeFormat.FirstDayOfWeek + 1;
                var order = Enumerable.Range(0, 7).Select(k => (first - 1 + k) % 7 + 1);
                var names = order.Where(EffectiveWeekdays.Contains).Select(d => symbols[d - 1]).ToList();
                return "Repeats weekly on " + JoinList(names, culture);
            case Frequency.Monthly:
                return "Repeats monthly on the " + (DayOfMonth == 0 ? "last day" : Ordinal(DayOfMonth));
            case Frequency.Quarterly:
                return "Repeats every quarter";
            default:
                var monthName = culture.DateTimeFormat.MonthNames[Math.Min(Math.Max(Month, 1), 12) - 1];
                return $"Repeats yearly on {(Day == 0 ? "the last day of" : $"{Day}")} {monthName}";
        }
    }

    private HashSet<int> EffectiveWeekdays
    {
        get
        {
            var days = WeekdaysValue.Where(d => d is >= 1 and <= 7).ToHashSet();
            return days.Count == 0 ? [2] : days;
        }
    }

    /// <summary>Swift <c>Calendar</c>'s weekday: 1 = Sunday … 7 = Saturday.</summary>
    public static int WeekdayNumber(DateTime day) => (int)day.DayOfWeek + 1;

    /// <summary>The date for <paramref name="day"/> of a month, clamped to the month's length; 0 is the last day.</summary>
    private static DateTime DateFor(int year, int month, int day)
    {
        month = Math.Min(Math.Max(month, 1), 12);
        var length = DateTime.DaysInMonth(year, month);
        var clamped = day <= 0 ? length : Math.Min(day, length);
        return new DateTime(year, month, clamped);
    }

    private static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{n}{suffix}";
    }

    /// <summary>"Mon", "Mon and Wed", "Mon, Wed and Fri" (US English adds the serial comma), as <c>ListFormatter</c>.</summary>
    private static string JoinList(List<string> names, CultureInfo culture)
    {
        if (names.Count <= 1) return names.FirstOrDefault() ?? "";
        if (names.Count == 2) return $"{names[0]} and {names[1]}";
        var serial = culture.Name == "en-US" ? "," : "";
        return string.Join(", ", names.Take(names.Count - 1)) + $"{serial} and {names[^1]}";
    }

    // MARK: - File marker

    private static readonly string[] WeekdayCodes = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>The marker's words: <c>repeat=&lt;frequency&gt;[:&lt;options&gt;]</c>, then <c>carry-over</c> if set.</summary>
    internal List<string> MarkerWords
    {
        get
        {
            var value = FrequencyValue.ToString().ToLowerInvariant();
            switch (FrequencyValue)
            {
                case Frequency.Weekly:
                    value += ":" + string.Join(",", EffectiveWeekdays.Order().Select(d => WeekdayCodes[d - 1]));
                    break;
                case Frequency.Monthly:
                    value += ":" + (DayOfMonth == 0 ? "last" : $"{DayOfMonth}");
                    break;
                case Frequency.Yearly:
                    value += $":{Month:D2}-" + (Day == 0 ? "last" : $"{Day:D2}");
                    break;
            }
            var words = new List<string> { "repeat=" + value };
            if (CarriesOver) words.Add("carry-over");
            return words;
        }
    }

    /// <summary>Reads a schedule from a marker's words; null if there's no valid <c>repeat=</c>.</summary>
    internal static RepeatSchedule? FromMarkerWords(IReadOnlySet<string> words)
    {
        var word = words.FirstOrDefault(w => w.StartsWith("repeat=", StringComparison.Ordinal));
        if (word == null) return null;
        var value = word["repeat=".Length..];
        var parts = value.Split(':', 2);
        var name = parts[0];
        if (name.Length == 0 || name != name.ToLowerInvariant()
            || !Enum.TryParse<Frequency>(name, ignoreCase: true, out var frequency)
            || !Enum.IsDefined(frequency) || char.IsDigit(name[0]))
            return null;
        var schedule = new RepeatSchedule(frequency, carriesOver: words.Contains("carry-over"));
        var options = parts.Length > 1 ? parts[1] : "";
        static int? DayOf(string text) =>
            text == "last" ? 0 : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 31 ? d : null;
        switch (frequency)
        {
            case Frequency.Weekly:
                var days = options.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(code => Array.IndexOf(WeekdayCodes, code))
                    .Where(i => i >= 0)
                    .Select(i => i + 1)
                    .ToHashSet();
                if (days.Count > 0) schedule.WeekdaysValue = days;
                break;
            case Frequency.Monthly:
                if (DayOf(options) is int dm) schedule.DayOfMonth = dm;
                break;
            case Frequency.Yearly:
                var md = options.Split('-', StringSplitOptions.RemoveEmptyEntries);
                if (md.Length == 2
                    && int.TryParse(md[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= 1 and <= 12
                    && DayOf(md[1]) is int d2)
                {
                    schedule.Month = m;
                    schedule.Day = d2;
                }
                break;
        }
        return schedule;
    }
}
