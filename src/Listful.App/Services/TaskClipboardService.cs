using Windows.ApplicationModel.DataTransfer;

namespace Listful.App.Services;

/// <summary>
/// The clipboard (upstream's NSPasteboard): plain text for other apps, and Listful's own Markdown
/// under <see cref="TaskClipboard.TypeIdentifier"/>, which keeps done, decrufted and collapsed.
/// </summary>
internal static class TaskClipboardService
{
    public static void Write(string plainText, string markdown)
    {
        var package = new DataPackage();
        package.SetText(plainText);
        package.SetData(TaskClipboard.TypeIdentifier, markdown);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    /// <summary>The clipboard's text, and Listful's Markdown if it is what Listful copied.</summary>
    public static async Task<(string? Text, string? Markdown)> Read()
    {
        try
        {
            var content = Clipboard.GetContent();
            string? text = content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
            string? markdown = null;
            if (content.Contains(TaskClipboard.TypeIdentifier) && await content.GetDataAsync(TaskClipboard.TypeIdentifier) is string data)
                markdown = data;
            return (text, markdown);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }
}
