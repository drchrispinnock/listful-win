namespace Listful.App.Services;

/// <summary>Windows-only: an exception escaping a UI event ends the process with no trace, so keep it in
/// <c>%LOCALAPPDATA%\Listful\Logs\crash.log</c> (rotated at 1 MB).</summary>
internal static class CrashLog
{
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Listful", "Logs", "crash.log");

    public static void Append(Exception e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                File.Move(FilePath, FilePath + ".1", overwrite: true);
            File.AppendAllText(FilePath, $"{DateTime.Now:O} {e}\n\n");
        }
        catch (Exception) { }
    }
}
