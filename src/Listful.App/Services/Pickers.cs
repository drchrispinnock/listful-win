using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace Listful.App.Services;

/// <summary>File and folder pickers (upstream's NSOpenPanel), tied to the window as an unpackaged app must.</summary>
internal static class Pickers
{
    public static async Task<string?> ChooseFolder(Window owner)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<List<string>> ChooseFiles(Window owner, bool multiple)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var extension in LibraryModel.LinkableExtensions) picker.FileTypeFilter.Add("." + extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
        if (multiple)
        {
            var files = await picker.PickMultipleFilesAsync();
            return files.Select(f => f.Path).ToList();
        }
        var file = await picker.PickSingleFileAsync();
        return file == null ? [] : [file.Path];
    }
}
