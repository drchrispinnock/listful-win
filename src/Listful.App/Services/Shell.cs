using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Listful.App.Services;

/// <summary>Windows glue upstream gets from NSWorkspace and Core Graphics.</summary>
internal static class Shell
{
    /// <summary>Shows a file selected in File Explorer, or opens a folder (upstream's Show in Finder).</summary>
    public static void ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    /// <summary>Seconds since the last keyboard or mouse input on this computer
    /// (upstream: <c>CGEventSource.secondsSinceLastEventType</c>).</summary>
    public static double SecondsSinceInput
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    /// <summary>Where the Dropbox client keeps the user's personal Dropbox, from its <c>info.json</c>.</summary>
    public static string? DropboxFolder
    {
        get
        {
            foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                var info = Path.Combine(Environment.GetFolderPath(root), "Dropbox", "info.json");
                try
                {
                    if (!File.Exists(info)) continue;
                    using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(info));
                    foreach (var account in new[] { "personal", "business" })
                        if (json.RootElement.TryGetProperty(account, out var a) && a.TryGetProperty("path", out var p)
                            && p.GetString() is string path && Directory.Exists(path))
                            return path;
                }
                catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
            }
            return null;
        }
    }

    /// <summary>The user's OneDrive folder, if OneDrive is set up.</summary>
    public static string? OneDriveFolder
    {
        get
        {
            foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                if (Environment.GetEnvironmentVariable(name) is string path && Directory.Exists(path)) return path;
            return null;
        }
    }

    /// <summary>A path with the user's profile folder shortened to <c>~</c>, as upstream abbreviates with a tilde.</summary>
    public static string Abbreviated(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..] : path;
    }
}
