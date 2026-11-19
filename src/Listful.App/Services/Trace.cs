namespace Listful.App.Services;

/// <summary>Debugging aid: with LISTFUL_TRACE set to a file path, lines are appended there.</summary>
internal static class Trace
{
    private static readonly string? Path = Environment.GetEnvironmentVariable("LISTFUL_TRACE");

    public static void Log(string line)
    {
        if (Path == null) return;
        try { File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {line}\n"); } catch (IOException) { }
    }
}
