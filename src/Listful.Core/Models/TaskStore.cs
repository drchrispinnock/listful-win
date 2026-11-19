using System.Text;

namespace Listful;

/// <summary>
/// A place an outline is loaded from and saved to. Local Markdown is the
/// first backend; git-backed Markdown is planned.
/// </summary>
public interface ITaskStore
{
    Outline Load();
    void Save(Outline outline);
}

/// <summary>
/// Stores the outline as a Markdown file on the local filesystem, or in a folder that
/// OneDrive, Dropbox or another client syncs.
/// <para>
/// Windows differences: there is no <c>NSFileCoordinator</c>. A sync client may hold a file
/// open for a moment, so reads and writes retry briefly on a sharing violation. A OneDrive
/// file that is online-only is downloaded by Windows when it is read, which can take a
/// while (<see cref="NeedsDownload"/>), so read off the UI thread where it matters.
/// Text is written as UTF-8 without a BOM, with LF line endings, so files move between the
/// Mac, iOS and Windows apps unchanged.
/// </para>
/// </summary>
public sealed class MarkdownFileStore(string filePath) : ITaskStore
{
    public string FilePath { get; } = filePath;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>A missing file loads as an empty outline.</summary>
    public Outline Load() => ReadText() is string text ? MarkdownCodec.Decode(text) : new Outline();

    public void Save(Outline outline) => WriteText(MarkdownCodec.Encode(outline));

    /// <summary>
    /// Whether the file is in the cloud but its contents aren't on this computer (a OneDrive or
    /// Dropbox online-only file), so reading it means waiting for a download.
    /// </summary>
    public bool NeedsDownload
    {
        get
        {
            try
            {
                if (!File.Exists(FilePath)) return false;
                var attributes = (int)File.GetAttributes(FilePath);
                return (attributes & (RecallOnDataAccess | RecallOnOpen | (int)FileAttributes.Offline)) != 0;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    private const int RecallOnOpen = 0x0004_0000, RecallOnDataAccess = 0x0040_0000;

    /// <summary>
    /// The file's text, or null if it doesn't exist. Used to spot changes
    /// made elsewhere since it was last read or written.
    /// </summary>
    public string? ReadText()
    {
        return Retrying(() =>
        {
            if (!File.Exists(FilePath)) return null;
            try
            {
                var text = File.ReadAllText(FilePath, Utf8);
                return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        });
    }

    /// <summary>Writes the text atomically: to a hidden file beside it, then moved over it.</summary>
    public void WriteText(string text)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, text, Utf8);
            File.SetAttributes(temporary, File.GetAttributes(temporary) | FileAttributes.Hidden);
            Retrying<object?>(() =>
            {
                File.Move(temporary, FilePath, overwrite: true);
                return null;
            });
            var attributes = File.GetAttributes(FilePath);
            if (attributes.HasFlag(FileAttributes.Hidden)) File.SetAttributes(FilePath, attributes & ~FileAttributes.Hidden);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    /// <summary>Runs <paramref name="body"/>, trying again for about a second while another process has the file locked.</summary>
    internal static T Retrying<T>(Func<T> body)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return body(); }
            catch (IOException e) when (attempt < 10 && IsSharingViolation(e))
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 10)
            {
                // A replace over a file another process has open can surface as access denied.
                Thread.Sleep(100);
            }
        }
    }

    private static bool IsSharingViolation(IOException e) =>
        (e.HResult & 0xFFFF) is 32 or 33;  // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
}
