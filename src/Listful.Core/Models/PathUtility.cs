using System.Runtime.InteropServices;

namespace Listful;

/// <summary>Path helpers standing in for Foundation's URL methods. Paths are strings here.</summary>
public static class PathUtility
{
    /// <summary>Windows paths are case-insensitive.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>A full path without a trailing separator (except a drive root).</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>Whether two paths name the same place, ignoring case and trailing separators.</summary>
    public static bool SamePath(string? a, string? b) =>
        a != null && b != null && Comparer.Equals(Normalize(a), Normalize(b));

    /// <summary>
    /// The path with any symbolic link or junction among its folders followed, as
    /// <c>resolvingSymlinksInPath()</c> does. Parts that don't exist are kept as they are.
    /// </summary>
    public static string RealPath(string path)
    {
        var full = Normalize(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Normalize(target.FullName);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return current;
    }

    /// <summary>The file name without its extension (Swift's <c>deletingPathExtension().lastPathComponent</c>).</summary>
    public static string DisplayName(string path) => Path.GetFileNameWithoutExtension(TrimSeparator(path));

    public static string LastPathComponent(string path) => Path.GetFileName(TrimSeparator(path));

    public static string Parent(string path) => Path.GetDirectoryName(TrimSeparator(path)) ?? path;

    private static string TrimSeparator(string path) =>
        path.Length > 3 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}

/// <summary>
/// The Recycle Bin, in place of <c>FileManager.trashItem</c>. Files in OneDrive and Dropbox
/// folders go to the Recycle Bin too, and the sync clients pass the deletion on.
/// </summary>
public static class Trash
{
    /// <summary>Replaceable by tests, which can't easily inspect the Recycle Bin.</summary>
    public static Action<string> MoveToTrash { get; set; } = RecycleBin;

    public static void RecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path);
            return;
        }
        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
        };
        var result = SHFileOperationW(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
            throw new IOException($"Couldn't move “{Path.GetFileName(path)}” to the Recycle Bin (error {result}).");
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCT operation);
}
