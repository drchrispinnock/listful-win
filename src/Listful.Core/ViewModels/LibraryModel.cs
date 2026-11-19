using System.Globalization;

namespace Listful;

/// <summary>
/// The folder of task lists shown in the sidebar, the current selection, and
/// the <see cref="TaskDocument"/> for whichever list is open.
/// <para>
/// Windows differences: no iCloud. The lists folder is any folder the user chooses (on the
/// computer, or one OneDrive or Dropbox syncs); until one is chosen it is
/// <see cref="DefaultFolderPath"/>, and the app's first run offers the choice
/// (<see cref="HasChosenFolder"/>). Panels and alerts are the view's: methods here take the
/// paths and names the user chose. Paths are strings, folders without a trailing separator, so
/// sidebar entries carry their kind instead.
/// </para>
/// </summary>
public sealed class LibraryModel : IDisposable
{
    public enum NamePromptKind { NewList, NewFolder, Rename }

    /// <summary>What a name prompt is for: a new list in <c>Folder</c>, a new folder, or renaming <c>Path</c>.</summary>
    public sealed record NamePrompt(NamePromptKind Kind, string? Path = null, bool IsFolder = false);

    /// <summary>A folder of lists shown as a group in the sidebar: a subfolder of
    /// the lists folder, or a linked folder from anywhere else.</summary>
    public sealed record FolderGroup(string Path, List<string> Lists, bool IsLinked, bool IsMissing = false)
    {
        public string Name => PathUtility.LastPathComponent(Path);
        public TaskLibrary Library => new(Path);
        public bool Equals(FolderGroup? other) =>
            other is not null && Path == other.Path && IsLinked == other.IsLinked && IsMissing == other.IsMissing
            && Lists.SequenceEqual(other.Lists);
        public override int GetHashCode() => HashCode.Combine(Path, Lists.Count);
    }

    /// <summary>Extensions accepted when adding files from elsewhere.</summary>
    public static readonly HashSet<string> LinkableExtensions = new(StringComparer.OrdinalIgnoreCase) { "md", "markdown", "txt" };

    /// <summary>Where lists go until the user chooses a folder: Documents\Listful (which OneDrive may back up).</summary>
    public static string DefaultFolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Listful");

    public Preferences Preferences { get; }
    public string FolderPath { get; private set; }
    public List<string> Lists { get; private set; } = [];
    /// <summary>Repeating checklists' templates, from the folder's Templates subfolder.</summary>
    public List<string> Templates { get; private set; } = [];
    /// <summary>Task list files outside the folder, edited in place. Persisted as paths.</summary>
    public List<string> LinkedFiles { get; private set; } = [];
    /// <summary>Linked files that no longer exist at their saved path.</summary>
    public HashSet<string> MissingFiles { get; private set; } = new(PathUtility.Comparer);
    /// <summary>Subfolders of the lists folder, one level down.</summary>
    public List<FolderGroup> Subfolders { get; private set; } = [];
    /// <summary>Folders outside the lists folder (e.g. in Dropbox), shown with their
    /// lists, which are edited in place. Persisted as paths.</summary>
    public List<FolderGroup> LinkedFolders { get; private set; } = [];
    /// <summary>Sidebar folder groups the user has folded away.</summary>
    public HashSet<string> CollapsedFolders { get; } = new(PathUtility.Comparer);
    /// <summary><c>#</c> titles read from list files that have one.</summary>
    public Dictionary<string, string> Titles { get; private set; } = new(PathUtility.Comparer);
    /// <summary>Lists with nothing left to do; earlier repeating copies like these are tucked away in the sidebar.</summary>
    public HashSet<string> FinishedLists { get; private set; } = new(PathUtility.Comparer);
    /// <summary>Sidebar disclosure state for repeating checklists: templates folded
    /// away, and templates whose finished earlier copies are shown.</summary>
    public HashSet<string> CollapsedTemplates { get; } = new(PathUtility.Comparer);
    public HashSet<string> ExpandedEarlier { get; } = new(PathUtility.Comparer);

    public TaskDocument? Document { get; private set; }

    private string? selection;
    public string? Selection
    {
        get => selection;
        set
        {
            if (selection == value) return;
            var oldValue = selection;
            selection = value;
            OpenSelection();
            RaiseChanged();
            // Leaving a template is when a newly written one first gets copied.
            if (oldValue != null && Templates.Contains(oldValue, PathUtility.Comparer)) _ = MakeDueCopies();
        }
    }

    private string? errorMessage;
    public string? ErrorMessage
    {
        get => errorMessage;
        set { errorMessage = value; RaiseChanged(); }
    }

    /// <summary>Raised after anything the sidebar shows, or the open document, has changed.</summary>
    public event Action? Changed;
    /// <summary>Raised when a different document is opened (or none).</summary>
    public event Action? DocumentChanged;

    private TaskLibrary library;
    private TaskLibrary templateLibrary;
    private readonly List<FileWatcher> folderWatchers = [];
    private List<string> watchedFolders = [];
    private List<string> linkedFolderPaths;
    private CancellationTokenSource? titlesTask;
    private bool isMakingCopies;
    private bool needsCopiesAgain;
    private readonly bool watch;

    /// <summary>The culture for copies' titles; the current one unless a test sets it.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;
    /// <summary>The time, for making copies; tests set it.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;

    public LibraryModel(Preferences preferences, bool watch = true)
    {
        Preferences = preferences;
        this.watch = watch;
        FolderPath = PathUtility.Normalize(preferences.GetString(Preferences.FolderKey) ?? DefaultFolderPath);
        library = new TaskLibrary(FolderPath);
        templateLibrary = new TaskLibrary(library.TemplatesFolderPath);
        LinkedFiles = preferences.GetStrings(Preferences.LinkedFilesKey);
        linkedFolderPaths = preferences.GetStrings(Preferences.LinkedFoldersKey);
        LoadFolder(preferences.GetString(Preferences.SelectedListKey));
    }

    public void Dispose()
    {
        Document?.SaveNow();
        Document?.Dispose();
        foreach (var w in folderWatchers) w.Dispose();
        folderWatchers.Clear();
    }

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>Whether the user has picked the lists folder (or it was set before), rather than the default.</summary>
    public bool HasChosenFolder => Preferences.GetString(Preferences.FolderKey) != null;

    // MARK: - Folder

    /// <summary>Uses <paramref name="path"/> as the lists folder.</summary>
    public void SwitchFolder(string path, string? restoring = null)
    {
        CloseDocument();
        FolderPath = PathUtility.Normalize(path);
        library = new TaskLibrary(FolderPath);
        templateLibrary = new TaskLibrary(library.TemplatesFolderPath);
        Preferences.Set(Preferences.FolderKey, FolderPath);
        selection = null;
        LoadFolder(restoring);
        RaiseChanged();
    }

    private void LoadFolder(string? selectedName)
    {
        // The default folder is only made once the user has settled on it (the app asks on first run).
        try { if (HasChosenFolder) library.EnsureFolderExists(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errorMessage = $"Couldn't create {FolderPath}: {e.Message}";
        }
        // A linked folder that's now the lists folder or inside it is shown there instead.
        SetLinkedFolders(linkedFolderPaths);
        RefreshLists();
        // The saved selection is a path relative to the folder for lists inside it, else a full path.
        var restored = selectedName == null ? null
            : Path.IsPathRooted(selectedName) ? selectedName : Path.Combine(FolderPath, selectedName);
        Selection = AllLists.FirstOrDefault(p => PathUtility.SamePath(p, restored)) ?? Lists.FirstOrDefault();
        _ = MakeDueCopies();
    }

    /// <summary>Every list in the sidebar: the folder's, templates, those in folder groups, and linked files.</summary>
    public List<string> AllLists =>
        [.. Lists, .. Templates, .. Subfolders.Concat(LinkedFolders).SelectMany(g => g.Lists), .. LinkedFiles];

    /// <summary>The folders whose lists are shown, each as the <see cref="TaskLibrary"/> its lists' paths come from.</summary>
    private List<TaskLibrary> Libraries =>
        [library, templateLibrary, .. Subfolders.Concat(LinkedFolders).Select(g => g.Library)];

    private static List<string> TryList(Func<List<string>> body)
    {
        try { return body(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Re-reads the folder and re-checks linked files, keeping the selection if its file still exists.</summary>
    public void RefreshLists()
    {
        var changed = false;
        var current = TryList(library.Lists);
        if (!current.SequenceEqual(Lists)) { Lists = current; changed = true; }
        var currentTemplates = Directory.Exists(templateLibrary.FolderPath) ? TryList(templateLibrary.Lists) : [];
        if (!currentTemplates.SequenceEqual(Templates)) { Templates = currentTemplates; changed = true; }
        var currentSubfolders = TryList(library.Subfolders)
            .Select(p => new FolderGroup(p, TryList(new TaskLibrary(p).Lists), false)).ToList();
        if (!currentSubfolders.SequenceEqual(Subfolders)) { Subfolders = currentSubfolders; changed = true; }
        var currentLinkedFolders = linkedFolderPaths.Select(p =>
        {
            var exists = Directory.Exists(p);
            return new FolderGroup(p, exists ? TryList(new TaskLibrary(p).Lists) : [], true, !exists);
        }).ToList();
        if (!currentLinkedFolders.SequenceEqual(LinkedFolders)) { LinkedFolders = currentLinkedFolders; changed = true; }
        var missing = LinkedFiles.Where(p => !TaskLibrary.FileExists(p)).ToHashSet(PathUtility.Comparer);
        if (!missing.SetEquals(MissingFiles)) { MissingFiles = missing; changed = true; }
        WatchFolders();
        RefreshTitles();

        if (selection is string selected)
        {
            if (missing.Contains(selected))
            {
                // Keep it selected so the detail pane can say what happened, but
                // don't save: that would recreate the file where it used to be.
                if (Document != null) { DropDocument(saving: false); changed = true; }
            }
            else if (!AllLists.Contains(selected, PathUtility.Comparer))
            {
                // Saving won't recreate it; unsaved edits are kept in a copy.
                DropDocument(saving: true);
                Selection = null;
            }
            else if (Document == null)
            {
                // A missing linked file has come back.
                OpenSelection();
                changed = true;
            }
        }
        if (changed) RaiseChanged();
    }

    /// <summary>
    /// Refreshes the sidebar when files are added, removed or renamed by
    /// anything, including File Explorer, in the folder, its Templates and other
    /// subfolders, or a linked folder. Re-watches only when that set changes.
    /// </summary>
    private void WatchFolders()
    {
        if (!watch) return;
        var folders = new List<string> { FolderPath, templateLibrary.FolderPath };
        folders.AddRange(Subfolders.Concat(LinkedFolders).Where(g => !g.IsMissing).Select(g => g.Path));
        // Templates may not exist yet; watch it once it does.
        folders = folders.Where(Directory.Exists).ToList();
        if (folders.SequenceEqual(watchedFolders, PathUtility.Comparer)) return;
        watchedFolders = folders;
        foreach (var w in folderWatchers) w.Dispose();
        folderWatchers.Clear();
        folderWatchers.AddRange(folders.Select(f => new FileWatcher(f, RefreshLists, isFolder: true)));
    }

    /// <summary>
    /// Re-reads each list's <c>#</c> title, and which lists are finished. Lists
    /// are small, but a read can stall while a sync client fetches the
    /// file, so it's done off the UI thread; a newer refresh supersedes one still running.
    /// </summary>
    private void RefreshTitles()
    {
        var paths = AllLists.Where(p => !MissingFiles.Contains(p)).ToList();
        titlesTask?.Cancel();
        var cancel = new CancellationTokenSource();
        titlesTask = cancel;
        _ = RefreshTitlesAsync(paths, cancel.Token);
    }

    private async Task RefreshTitlesAsync(List<string> paths, CancellationToken token)
    {
        var (current, finished) = await Task.Run(() => ReadTitles(paths, token));
        if (token.IsCancellationRequested) return;
        var changed = false;
        if (!SameTitles(current, Titles)) { Titles = current; changed = true; }
        if (!finished.SetEquals(FinishedLists)) { FinishedLists = finished; changed = true; }
        if (changed) RaiseChanged();
    }

    private static bool SameTitles(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    internal static (Dictionary<string, string>, HashSet<string>) ReadTitles(List<string> paths, CancellationToken token = default)
    {
        var titles = new Dictionary<string, string>(PathUtility.Comparer);
        var finished = new HashSet<string>(PathUtility.Comparer);
        foreach (var path in paths)
        {
            if (token.IsCancellationRequested) break;
            string? text;
            try
            {
                // Don't make OneDrive download every online-only list just for its title.
                var store = new MarkdownFileStore(path);
                if (store.NeedsDownload) continue;
                text = store.ReadText();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (text == null) continue;
            var outline = MarkdownCodec.Decode(text);
            if (!RepeatingChecklists.HasOpenTasks(outline)) finished.Add(path);
            if (outline.Title?.TrimWhitespace() is { Length: > 0 } title) titles[path] = title;
        }
        return (titles, finished);
    }

    // MARK: - Sidebar groups

    /// <summary>The folder's lists that aren't copies of a repeating checklist.</summary>
    public List<string> UngroupedLists
    {
        get
        {
            var grouped = Templates.SelectMany(CopiesOf).ToHashSet(PathUtility.Comparer);
            return Lists.Where(p => !grouped.Contains(p)).ToList();
        }
    }

    /// <summary>A template's copies, newest first.</summary>
    public List<string> CopiesOf(string template) =>
        RepeatingChecklists.Copies(TaskLibrary.DisplayName(template), Lists);

    /// <summary>
    /// A template's copies split for the sidebar: the newest, earlier ones
    /// with tasks left, and earlier finished ones. The open list always
    /// counts as shown, so it doesn't vanish when its last task is ticked.
    /// </summary>
    public (List<string> Shown, List<string> Earlier) CopyGroups(string template)
    {
        var all = CopiesOf(template);
        if (all.Count == 0) return ([], []);
        var older = all.Skip(1).ToList();
        bool IsTucked(string p) => FinishedLists.Contains(p) && !PathUtility.SamePath(p, selection);
        return ([all[0], .. older.Where(p => !IsTucked(p))], older.Where(IsTucked).ToList());
    }

    /// <summary>A copy's sidebar name without its template's name, e.g. "Tue 3 Nov".</summary>
    public string CopyLabel(string path, string template)
    {
        var name = DisplayName(path);
        var prefix = DisplayName(template) + " — ";
        var label = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
        // A second copy of the same period, as a sync service names it.
        if (RepeatingChecklists.CopyKey(path, TaskLibrary.DisplayName(template)) is (_, int duplicate) && duplicate > 0)
            return $"{label} ({duplicate})";
        return label;
    }

    /// <summary>The sidebar name for a list: its <c>#</c> title (live, for the open list) or else the file name.</summary>
    public string DisplayName(string path)
    {
        if (Document != null && PathUtility.SamePath(Document.FilePath, path)) return Document.DisplayTitle;
        return Titles.TryGetValue(path, out var title) ? title : TaskLibrary.DisplayName(path);
    }

    // MARK: - Opening

    private void OpenSelection()
    {
        Document?.SaveNow();
        Document?.Dispose();
        if (selection is not string selected || MissingFiles.Contains(selected))
        {
            Document = null;
            DocumentChanged?.Invoke();
            return;
        }
        Document = new TaskDocument(selected, Preferences, watch);
        var folder = FolderPath.EndsWith(Path.DirectorySeparatorChar) ? FolderPath : FolderPath + Path.DirectorySeparatorChar;
        var saved = selected.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? selected[folder.Length..] : selected;
        Preferences.Set(Preferences.SelectedListKey, saved);
        DocumentChanged?.Invoke();
    }

    /// <summary>Saves and drops the open document, so that nothing can write to its
    /// file after it has been renamed or deleted.</summary>
    public void CloseDocument() => DropDocument(saving: true);

    private void DropDocument(bool saving)
    {
        if (Document == null) return;
        if (saving) Document.SaveNow(); else Document.DiscardChanges();
        Document.Dispose();
        Document = null;
        DocumentChanged?.Invoke();
    }

    /// <summary>Checks the open list and linked files when the window is activated, and makes copies due.</summary>
    public void Activated()
    {
        RefreshLists();
        Document?.CheckDisk();
        _ = MakeDueCopies();
    }

    // MARK: - Files from elsewhere

    public bool IsLinked(string path) => LinkedFiles.Contains(path, PathUtility.Comparer);

    /// <summary>
    /// Adds files to the sidebar and selects the last one. Files already in
    /// the folder or already linked are just selected; folders are linked as folders.
    /// Returns whether any of the paths was usable.
    /// </summary>
    public bool Link(IEnumerable<string> paths)
    {
        string? toSelect = null;
        var added = LinkedFiles.ToList();
        var linkedAFolder = false;
        foreach (var raw in paths)
        {
            if (Directory.Exists(raw))
            {
                LinkFolder(raw);
                linkedAFolder = true;
                continue;
            }
            var path = PathUtility.Normalize(raw);
            if (!LinkableExtensions.Contains(Path.GetExtension(path).TrimStart('.'))) continue;
            if (Libraries.FirstOrDefault(l => l.Contains(path)) is TaskLibrary owner)
            {
                toSelect = owner.CanonicalPath(path);
                continue;
            }
            if (!added.Contains(path, PathUtility.Comparer)) added.Add(path);
            toSelect = path;
        }
        SetLinkedFiles(added);
        RefreshLists();
        if (toSelect != null) Selection = AllLists.FirstOrDefault(p => PathUtility.SamePath(p, toSelect)) ?? toSelect;
        RaiseChanged();
        return toSelect != null || linkedAFolder;
    }

    /// <summary>Shows a folder from elsewhere in the sidebar. The lists folder, its
    /// subfolders and folders already linked are just expanded.</summary>
    public void LinkFolder(string raw)
    {
        var path = PathUtility.Normalize(raw);
        var real = PathUtility.RealPath(path);
        var mainFolder = PathUtility.RealPath(FolderPath);
        if (PathUtility.Comparer.Equals(real, mainFolder)) return;
        if (Subfolders.Concat(LinkedFolders).FirstOrDefault(g => PathUtility.Comparer.Equals(PathUtility.RealPath(g.Path), real)) is FolderGroup shown)
        {
            CollapsedFolders.Remove(shown.Path);
            RaiseChanged();
            return;
        }
        // A subfolder, already shown (Templates is shown as Repeating).
        if (PathUtility.Comparer.Equals(PathUtility.RealPath(PathUtility.Parent(path)), mainFolder)) return;
        SetLinkedFolders([.. linkedFolderPaths, path]);
        RefreshLists();
        CollapsedFolders.Remove(path);
        RaiseChanged();
    }

    public bool IsLinkedFolder(string path) => linkedFolderPaths.Contains(path, PathUtility.Comparer);

    /// <summary>Removes a linked folder from the sidebar. The folder is untouched.</summary>
    public void UnlinkFolder(string path)
    {
        var wasInside = selection != null && PathUtility.SamePath(PathUtility.Parent(selection), path);
        if (wasInside) CloseDocument();
        SetLinkedFolders(linkedFolderPaths.Where(p => !PathUtility.SamePath(p, path)).ToList());
        RefreshLists();
        if (wasInside) Selection = Lists.FirstOrDefault();
        RaiseChanged();
    }

    private void SetLinkedFolders(List<string> paths)
    {
        var mainFolder = PathUtility.RealPath(FolderPath);
        var seen = new HashSet<string>(PathUtility.Comparer);
        var kept = paths.Where(p =>
        {
            var real = PathUtility.RealPath(p);
            return !PathUtility.Comparer.Equals(real, mainFolder)
                   && !PathUtility.Comparer.Equals(PathUtility.RealPath(PathUtility.Parent(p)), mainFolder)
                   && seen.Add(real);
        }).ToList();
        var sorted = kept.OrderBy(PathUtility.LastPathComponent, Comparer<string>.Create((a, b) => a.LocalizedStandardCompare(b))).ToList();
        linkedFolderPaths = sorted;
        Preferences.Set(Preferences.LinkedFoldersKey, sorted);
        // A linked file that's in a shown folder is listed there instead.
        var shownFolders = new HashSet<string>(PathUtility.Comparer) { mainFolder };
        foreach (var p in sorted) shownFolders.Add(PathUtility.RealPath(p));
        SetLinkedFiles(LinkedFiles.Where(p => !shownFolders.Contains(PathUtility.RealPath(PathUtility.Parent(p)))).ToList());
    }

    /// <summary>Points a missing linked file's sidebar entry at its new location. Returns false if
    /// <paramref name="newPath"/> isn't a Markdown or text file.</summary>
    public bool Relocate(string missing, string newPath)
    {
        SetLinkedFiles(LinkedFiles.Where(p => !PathUtility.SamePath(p, missing)).ToList());
        if (Link([newPath])) return true;
        ErrorMessage = $"“{Path.GetFileName(newPath)}” isn't a Markdown or text file.";
        return false;
    }

    /// <summary>Removes a linked file from the sidebar. The file itself is untouched.</summary>
    public void Unlink(string path)
    {
        var wasSelected = PathUtility.SamePath(selection, path);
        if (wasSelected) CloseDocument();
        SetLinkedFiles(LinkedFiles.Where(p => !PathUtility.SamePath(p, path)).ToList());
        RefreshLists();
        if (wasSelected) Selection = Lists.FirstOrDefault() ?? LinkedFiles.FirstOrDefault();
        RaiseChanged();
    }

    private void SetLinkedFiles(List<string> paths)
    {
        var sorted = paths.OrderBy(TaskLibrary.DisplayName, Comparer<string>.Create((a, b) => a.LocalizedStandardCompare(b))).ToList();
        if (!sorted.SequenceEqual(LinkedFiles)) LinkedFiles = sorted;
        Preferences.Set(Preferences.LinkedFilesKey, sorted);
    }

    // MARK: - Creating, renaming, deleting

    /// <summary>
    /// What to ask for a new list's name: it goes in <paramref name="folder"/> if given, else in the
    /// folder group the open list is in, else the lists folder. Returns the prompt and suggested name.
    /// </summary>
    public (NamePrompt Prompt, string Suggested) PromptNewList(string? folder = null)
    {
        var target = folder ?? (selection is string s ? FolderGroupContaining(s)?.Path : null) ?? FolderPath;
        return (new NamePrompt(NamePromptKind.NewList, target), new TaskLibrary(target).SuggestedNewName());
    }

    public (NamePrompt Prompt, string Suggested) PromptNewFolder() =>
        (new NamePrompt(NamePromptKind.NewFolder), library.SuggestedNewName("New Folder"));

    public (NamePrompt Prompt, string Suggested) PromptRename(string path, bool isFolder) =>
        (new NamePrompt(NamePromptKind.Rename, path, isFolder), isFolder ? PathUtility.LastPathComponent(path) : DisplayName(path));

    /// <summary>The subfolder or linked folder whose lists include <paramref name="path"/>.</summary>
    public FolderGroup? FolderGroupContaining(string path) =>
        Subfolders.Concat(LinkedFolders).FirstOrDefault(g => g.Lists.Contains(path, PathUtility.Comparer));

    /// <summary>The name of a folder new lists can go in, for prompts; null for the lists folder.</summary>
    public string? FolderName(string path) => PathUtility.SamePath(path, FolderPath) ? null : PathUtility.LastPathComponent(path);

    /// <summary>The title for a name prompt's dialog.</summary>
    public string NamePromptTitle(NamePrompt prompt) => prompt switch
    {
        { Kind: NamePromptKind.Rename, Path: string p } when Templates.Contains(p, PathUtility.Comparer) => "Rename Repeating Checklist",
        { Kind: NamePromptKind.Rename, IsFolder: true } => "Rename Folder",
        { Kind: NamePromptKind.Rename } => "Rename Task List",
        { Kind: NamePromptKind.NewFolder } => "New Folder",
        { Kind: NamePromptKind.NewList, Path: string folder } when FolderName(folder) is string name => $"New Task List in “{name}”",
        _ => "New Task List",
    };

    /// <summary>Carries out a name prompt with the name the user typed.</summary>
    public void CommitNamePrompt(NamePrompt prompt, string name)
    {
        try
        {
            switch (prompt)
            {
                case { Kind: NamePromptKind.NewList, Path: string folder }:
                {
                    var owner = Libraries.FirstOrDefault(l => PathUtility.SamePath(l.FolderPath, folder)) ?? new TaskLibrary(folder);
                    var path = owner.CreateList(name);
                    CollapsedFolders.Remove(folder);
                    RefreshLists();
                    Selection = path;
                    Document?.FocusFirstRow();
                    break;
                }
                case { Kind: NamePromptKind.NewFolder }:
                {
                    var path = library.CreateFolder(name);
                    RefreshLists();
                    CollapsedFolders.Remove(path);
                    break;
                }
                case { Kind: NamePromptKind.Rename, Path: string path, IsFolder: true }:
                {
                    var selectedName = selection != null && PathUtility.SamePath(PathUtility.Parent(selection), path)
                        ? Path.GetFileName(selection) : null;
                    if (selectedName != null) CloseDocument();
                    var renamed = library.RenameFolder(path, name);
                    if (CollapsedFolders.Remove(path)) CollapsedFolders.Add(renamed);
                    RefreshLists();
                    if (selectedName != null)
                    {
                        Selection = null;
                        Selection = Subfolders.FirstOrDefault(g => PathUtility.SamePath(g.Path, renamed))?.Lists
                            .FirstOrDefault(p => PathUtility.Comparer.Equals(Path.GetFileName(p), selectedName));
                    }
                    break;
                }
                case { Kind: NamePromptKind.Rename, Path: string path }:
                {
                    var wasSelected = PathUtility.SamePath(selection, path);
                    if (wasSelected) CloseDocument();
                    var renamed = Owner(path).RenameList(path, name);
                    Retitle(renamed, name);
                    RefreshLists();
                    if (wasSelected)
                    {
                        // Clear first so the setter reopens even if the path is unchanged.
                        Selection = null;
                        Selection = renamed;
                    }
                    break;
                }
            }
        }
        catch (Exception e) when (e is TaskLibraryException or IOException or UnauthorizedAccessException)
        {
            errorMessage = e.Message;
            RefreshLists();
            if (selection != null && Document == null) OpenSelection();
        }
        RaiseChanged();
    }

    /// <summary>Renaming changes what the sidebar shows, so a list with a <c>#</c> title
    /// gets the new name as its title too (the file name alone wouldn't show).</summary>
    private void Retitle(string path, string name)
    {
        var store = new MarkdownFileStore(path);
        Outline outline;
        try { outline = store.Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        if (outline.Title == null) return;
        outline.Title = name.Trim();
        try { store.Save(outline); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errorMessage = $"Renamed the file, but couldn't update its title: {e.Message}";
        }
    }

    /// <summary>The question to ask before deleting a list or folder (to the Recycle Bin).</summary>
    public (string Title, string Message) TrashQuestion(string path, bool isFolder) => isFolder
        ? ($"Delete the folder “{PathUtility.LastPathComponent(path)}”?",
           "Every task list in the folder goes too. You can restore it from the Recycle Bin.")
        : ($"Delete “{DisplayName(path)}”?", "You can restore it from the Recycle Bin.");

    public void Trash(string path, bool isFolder)
    {
        if (isFolder)
        {
            TrashFolder(path);
            return;
        }
        var wasSelected = PathUtility.SamePath(selection, path);
        var neighbour = wasSelected ? NeighbourInList(path) : null;
        if (wasSelected) CloseDocument();
        try
        {
            Owner(path).TrashList(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errorMessage = $"Couldn't move {Path.GetFileName(path)} to the Recycle Bin: {e.Message}";
        }
        RefreshLists();
        if (wasSelected)
            Selection = neighbour != null && AllLists.Contains(neighbour, PathUtility.Comparer) ? neighbour : Lists.FirstOrDefault();
        RaiseChanged();
    }

    private void TrashFolder(string path)
    {
        var wasInside = selection != null && PathUtility.SamePath(PathUtility.Parent(selection), path);
        if (wasInside) CloseDocument();
        try
        {
            library.TrashFolder(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            errorMessage = $"Couldn't move {PathUtility.LastPathComponent(path)} to the Recycle Bin: {e.Message}";
        }
        RefreshLists();
        if (wasInside) Selection = Lists.FirstOrDefault();
        RaiseChanged();
    }

    private string? NeighbourInList(string path)
    {
        var siblings = Templates.Contains(path, PathUtility.Comparer) ? Templates : FolderGroupContaining(path)?.Lists ?? Lists;
        var i = siblings.FindIndex(p => PathUtility.SamePath(p, path));
        if (i < 0) return null;
        if (i + 1 < siblings.Count) return siblings[i + 1];
        return i > 0 ? siblings[i - 1] : null;
    }

    /// <summary>The folder a list belongs to, as the <see cref="TaskLibrary"/> its path came from.</summary>
    private TaskLibrary Owner(string path)
    {
        if (Templates.Contains(path, PathUtility.Comparer)) return templateLibrary;
        return FolderGroupContaining(path)?.Library ?? library;
    }

    // MARK: - Repeating checklists

    public string SuggestedRepeatingName() => templateLibrary.SuggestedNewName("Checklist");

    /// <summary>Creates the template, titled with its name, and opens it to be filled
    /// in. Its first copy is made once it has tasks and the user moves on.</summary>
    public void CreateRepeating(string rawName, RepeatSchedule schedule)
    {
        try
        {
            var name = rawName.Trim();
            templateLibrary.EnsureFolderExists();
            var path = templateLibrary.CreateList(name, new Outline(name, schedule: schedule.Copy()));
            watchedFolders = [];  // Templates may be new, so watch it afresh.
            RefreshLists();
            Selection = path;
            Document?.FocusFirstRow();
        }
        catch (Exception e) when (e is TaskLibraryException or IOException or UnauthorizedAccessException)
        {
            errorMessage = e.Message;
        }
        RaiseChanged();
    }

    /// <summary>
    /// Makes any repeating checklist copies that are due, off the UI
    /// thread, and merges away duplicate copies. If the open list was the previous copy, the new
    /// one is opened in its place. Overlapping requests run once more afterwards.
    /// </summary>
    public async Task MakeDueCopies()
    {
        if (isMakingCopies)
        {
            needsCopiesAgain = true;
            return;
        }
        isMakingCopies = true;
        // Save first, so tasks being carried over include the latest edits.
        Document?.SaveNow();
        var templates = templateLibrary;
        var lists = library;
        var open = selection;
        var date = Now();
        var culture = Culture;
        var (results, trashed) = await Task.Run(() =>
        {
            if (!Directory.Exists(templates.FolderPath)) return ([], []);
            var made = RepeatingChecklists.MakeDueCopies(templates, lists, date, culture);
            // When two computers make the same copy, a sync service may keep both, renaming one.
            var merged = RepeatingChecklists.MergeDuplicates(templates, lists, open);
            return (made, merged);
        });
        isMakingCopies = false;
        // The folder may have changed meanwhile; these results are for the old one.
        if (templates.FolderPath == templateLibrary.FolderPath)
        {
            if (results.Count > 0 || trashed.Count > 0) RefreshLists();
            if (results.Select(r => r.Error).FirstOrDefault(e => e != null) is string error) ErrorMessage = error;
            if (results.FirstOrDefault(r => r.Copy != null && r.Previous != null && PathUtility.SamePath(r.Previous, selection)) is { } moved)
            {
                CollapsedTemplates.Remove(moved.Template);
                Selection = AllLists.FirstOrDefault(p => PathUtility.SamePath(p, moved.Copy)) ?? moved.Copy;
            }
        }
        if (needsCopiesAgain)
        {
            needsCopiesAgain = false;
            await MakeDueCopies();
        }
    }
}
