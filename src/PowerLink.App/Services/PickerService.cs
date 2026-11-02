using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PowerLink.App.Services;

public static class PickerService
{
    // Uses the Win32 Common Item Dialog rather than Windows.Storage.Pickers:
    // the WinRT pickers fail with E_FAIL when PowerLink runs elevated
    // (issue #4), while the classic dialog works either way.
    public static Task<string?> PickFolderAsync() => PickAsync(pickFolder: true);

    public static Task<string?> PickFileAsync() => PickAsync(pickFolder: false);

    private static async Task<string?> PickAsync(bool pickFolder)
    {
        try
        {
            return NativeFileDialog.Show(GetMainWindowHandle(), pickFolder);
        }
        catch (Exception ex)
        {
            // Safety net: if even the native dialog fails, let the user type
            // or paste the path instead of crashing the app.
            ErrorLog.Write(pickFolder ? "FolderPicker" : "FilePicker", ex,
                "falling back to path prompt");
            return await PromptForPathAsync(pickFolder);
        }
    }

    private static async Task<string?> PromptForPathAsync(bool isFolder)
    {
        var xamlRoot = App.MainAppWindow?.Content?.XamlRoot;
        if (xamlRoot is null) return null;

        var input = new TextBox
        {
            PlaceholderText = isFolder ? @"C:\path\to\folder" : @"C:\path\to\file.ext",
        };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = Application.Current.Resources["SystemFillColorCriticalBrush"] as Brush,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = isFolder ? "Enter folder path" : "Enter file path",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "The Windows file picker couldn't open. Paste the path instead — " +
                               "in Explorer, Shift+right-click the item and choose \"Copy as path\".",
                    },
                    input,
                    error,
                },
            },
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        string? accepted = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var path = PathInput.TryNormalize(input.Text);
            var exists = path is not null && (isFolder ? Directory.Exists(path) : File.Exists(path));
            if (!exists)
            {
                args.Cancel = true;
                error.Text = isFolder
                    ? "Folder not found. Enter a full path such as C:\\Data."
                    : "File not found. Enter a full path such as C:\\Data\\file.bin.";
                error.Visibility = Visibility.Visible;
                return;
            }
            accepted = path;
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? accepted : null;
    }

    private static IntPtr GetMainWindowHandle()
    {
        var window = App.MainAppWindow ?? throw new InvalidOperationException("Main window not initialized.");
        return WinRT.Interop.WindowNative.GetWindowHandle(window);
    }
}
