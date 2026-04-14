using Windows.Storage.Pickers;

namespace PowerLink.App.Services;

public static class PickerService
{
    public static async Task<string?> PickFolderAsync()
    {
        var hwnd = GetMainWindowHandle();
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public static async Task<string?> PickFileAsync()
    {
        var hwnd = GetMainWindowHandle();
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.Desktop;
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static IntPtr GetMainWindowHandle()
    {
        var window = App.MainAppWindow ?? throw new InvalidOperationException("Main window not initialized.");
        return WinRT.Interop.WindowNative.GetWindowHandle(window);
    }
}
