namespace Listful;

/// <summary>What saving a list did (see <see cref="FileSync.Save"/>).</summary>
public abstract record SaveOutcome
{
    /// <summary>Written; this text is now in the file.</summary>
    public sealed record Saved(string Text) : SaveOutcome;
    /// <summary>Merged with changes made elsewhere: this is now the file's text, and the list should show it.</summary>
    public sealed record Merged(string Text) : SaveOutcome;
    /// <summary>
    /// Changes made elsewhere clash with these. The file keeps the other
    /// version, <c>Disk</c>, which the list should show; these were written to <c>Copy</c>.
    /// </summary>
    public sealed record Conflict(string Disk, string Copy) : SaveOutcome;
    /// <summary>
    /// The file has gone (renamed or deleted, perhaps on another computer) and
    /// isn't recreated. Unsaved edits, if there were any, went to <c>Copy</c>.
    /// </summary>
    public sealed record Gone(string? Copy) : SaveOutcome;
}

/// <summary>
/// Saving a list that may also be edited elsewhere: by another app, or on
/// another computer through a synced folder.
/// </summary>
public static class FileSync
{
    /// <summary>
    /// Saves <paramref name="mine"/>, the list's text, given <paramref name="synced"/>, the file's text as last
    /// read or written here (null if it has never been). If the file has
    /// changed since, the two versions are merged line by line; if they
    /// clash, the other version wins and <paramref name="mine"/> is kept beside it as a copy.
    /// A file that was there and has gone is never recreated.
    /// </summary>
    public static SaveOutcome Save(string mine, MarkdownFileStore store, string? synced, DateTime? date = null)
    {
        if (store.ReadText() is not string disk)
        {
            if (synced == null)
            {
                store.WriteText(mine);
                return new SaveOutcome.Saved(mine);
            }
            // Writing it would bring it back under its old name.
            if (mine == synced) return new SaveOutcome.Gone(null);
            return new SaveOutcome.Gone(WriteCopy(mine, store.FilePath, date));
        }
        if (disk == synced)
        {
            store.WriteText(mine);
            return new SaveOutcome.Saved(mine);
        }
        switch (TextMerge.Merge(synced ?? "", mine, disk))
        {
            case TextMerge.Result.Merged(var text):
                if (text != disk) store.WriteText(text);
                return new SaveOutcome.Merged(text);
            default:
                return new SaveOutcome.Conflict(disk, WriteCopy(mine, store.FilePath, date));
        }
    }

    private static string WriteCopy(string text, string path, DateTime? date)
    {
        var copy = TaskLibrary.ConflictCopyPath(path, date);
        new MarkdownFileStore(copy).WriteText(text);
        return copy;
    }
}
