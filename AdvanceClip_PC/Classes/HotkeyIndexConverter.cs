using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AdvanceClip
{
    /// <summary>
    /// Converts AlternationIndex (0-9) to hotkey label: 0→"Alt+1", 1→"Alt+2", ..., 8→"Alt+9", 9→"Alt+0"
    /// </summary>
    public class HotkeyIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int index && index >= 0 && index < 10)
            {
                int display = (index + 1) % 10; // 0→1, 1→2, ..., 8→9, 9→0
                return $"Alt+{display}";
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
