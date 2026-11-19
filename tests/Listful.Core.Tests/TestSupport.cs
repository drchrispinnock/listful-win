namespace Listful.Tests;

internal static class TestSupport
{
    /// <summary>
    /// Builds an outline from "title:depth" shorthand, e.g. ["a:0", "b:1"].
    /// Entries starting with "#" are headings: "##B" is a level-2 heading "B".
    /// </summary>
    public static Outline MakeOutline(params string[] spec) =>
        new(items: spec.Select(entry =>
        {
            if (entry.StartsWith('#'))
            {
                var level = entry.TakeWhile(c => c == '#').Count();
                return new TaskItem(entry[level..], headingLevel: level);
            }
            var parts = entry.Split(':');
            return new TaskItem(parts[0], depth: int.Parse(parts[1]));
        }));

    /// <summary>Renders an outline back to the same shorthand.</summary>
    public static string[] Shape(Outline outline) =>
        outline.Items.Select(item => item.HeadingLevel is int level
            ? new string('#', level) + item.Title
            : $"{item.Title}:{item.Depth}").ToArray();
}

/// <summary>A fresh folder under the temp directory, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ListfulTests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
