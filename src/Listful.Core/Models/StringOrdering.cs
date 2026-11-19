using System.Globalization;

namespace Listful;

public static class StringOrdering
{
    /// <summary>
    /// Foundation's <c>localizedStandardCompare</c>, the way Finder (and File Explorer) sorts names:
    /// ignoring case, with runs of digits compared by value, so "2" comes before "10".
    /// </summary>
    public static int LocalizedStandardCompare(this string a, string b) =>
        string.Compare(a, b, CultureInfo.CurrentCulture,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.NumericOrdering);

    /// <summary>Swift's <c>trimmingCharacters(in: .whitespaces)</c>: spaces and tabs, not line breaks.</summary>
    public static string TrimWhitespace(this string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsSpace(s[start])) start++;
        while (end > start && IsSpace(s[end - 1])) end--;
        return start == 0 && end == s.Length ? s : s[start..end];
    }

    private static bool IsSpace(char c) =>
        c == '\t' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
}
