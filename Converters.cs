using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace HdrBridge;

public class BoolToColorConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        if (value is bool b && b)
            return new SolidColorBrush(Color.FromRgb(40, 190, 80)); // Green
        return new SolidColorBrush(Color.FromRgb(220, 50, 50)); // Red
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
        return Binding.DoNothing;
    }
}

public class EnumMatchToVisibilityConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        if (value == null || parameter == null) return System.Windows.Visibility.Collapsed;
        return value.ToString() == parameter.ToString() ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
        return Binding.DoNothing;
    }
}

public class EnumMatchToBoolConverter : IValueConverter {
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
        if (value is bool isChecked && isChecked) {
            return Enum.Parse(targetType, parameter.ToString()!);
        }
        return Binding.DoNothing;
    }
}

public class RGBToColorConverter : IMultiValueConverter {
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
        try {
            if (values.Length == 3) {
                byte r = System.Convert.ToByte(values[0]);
                byte g = System.Convert.ToByte(values[1]);
                byte b = System.Convert.ToByte(values[2]);
                return Color.FromRgb(r, g, b);
            }
        } catch { }
        return Colors.Black;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) {
        throw new NotImplementedException();
    }
}
