using System;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace AdvanceClip.Classes
{
    /// <summary>
    /// Detects and converts color codes in clipboard text.
    /// Supports: #hex, rgb(), rgba(), hsl(), hsla()
    /// </summary>
    public static class ColorHelper
    {
        // Regex patterns for color detection
        private static readonly Regex HexPattern = new Regex(
            @"#([0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})\b", RegexOptions.Compiled);

        private static readonly Regex RgbPattern = new Regex(
            @"rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex HslPattern = new Regex(
            @"hsla?\(\s*(\d{1,3})\s*,\s*(\d{1,3})%?\s*,\s*(\d{1,3})%?\s*(?:,\s*[\d.]+\s*)?\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Try to detect a color in text. Returns true if found.
        /// </summary>
        public static bool TryDetectColor(string text, out string hexColor, out byte r, out byte g, out byte b)
        {
            hexColor = "";
            r = g = b = 0;

            if (string.IsNullOrWhiteSpace(text)) return false;

            // Try hex first
            var hexMatch = HexPattern.Match(text);
            if (hexMatch.Success)
            {
                string hex = hexMatch.Groups[1].Value;
                if (hex.Length == 3)
                {
                    r = Convert.ToByte(new string(hex[0], 2), 16);
                    g = Convert.ToByte(new string(hex[1], 2), 16);
                    b = Convert.ToByte(new string(hex[2], 2), 16);
                }
                else if (hex.Length == 6 || hex.Length == 8)
                {
                    int offset = hex.Length == 8 ? 2 : 0; // Skip alpha if 8-char
                    r = Convert.ToByte(hex.Substring(offset, 2), 16);
                    g = Convert.ToByte(hex.Substring(offset + 2, 2), 16);
                    b = Convert.ToByte(hex.Substring(offset + 4, 2), 16);
                }
                hexColor = $"#{r:X2}{g:X2}{b:X2}";
                return true;
            }

            // Try rgb()
            var rgbMatch = RgbPattern.Match(text);
            if (rgbMatch.Success)
            {
                r = ClampByte(int.Parse(rgbMatch.Groups[1].Value));
                g = ClampByte(int.Parse(rgbMatch.Groups[2].Value));
                b = ClampByte(int.Parse(rgbMatch.Groups[3].Value));
                hexColor = $"#{r:X2}{g:X2}{b:X2}";
                return true;
            }

            // Try hsl()
            var hslMatch = HslPattern.Match(text);
            if (hslMatch.Success)
            {
                int h = int.Parse(hslMatch.Groups[1].Value) % 360;
                int s = Math.Clamp(int.Parse(hslMatch.Groups[2].Value), 0, 100);
                int l = Math.Clamp(int.Parse(hslMatch.Groups[3].Value), 0, 100);
                HslToRgb(h, s / 100.0, l / 100.0, out r, out g, out b);
                hexColor = $"#{r:X2}{g:X2}{b:X2}";
                return true;
            }

            return false;
        }

        /// <summary>
        /// Create a WPF SolidColorBrush from hex string.
        /// </summary>
        public static SolidColorBrush ToBrush(string hexColor)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hexColor);
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch
            {
                return new SolidColorBrush(Colors.Gray);
            }
        }

        // ═══ Format Converters ═══

        public static string ToHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

        public static string ToRgb(byte r, byte g, byte b) => $"rgb({r}, {g}, {b})";

        public static string ToHsl(byte r, byte g, byte b)
        {
            double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double h = 0, s = 0, l = (max + min) / 2.0;

            if (max != min)
            {
                double d = max - min;
                s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

                if (max == rd) h = (gd - bd) / d + (gd < bd ? 6 : 0);
                else if (max == gd) h = (bd - rd) / d + 2;
                else h = (rd - gd) / d + 4;

                h /= 6;
            }

            return $"hsl({(int)(h * 360)}, {(int)(s * 100)}%, {(int)(l * 100)}%)";
        }

        // ═══ Helpers ═══

        private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);

        private static void HslToRgb(int h, double s, double l, out byte r, out byte g, out byte b)
        {
            double hue = h / 360.0;

            if (s == 0)
            {
                r = g = b = (byte)(l * 255);
                return;
            }

            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;

            r = (byte)(HueToRgb(p, q, hue + 1.0 / 3) * 255);
            g = (byte)(HueToRgb(p, q, hue) * 255);
            b = (byte)(HueToRgb(p, q, hue - 1.0 / 3) * 255);
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }
}
