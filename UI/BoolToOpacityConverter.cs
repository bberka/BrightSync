using System.Globalization;
using System.Windows.Data;

namespace BrightSync.UI;


public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? 1.0 : 0.38;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
