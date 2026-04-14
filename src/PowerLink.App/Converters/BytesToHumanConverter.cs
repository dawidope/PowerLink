using Microsoft.UI.Xaml.Data;

namespace PowerLink.App.Converters;

public sealed class BytesToHumanConverter : IValueConverter
{
    private static readonly string[] Units = { "B", "KiB", "MiB", "GiB", "TiB" };

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        long bytes = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0,
        };

        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F2} {Units[unit]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
