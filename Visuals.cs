using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TrayMixer
{
    static class Glyphs
    {
        public static readonly string FontName = FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets");
        static readonly bool IsFluent = FontName == "Segoe Fluent Icons";

        public static readonly string Mixer = IsFluent ? "" : "";
        public const string Mute = "";
        public const string Bell = "";
        public const string SystemSounds = "";
        public const string More = "";

        public static string ForVolume(Level level)
        {
            if (level.Muted) return Mute;
            if (level.Volume == 0) return "";
            if (level.Volume < 34) return "";
            if (level.Volume < 67) return "";
            return "";
        }

        static string FirstInstalled(params string[] names)
        {
            foreach (string name in names)
            {
                using (var font = new Font(name, 10f))
                {
                    if (string.Equals(font.Name, name, StringComparison.OrdinalIgnoreCase))
                        return name;
                }
            }
            return names[names.Length - 1];
        }
    }

    static class SystemTheme
    {
        const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

        public static bool AppsUseLightTheme => ReadFlag("AppsUseLightTheme", true);
        public static bool TaskbarUsesLightTheme => ReadFlag("SystemUsesLightTheme", false);

        // Windows 11 draws slider fills with "accent light 2" in dark mode and "accent dark 1" in light mode.
        public static Color Accent(bool lightTheme)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(AccentKey))
            {
                if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 32)
                {
                    int i = (lightTheme ? 4 : 1) * 4;
                    return Color.FromArgb(palette[i], palette[i + 1], palette[i + 2]);
                }
            }
            return lightTheme ? Color.FromArgb(0x00, 0x5F, 0xB8) : Color.FromArgb(0x60, 0xCD, 0xFF);
        }

        static bool ReadFlag(string name, bool fallback)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey))
                return key?.GetValue(name) is int value ? value != 0 : fallback;
        }
    }

    sealed class Palette
    {
        public Color Background, Border, Text, SubText, Hover, Pressed, Separator, Track, ThumbOuter, ThumbBorder, MutedFill, Accent, Peak;

        public static Palette Current()
        {
            bool light = SystemTheme.AppsUseLightTheme;
            Color accent = SystemTheme.Accent(light);
            var palette = light ? LightBase() : DarkBase();
            palette.Accent = accent;
            palette.Peak = Blend(accent, Color.White, light ? 0.55f : 0.5f);
            return palette;
        }

        static Palette DarkBase() => new Palette
        {
            Background = Color.FromArgb(0x2B, 0x2B, 0x2B),
            Border = Color.FromArgb(0x45, 0x45, 0x45),
            Text = Color.White,
            SubText = Color.FromArgb(0xAB, 0xAB, 0xAB),
            Hover = Color.FromArgb(0x38, 0x38, 0x38),
            Pressed = Color.FromArgb(0x32, 0x32, 0x32),
            Separator = Color.FromArgb(0x3A, 0x3A, 0x3A),
            Track = Color.FromArgb(0x9A, 0x9A, 0x9A),
            ThumbOuter = Color.FromArgb(0x45, 0x45, 0x45),
            ThumbBorder = Color.FromArgb(0x52, 0x52, 0x52),
            MutedFill = Color.FromArgb(0x6B, 0x6B, 0x6B),
        };

        static Palette LightBase() => new Palette
        {
            Background = Color.FromArgb(0xF9, 0xF9, 0xF9),
            Border = Color.FromArgb(0xD6, 0xD6, 0xD6),
            Text = Color.FromArgb(0x1B, 0x1B, 0x1B),
            SubText = Color.FromArgb(0x5D, 0x5D, 0x5D),
            Hover = Color.FromArgb(0xEC, 0xEC, 0xEC),
            Pressed = Color.FromArgb(0xE4, 0xE4, 0xE4),
            Separator = Color.FromArgb(0xE5, 0xE5, 0xE5),
            Track = Color.FromArgb(0x8A, 0x8A, 0x8A),
            ThumbOuter = Color.White,
            ThumbBorder = Color.FromArgb(0xD4, 0xD4, 0xD4),
            MutedFill = Color.FromArgb(0xA6, 0xA6, 0xA6),
        };

        public static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }

    static class TrayIcons
    {
        public static Icon Create(string glyph, bool lightTaskbar)
        {
            Size size = SystemInformation.SmallIconSize;
            using (var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                using (var family = new FontFamily(Glyphs.FontName))
                using (var path = new GraphicsPath())
                using (var format = new StringFormat(StringFormat.GenericTypographic))
                {
                    Color color = lightTaskbar ? Color.Black : Color.White;
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    path.AddString(glyph, family, (int)FontStyle.Regular, size.Height, new RectangleF(0, 0, size.Width, size.Height), format);
                    // The icon font's hairline strokes look faint at tray size, so thicken them slightly.
                    using (var brush = new SolidBrush(color))
                    using (var pen = new Pen(color, size.Height / 20f) { LineJoin = LineJoin.Round })
                    {
                        g.FillPath(brush, path);
                        g.DrawPath(pen, path);
                    }
                }
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return Native.CloneIcon(handle);
                }
                finally
                {
                    Native.DestroyIcon(handle);
                }
            }
        }
    }

    // Executable icons, extracted at the exact pixel size they're drawn at. Only used on the UI thread.
    static class AppIcons
    {
        static readonly Dictionary<string, Icon> Cache = new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);

        public static Icon Get(string imagePath, int size)
        {
            if (string.IsNullOrEmpty(imagePath))
                return null;
            string key = size + "|" + imagePath;
            if (Cache.TryGetValue(key, out Icon icon))
                return icon;
            icon = Extract(imagePath, size);
            Cache[key] = icon;
            return icon;
        }

        static Icon Extract(string imagePath, int size)
        {
            if (SHDefExtractIcon(imagePath, 0, 0, out IntPtr large, out IntPtr small, (uint)(size | (16 << 16))) == 0 && large != IntPtr.Zero)
            {
                try
                {
                    return Native.CloneIcon(large);
                }
                finally
                {
                    Native.DestroyIcon(large);
                    if (small != IntPtr.Zero)
                        Native.DestroyIcon(small);
                }
            }
            try
            {
                return Icon.ExtractAssociatedIcon(imagePath);
            }
            catch (ArgumentException) { }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHDefExtractIcon(string iconFile, int index, uint flags, out IntPtr largeIcon, out IntPtr smallIcon, uint iconSize);
    }

    static class Native
    {
        public static Icon CloneIcon(IntPtr handle)
        {
            using (var temporary = Icon.FromHandle(handle))
                return (Icon)temporary.Clone();
        }

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
    }

    static class GraphicsExtensions
    {
        public static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF rect, float radius)
        {
            using (var path = new GraphicsPath())
            {
                float d = radius * 2;
                path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }
    }
}
