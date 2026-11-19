namespace Listful;

/// <summary>Longest-common-subsequence matching between two sequences.</summary>
internal static class Diff
{
    /// <summary>
    /// Index pairs <c>(i, j)</c> with <c>a[i] == b[j]</c>, in increasing order, forming
    /// a longest common subsequence. A shared prefix and suffix are matched
    /// directly, so the quadratic part only covers the region that changed.
    /// </summary>
    public static List<(int, int)> Matches<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        var eq = EqualityComparer<T>.Default;
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && eq.Equals(a[prefix], b[prefix])) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix
               && eq.Equals(a[a.Count - 1 - suffix], b[b.Count - 1 - suffix])) suffix++;

        var n = a.Count - prefix - suffix;
        var m = b.Count - prefix - suffix;
        // lengths[i * (m + 1) + j]: LCS length of the middles from i and j on.
        var lengths = new int[(n + 1) * (m + 1)];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lengths[i * (m + 1) + j] = eq.Equals(a[prefix + i], b[prefix + j])
                    ? lengths[(i + 1) * (m + 1) + j + 1] + 1
                    : Math.Max(lengths[(i + 1) * (m + 1) + j], lengths[i * (m + 1) + j + 1]);

        var result = new List<(int, int)>();
        for (var k = 0; k < prefix; k++) result.Add((k, k));
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (eq.Equals(a[prefix + x], b[prefix + y]))
            {
                result.Add((prefix + x, prefix + y));
                x++;
                y++;
            }
            else if (lengths[(x + 1) * (m + 1) + y] >= lengths[x * (m + 1) + y + 1]) x++;
            else y++;
        }
        for (var k = 0; k < suffix; k++) result.Add((a.Count - suffix + k, b.Count - suffix + k));
        return result;
    }
}

/// <summary>
/// Three-way merge of text by lines, for reconciling a list edited here with
/// the same file changed elsewhere (another computer, via a synced folder) since it was read.
/// </summary>
public static class TextMerge
{
    public abstract record Result
    {
        public sealed record Merged(string Text) : Result;
        /// <summary>Both sides changed the same lines differently.</summary>
        public sealed record Conflict : Result;
    }

    /// <summary>
    /// Combines the changes <paramref name="mine"/> and <paramref name="theirs"/> each made to <paramref name="base"/>. Changes
    /// to different lines are both kept; lines inserted at the same place by
    /// both go in theirs-then-mine. Different changes to the same lines are
    /// a conflict.
    /// </summary>
    public static Result Merge(string @base, string mine, string theirs)
    {
        if (mine == theirs || @base == theirs) return new Result.Merged(mine);
        if (@base == mine) return new Result.Merged(theirs);

        var baseLines = Lines(@base);
        var hunks = Changes(baseLines, Lines(mine), isMine: true)
            .Concat(Changes(baseLines, Lines(theirs), isMine: false))
            .OrderBy(h => h.Start).ThenBy(h => h.End)
            .ToList();

        var output = new List<string>();
        var position = 0;
        var next = 0;
        while (next < hunks.Count)
        {
            // A group is a run of hunks touching overlapping base lines,
            // or several insertions at the same point.
            var group = new List<Hunk> { hunks[next] };
            var low = hunks[next].Start;
            var high = hunks[next].End;
            next++;
            while (next < hunks.Count)
            {
                var hunk = hunks[next];
                var isOverlap = hunk.Start < high;
                var isSameInsertion = low == high && hunk.Start == low && hunk.End == low;
                if (!(isOverlap || isSameInsertion)) break;
                group.Add(hunk);
                high = Math.Max(high, hunk.End);
                next++;
            }

            var mineVersion = Apply(group.Where(h => h.IsMine), baseLines, low, high);
            var theirsVersion = Apply(group.Where(h => !h.IsMine), baseLines, low, high);
            output.AddRange(baseLines.Skip(position).Take(low - position));
            if (!group.Any(h => h.IsMine) || mineVersion.SequenceEqual(theirsVersion))
                output.AddRange(theirsVersion);
            else if (!group.Any(h => !h.IsMine))
                output.AddRange(mineVersion);
            else if (low == high)
            {
                output.AddRange(theirsVersion);
                output.AddRange(mineVersion);
            }
            else
                return new Result.Conflict();
            position = high;
        }
        output.AddRange(baseLines.Skip(position));
        return new Result.Merged(string.Join("\n", output));
    }

    /// <summary>A replacement of base lines <c>Start..&lt;End</c> (empty for an insertion).</summary>
    private sealed record Hunk(int Start, int End, List<string> Lines, bool IsMine);

    private static List<string> Lines(string text) => [.. text.Split('\n')];

    private static List<Hunk> Changes(List<string> @base, List<string> other, bool isMine)
    {
        var hunks = new List<Hunk>();
        int b = 0, o = 0;
        var pairs = Diff.Matches(@base, other);
        pairs.Add((@base.Count, other.Count));
        foreach (var (bi, oi) in pairs)
        {
            if (bi > b || oi > o)
                hunks.Add(new Hunk(b, bi, other.GetRange(o, oi - o), isMine));
            b = bi + 1;
            o = oi + 1;
        }
        return hunks;
    }

    /// <summary>Base lines <c>low..&lt;high</c> with one side's hunks (all within that range) applied.</summary>
    private static List<string> Apply(IEnumerable<Hunk> hunks, List<string> @base, int low, int high)
    {
        var result = new List<string>();
        var position = low;
        foreach (var hunk in hunks)
        {
            result.AddRange(@base.GetRange(position, hunk.Start - position));
            result.AddRange(hunk.Lines);
            position = hunk.End;
        }
        result.AddRange(@base.GetRange(position, high - position));
        return result;
    }
}
