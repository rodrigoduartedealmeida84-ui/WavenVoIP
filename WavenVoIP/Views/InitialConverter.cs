using System;
using System.Globalization;
using System.Windows.Data;

namespace WavenVoIP.Views
{
    public class InitialConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string;
            if (string.IsNullOrWhiteSpace(s)) return "?";
            var parts = s.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";
            return char.ToUpper(parts[0][0]).ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotImplementedException();
    }
}
