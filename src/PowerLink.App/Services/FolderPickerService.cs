using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace PowerLink.App.Services;

public static class FolderPickerService
{
    public static async Task<string?> PickFolderAsync()
    {
        var window = App.MainAppWindow ?? throw new InvalidOperationException("Main window not initialized.");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }
}
