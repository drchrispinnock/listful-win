using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Listful.Tests;

internal static class TrashForTests
{
    /// <summary>Tests delete instead of filling the Recycle Bin.</summary>
    [ModuleInitializer]
    internal static void Install() => Trash.MoveToTrash = path =>
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else File.Delete(path);
    };
}

public sealed class TaskLibraryTests : IDisposable
{
    private readonly TempFolder folder = new();
    private TaskLibrary Library => new(folder.Path);

    public void Dispose() => folder.Dispose();

    [Fact]
    public void ListsOnlyMarkdownFilesSortedLikeExplorer()
    {
        foreach (var name in new[] { "b.md", "a 10.md", "a 2.md", "notes.txt", ".hidden.md", "Upper.MD" })
            File.WriteAllText(folder.File(name), "");
        Directory.CreateDirectory(folder.File("dir.md"));
        Assert.Equal(["a 2", "a 10", "b", "Upper"], Library.Lists().Select(TaskLibrary.DisplayName));
    }

    [Fact]
    public void ListPathsMatchCreatedPaths()
    {
        var created = Library.CreateList("Groceries");
        Assert.Equal([created], Library.Lists());
    }

    [Fact]
    public void ContainsMatchesFolderFilesThroughLinks()
    {
        var link = Path.Combine(Path.GetTempPath(), "TaskLibraryLink-" + Guid.NewGuid().ToString("N"));
        MakeDirectoryLink(link, folder.Path);
        try
        {
            Assert.True(Library.Contains(folder.File("a.md")));
            Assert.True(Library.Contains(Path.Combine(link, "a.md")));
            Assert.Equal(Library.PathForName("a"), Library.CanonicalPath(Path.Combine(link, "a.md")));
            Assert.False(Library.Contains(folder.File("a.txt")));
            Assert.False(Library.Contains(Path.Combine(folder.Path, "sub", "a.md")));
            Assert.False(Library.Contains(Path.Combine(Path.GetTempPath(), "a.md")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>A symbolic link if Windows allows one (Developer Mode), else a junction.</summary>
    private static void MakeDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (IOException)
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            })!;
            process.WaitForExit();
        }
        catch (UnauthorizedAccessException)
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            })!;
            process.WaitForExit();
        }
    }

    [Fact]
    public void CreateValidatesAndRejectsDuplicates()
    {
        var path = Library.CreateList("  Work.md ");
        Assert.Equal("Work.md", Path.GetFileName(path));
        Assert.Equal("", File.ReadAllText(path));

        var clash = Assert.Throws<TaskLibraryException.AlreadyExists>(() => Library.CreateList("Work"));
        Assert.Equal("Work", clash.Name);
        // Windows adds its own forbidden characters, trailing dots and device names.
        foreach (var bad in new[] { "", "   ", ".secret", "a/b", "a:b", "a\\b", "a?b", "a*b", "trailing.", "CON", "nul.txt" })
            Assert.Throws<TaskLibraryException.InvalidName>(() => Library.CreateList(bad));
    }

    [Fact]
    public void SuggestedNameSkipsTakenNames()
    {
        Assert.Equal("Untitled", Library.SuggestedNewName());
        Library.CreateList("Untitled");
        Library.CreateList("Untitled 2");
        Assert.Equal("Untitled 3", Library.SuggestedNewName());
    }

    [Fact]
    public void RenameMovesFileAndRejectsClashes()
    {
        var home = Library.CreateList("Home");
        Library.CreateList("Work");

        Assert.Equal("Work", Assert.Throws<TaskLibraryException.AlreadyExists>(() => Library.RenameList(home, "Work")).Name);

        var renamed = Library.RenameList(home, "House");
        Assert.Equal(["House", "Work"], Library.Lists().Select(TaskLibrary.DisplayName));

        var recased = Library.RenameList(renamed, "house");
        Assert.Equal(["house", "Work"], Library.Lists().Select(TaskLibrary.DisplayName));
        Assert.Equal("house.md", Path.GetFileName(recased));
    }
}

public sealed class SubfolderTests : IDisposable
{
    private readonly TempFolder folder = new();
    private TaskLibrary Library => new(folder.Path);

    public void Dispose() => folder.Dispose();

    [Fact]
    public void SubfoldersSkipTemplatesAndHiddenFolders()
    {
        foreach (var name in new[] { "Work 10", "Work 2", "Templates", ".git", "Hidden" })
            Directory.CreateDirectory(folder.File(name));
        File.SetAttributes(folder.File("Hidden"), FileAttributes.Directory | FileAttributes.Hidden);
        Library.CreateList("List");
        Assert.Equal(["Work 2", "Work 10"], Library.Subfolders().Select(Path.GetFileName));
    }

    [Fact]
    public void SubfolderPathsAreStableAndListTheirFiles()
    {
        var work = Library.CreateFolder("Work");
        Assert.Equal([work], Library.Subfolders());
        var inside = new TaskLibrary(work);
        var list = inside.CreateList("Plan");
        Assert.Equal([list], inside.Lists());
        Assert.Equal([list], new TaskLibrary(Library.Subfolders()[0]).Lists());
        Assert.Empty(Library.Lists());
    }

    [Fact]
    public void CreateAndRenameRejectClashes()
    {
        var work = Library.CreateFolder("Work");
        Assert.Throws<TaskLibraryException.AlreadyExists>(() => Library.CreateFolder("Work"));
        Assert.Throws<TaskLibraryException.InvalidName>(() => Library.CreateFolder("a/b"));
        Library.CreateFolder("Home");
        Assert.Throws<TaskLibraryException.AlreadyExists>(() => Library.RenameFolder(work, "Home"));
        Assert.Throws<TaskLibraryException.AlreadyExists>(() => Library.RenameFolder(work, "Templates"));

        new TaskLibrary(work).CreateList("Plan");
        var renamed = Library.RenameFolder(work, "Office");
        Assert.Equal(["Home", "Office"], Library.Subfolders().Select(Path.GetFileName));
        Assert.Equal(["Plan.md"], new TaskLibrary(renamed).Lists().Select(Path.GetFileName));
        var caseOnly = Library.RenameFolder(renamed, "office");
        Assert.Equal("office", Path.GetFileName(caseOnly));
        Assert.Equal(["Home", "office"], Library.Subfolders().Select(Path.GetFileName));
    }

    [Fact]
    public void FolderNamesAreTakenForNewLists()
    {
        Library.CreateFolder("Untitled");
        Assert.Equal("Untitled 2", Library.SuggestedNewName());
    }

    [Fact]
    public void TrashingAFolderRemovesIt()
    {
        var work = Library.CreateFolder("Work");
        new TaskLibrary(work).CreateList("Plan");
        Library.TrashFolder(work);
        Assert.Empty(Library.Subfolders());
    }
}
