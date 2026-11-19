using System.Globalization;

namespace Listful;

public abstract class TaskLibraryException(string message) : Exception(message)
{
    public sealed class InvalidName() : TaskLibraryException(
        "A task list name can't be empty, start with a dot, end with a dot or space, or contain any of \\ / : * ? \" < > |.");

    public sealed class AlreadyExists(string name) : TaskLibraryException($"A task list called “{name}” already exists.")
    {
        public string Name { get; } = name;
    }
}

/// <summary>
/// A folder of task lists, one Markdown file per list. List names are the
/// file names without the <c>.md</c> extension.
/// <para>
/// Windows differences: paths are strings (upstream's <c>URL</c>s), compared with
/// <see cref="PathUtility.SamePath"/>; folders carry no trailing separator, so callers tell
/// lists from folders by type rather than by <c>hasDirectoryPath</c>. There are no iCloud
/// placeholders. Names follow Windows' rules as well as upstream's.
/// </para>
/// </summary>
public sealed class TaskLibrary(string folderPath)
{
    public const string FileExtension = "md";

    public string FolderPath { get; } = PathUtility.Normalize(folderPath);

    public static string DisplayName(string path) => PathUtility.DisplayName(path);

    /// <summary>
    /// Where to keep this computer's version of a list when it clashes with
    /// changes made elsewhere: beside it, as "Name (conflict 2026-10-19 14-05).md",
    /// numbered if that's taken.
    /// </summary>
    public static string ConflictCopyPath(string path, DateTime? date = null)
    {
        var stamp = (date ?? DateTime.Now).ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture);
        var @base = $"{DisplayName(path)} (conflict {stamp})";
        var folder = PathUtility.Parent(path);
        var name = @base;
        var counter = 2;
        while (FileExists(Path.Combine(folder, name + "." + FileExtension)))
        {
            name = $"{@base} {counter}";
            counter++;
        }
        return Path.Combine(folder, name + "." + FileExtension);
    }

    public string PathForName(string name) => Path.Combine(FolderPath, name + "." + FileExtension);

    /// <summary>
    /// Whether <paramref name="path"/> is a list directly inside this folder, comparing real
    /// paths so that linked spellings (a junction or symbolic link) match.
    /// </summary>
    public bool Contains(string path) =>
        PathUtility.Comparer.Equals(PathUtility.RealPath(PathUtility.Parent(path)), PathUtility.RealPath(FolderPath))
        && Path.GetExtension(path).Equals("." + FileExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The <see cref="PathForName"/> spelling of a file inside this folder, so it matches <see cref="Lists"/>.</summary>
    public string CanonicalPath(string path) => Path.Combine(FolderPath, PathUtility.LastPathComponent(path));

    /// <summary>
    /// Where repeating checklists' templates are kept: a subfolder, so they
    /// aren't listed among the lists themselves.
    /// </summary>
    public string TemplatesFolderPath => Path.Combine(FolderPath, "Templates");

    public void EnsureFolderExists() => Directory.CreateDirectory(FolderPath);

    /// <summary>
    /// The <c>.md</c> files in the folder, sorted by name the way File Explorer does.
    /// Paths are rebuilt from <see cref="FolderPath"/> so they compare equal to ones from
    /// <see cref="PathForName"/>.
    /// </summary>
    public List<string> Lists()
    {
        var names = new HashSet<string>(PathUtility.Comparer);
        foreach (var file in new DirectoryInfo(FolderPath).EnumerateFiles())
        {
            var name = file.Name;
            if (name.StartsWith('.') || file.Attributes.HasFlag(FileAttributes.Hidden)) continue;
            if (!Path.GetExtension(name).Equals("." + FileExtension, StringComparison.OrdinalIgnoreCase)) continue;
            names.Add(name);
        }
        return names
            .Select(name => Path.Combine(FolderPath, name))
            .OrderBy(path => DisplayName(path), Comparer<string>.Create((a, b) => a.LocalizedStandardCompare(b)))
            .ToList();
    }

    /// <summary>Whether a file or folder is at <paramref name="path"/>. (Upstream also counts iCloud placeholders.)</summary>
    public static bool FileExists(string path) => File.Exists(path) || Directory.Exists(path);

    // MARK: - Subfolders

    /// <summary>
    /// The folders directly inside this one that hold lists of their own,
    /// sorted like File Explorer: every visible subfolder except Templates.
    /// </summary>
    public List<string> Subfolders() =>
        new DirectoryInfo(FolderPath).EnumerateDirectories()
            .Where(d => !d.Name.StartsWith('.')
                        && !d.Attributes.HasFlag(FileAttributes.Hidden)
                        && !d.Attributes.HasFlag(FileAttributes.System)
                        && !PathUtility.Comparer.Equals(d.Name, "Templates"))
            .Select(d => FolderPathNamed(d.Name))
            .OrderBy(path => PathUtility.LastPathComponent(path), Comparer<string>.Create((a, b) => a.LocalizedStandardCompare(b)))
            .ToList();

    public string FolderPathNamed(string name) => Path.Combine(FolderPath, name);

    public string CreateFolder(string rawName)
    {
        var name = ValidatedName(rawName, keepingExtension: true);
        var path = FolderPathNamed(name);
        if (FileExists(path)) throw new TaskLibraryException.AlreadyExists(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string RenameFolder(string path, string rawName)
    {
        var name = ValidatedName(rawName, keepingExtension: true);
        if (PathUtility.Comparer.Equals(name, "Templates")) throw new TaskLibraryException.AlreadyExists(name);
        var destination = FolderPathNamed(name);
        if (destination == PathUtility.Normalize(path)) return path;
        var isCaseChange = PathUtility.SamePath(destination, path);
        if (!isCaseChange && FileExists(destination)) throw new TaskLibraryException.AlreadyExists(name);
        MoveItem(path, destination);
        return destination;
    }

    /// <summary>Moves a subfolder, and every list in it, to the Recycle Bin.</summary>
    public void TrashFolder(string path) => TrashList(path);

    /// <summary>"Untitled", or "Untitled 2", "Untitled 3"… if taken.</summary>
    public string SuggestedNewName(string @base = "Untitled")
    {
        var name = @base;
        var counter = 2;
        while (FileExists(PathForName(name)) || FileExists(FolderPathNamed(name)))
        {
            name = $"{@base} {counter}";
            counter++;
        }
        return name;
    }

    public string CreateList(string rawName, Outline? contents = null)
    {
        var name = ValidatedName(rawName);
        var path = PathForName(name);
        if (FileExists(path)) throw new TaskLibraryException.AlreadyExists(name);
        EnsureFolderExists();
        new MarkdownFileStore(path).Save(contents ?? new Outline());
        return path;
    }

    public string RenameList(string path, string rawName)
    {
        var name = ValidatedName(rawName);
        var destination = PathForName(name);
        if (destination == path) return path;
        // Allow case-only renames, which the case-insensitive filesystem
        // would otherwise report as a clash with the file itself.
        var isCaseChange = PathUtility.SamePath(destination, path);
        if (!isCaseChange && FileExists(destination)) throw new TaskLibraryException.AlreadyExists(name);
        MoveItem(path, destination);
        return destination;
    }

    public void TrashList(string path) => Trash.MoveToTrash(path);

    /// <summary>Moves a file or folder, trying again while a sync client has it open.</summary>
    internal static void MoveItem(string source, string destination)
    {
        MarkdownFileStore.Retrying<object?>(() =>
        {
            if (Directory.Exists(source)) Directory.Move(source, destination);
            else File.Move(source, destination);
            return null;
        });
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    internal static string ValidatedName(string rawName, bool keepingExtension = false)
    {
        var name = rawName.Trim();
        if (!keepingExtension && name.EndsWith("." + FileExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^(FileExtension.Length + 1)];
        if (name.Length == 0 || name.StartsWith('.') || name.EndsWith('.') || name.EndsWith(' ')
            || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0
            || name.Any(char.IsControl)
            || ReservedNames.Contains(name.Split('.')[0]))
            throw new TaskLibraryException.InvalidName();
        return name;
    }
}
