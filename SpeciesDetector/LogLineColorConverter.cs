using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SpeciesDetector
{
    /// <summary>Colors a log line by content so errors/warnings/successes are scannable at a glance.</summary>
    public class LogLineColorConverter : IValueConverter
    {
        private static readonly SolidColorBrush Error   = Freeze(0xF3, 0x8B, 0xA8);
        private static readonly SolidColorBrush Warning = Freeze(0xF9, 0xE2, 0xAF);
        private static readonly SolidColorBrush Success = Freeze(0xA6, 0xE3, 0xA1);
        private static readonly SolidColorBrush Info    = Freeze(0xCD, 0xD6, 0xF4);
        private static readonly SolidColorBrush Muted   = Freeze(0x6C, 0x70, 0x86);

        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = value as string ?? string.Empty;

            if (Contains(s, "ERROR") || Contains(s, "FAILED") || Contains(s, "server error"))
                return Error;
            if (Contains(s, "WARNING") || Contains(s, "TIMED OUT") || Contains(s, "Cooldown"))
                return Warning;
            if (Contains(s, "MATCHED") || Contains(s, "sent successfully") || Contains(s, "is ready"))
                return Success;
            if (s.StartsWith("    ") || s.StartsWith("\t")) // indented sub-detail lines
                return Muted;

            return Info;
        }

        private static bool Contains(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
