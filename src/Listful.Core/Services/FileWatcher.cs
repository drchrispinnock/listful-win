namespace Listful;

/// <summary>
/// Calls back when a file, or the contents of a folder, change on disk. Upstream watches with
/// dispatch sources; this uses <see cref="FileSystemWatcher"/>. Bursts of events (a save is several)
/// are coalesced, and the callback runs on the synchronization context it was created on.
/// </summary>
public sealed class FileWatcher : IDisposable
{
    private readonly FileSystemWatcher? watcher;
    private readonly SynchronizationContext? context;
    private readonly Action onChange;
    private readonly Timer timer;
    private int disposed;

    /// <summary>Watches <paramref name="path"/>: a folder's direct contents, or a single file (through its folder,
    /// since an atomic save replaces the file).</summary>
    public FileWatcher(string path, Action onChange, bool isFolder = false, int delayMilliseconds = 150)
    {
        this.onChange = onChange;
        context = SynchronizationContext.Current;
        timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
        var folder = isFolder ? path : Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder == null || !Directory.Exists(folder)) return;
        try
        {
            watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            if (!isFolder) watcher.Filter = Path.GetFileName(path);
            FileSystemEventHandler changed = (_, _) => timer.Change(delayMilliseconds, Timeout.Infinite);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => timer.Change(delayMilliseconds, Timeout.Infinite);
            watcher.Error += (_, _) => timer.Change(delayMilliseconds, Timeout.Infinite);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            watcher = null;
        }
    }

    private void Fire()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (context != null)
            context.Post(_ => { if (Volatile.Read(ref disposed) == 0) onChange(); }, null);
        else
            onChange();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        watcher?.Dispose();
        timer.Dispose();
    }
}
