using Microsoft.UI.Xaml.Data;

namespace PowerLink.App.Converters;

public sealed class Log2BufferSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var log2 = value switch
        {
            double d => (int)d,
            int i => i,
            _ => 6,
        };
        var kib = 1 << log2;
        return kib >= 1024 ? $"{kib / 1024} MiB" : $"{kib} KiB";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
