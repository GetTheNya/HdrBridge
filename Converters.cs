using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SyncLightBridge.Converters;

public class BoolToColorConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        if (value is bool b && b)
            return new SolidColorBrush(Color.FromRgb(40, 190, 80)); // Green
        return new SolidColorBrush(Color.FromRgb(220, 50, 50)); // Red
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
        throw new NotImplementedException();
    }
}
