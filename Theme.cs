using System;
using System.Drawing;
using Microsoft.Win32;

namespace TaskbarMetrics
{
    sealed class Palette
    {
        public bool Light;
        public Color Background;
        public Color Text;
        public Color Label;
        public Color Border;
        public Color Divider;
        public Color Accent;
        public Color Warning;
        public Color Critical;
        public Color[] MetricColors;
    }

    static class ThemeService
    {
        static readonly Color[] DarkMetrics =
        {
            Color.FromArgb(76, 194, 255),
            Color.FromArgb(167, 139, 250),
            Color.FromArgb(52, 211, 153),
            Color.FromArgb(251, 191, 36),
            Color.FromArgb(96, 165, 250),
            Color.FromArgb(34, 211, 238)
        };

        static readonly Color[] LightMetrics =
        {
            Color.FromArgb(0, 120, 212),
            Color.FromArgb(136, 90, 180),
            Color.FromArgb(16, 124, 16),
            Color.FromArgb(183, 121, 31),
            Color.FromArgb(0, 90, 158),
            Color.FromArgb(0, 124, 137)
        };

        public static Palette Create(AppSettings settings)
        {
            if (settings.FollowSystem)
                return CreateSystemPalette();

            bool light = IsLight(settings.Background);
            Palette p = new Palette();
            p.Light = light;
            p.Background = settings.Background;
            p.Text = settings.TextColor;
            p.Label = settings.LabelColor;
            p.Border = settings.BorderColor;
            p.Divider = Blend(settings.Background, settings.BorderColor, 0.65f);
            p.Accent = light ? Color.FromArgb(0, 120, 212) : Color.FromArgb(76, 194, 255);
            p.Warning = light ? Color.FromArgb(166, 110, 0) : Color.FromArgb(245, 196, 81);
            p.Critical = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 107, 107);
            p.MetricColors = light ? LightMetrics : DarkMetrics;
            return p;
        }

        static Palette CreateSystemPalette()
        {
            bool light = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0) != 0;
            Palette p = new Palette();
            p.Light = light;
            p.Background = light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(31, 31, 31);
            p.Text = light ? Color.FromArgb(24, 24, 24) : Color.FromArgb(242, 242, 242);
            p.Label = light ? Color.FromArgb(92, 92, 92) : Color.FromArgb(162, 162, 162);
            p.Border = light ? Color.FromArgb(198, 198, 198) : Color.FromArgb(62, 62, 62);
            p.Divider = light ? Color.FromArgb(218, 218, 218) : Color.FromArgb(46, 46, 46);
            p.Warning = light ? Color.FromArgb(166, 110, 0) : Color.FromArgb(245, 196, 81);
            p.Critical = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 107, 107);
            p.MetricColors = light ? LightMetrics : DarkMetrics;

            Color accent = ReadSystemAccent();
            p.Accent = accent;
            return p;
        }

        public static string Signature(Palette p)
        {
            return p.Background.ToArgb() + "|" + p.Text.ToArgb() + "|" + p.Label.ToArgb() + "|" +
                p.Border.ToArgb() + "|" + p.Accent.ToArgb() + "|" + (p.Light ? "1" : "0");
        }

        public static Color Blend(Color a, Color b, float amount)
        {
            float t = Math.Max(0f, Math.Min(1f, amount));
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static bool IsLight(Color color)
        {
            double luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
            return luminance > 0.58;
        }

        public static Color ReadableTextFor(Color background)
        {
            return IsLight(background) ? Color.FromArgb(20, 20, 20) : Color.FromArgb(245, 245, 245);
        }

        static Color ReadSystemAccent()
        {
            try
            {
                int value = ReadDwordSigned(@"Software\Microsoft\Windows\DWM", "ColorizationColor", 0);
                if (value != 0)
                {
                    Color c = Color.FromArgb(value);
                    if (c.A < 255) c = Color.FromArgb(255, c.R, c.G, c.B);
                    return c;
                }
            }
            catch
            {
            }
            return Color.FromArgb(76, 194, 255);
        }

        static int ReadDword(string path, string name, int fallback)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, false))
                {
                    if (key == null) return fallback;
                    object value = key.GetValue(name);
                    if (value is int) return (int)value;
                }
            }
            catch
            {
            }
            return fallback;
        }

        static int ReadDwordSigned(string path, string name, int fallback)
        {
            return ReadDword(path, name, fallback);
        }
    }
}
