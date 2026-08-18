using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using CutFlow.Models;

namespace CutFlow.Services;

public static class ThemeService
{
    public static void Apply(AppSettings settings)
    {
        var mode = settings.ThemeMode?.Trim() ?? "System";
        var useDark = mode.Equals("Dark", StringComparison.OrdinalIgnoreCase) ||
                      (mode.Equals("System", StringComparison.OrdinalIgnoreCase) && !WindowsUsesLightApps());

        Color window;
        if (mode.Equals("Custom", StringComparison.OrdinalIgnoreCase))
            window = Color.FromRgb(settings.CustomBackgroundR, settings.CustomBackgroundG, settings.CustomBackgroundB);
        else if (useDark)
            window = Color.FromRgb(24, 25, 27);
        else
            window = Color.FromRgb(244, 244, 242);

        var dark = Luminance(window) < 0.46;
        var custom = mode.Equals("Custom", StringComparison.OrdinalIgnoreCase);

        // Custom mode exposes BOTH the outer canvas and the editor/card surface. Contrast
        // is still derived automatically so a custom palette never makes text disappear.
        var customSurface = Color.FromRgb(settings.CustomSurfaceR, settings.CustomSurfaceG, settings.CustomSurfaceB);
        var panel = custom
            ? customSurface
            : (dark ? Mix(window, Colors.White, 0.055) : Color.FromRgb(255, 255, 255));
        var panelDark = Luminance(panel) < 0.46;
        // If the custom panel and canvas are nearly identical, nudge the title bar just enough
        // to remain visually separated without forcing the app back into Dark theme colors.
        var title = custom
            ? Mix(panel, panelDark ? Colors.White : Colors.Black, panelDark ? 0.045 : 0.035)
            : Mix(window, dark ? Colors.White : Colors.Black, dark ? 0.035 : 0.025);
        var panelAlt = panelDark ? Mix(panel, Colors.White, 0.055) : Mix(panel, Colors.Black, 0.035);
        var input = panelDark ? Mix(panel, Colors.White, 0.075) : Mix(panel, Colors.Black, 0.018);
        var hover = panelDark ? Mix(panel, Colors.White, 0.105) : Mix(panel, Colors.Black, 0.060);
        var pressed = panelDark ? Mix(panel, Colors.White, 0.15) : Mix(panel, Colors.Black, 0.105);
        var ink = panelDark ? Color.FromRgb(239, 240, 241) : Color.FromRgb(24, 25, 24);
        var muted = panelDark ? Color.FromRgb(176, 180, 184) : Color.FromRgb(92, 96, 91);
        var line = panelDark ? Mix(panel, Colors.White, 0.18) : Mix(panel, Colors.Black, 0.18);
        var preview = panelDark ? Mix(panel, Colors.Black, 0.70) : Mix(panel, Colors.Black, 0.88);

        SetBrush("WindowBrush", window);
        SetBrush("PanelBrush", panel);
        SetBrush("PanelAltBrush", panelAlt);
        SetBrush("TitleBrush", title);
        SetBrush("InputBrush", input);
        SetBrush("HoverBrush", hover);
        SetBrush("PressedBrush", pressed);
        SetBrush("InkBrush", ink);
        SetBrush("MutedBrush", muted);
        SetBrush("LineBrush", line);
        SetBrush("StrongLineBrush", panelDark ? Mix(panel, Colors.White, 0.34) : Mix(panel, Colors.Black, 0.34));
        SetBrush("PreviewBrush", preview);
        SetBrush("AccentBrush", panelDark ? Color.FromRgb(102, 151, 180) : Color.FromRgb(77, 126, 155));
        SetBrush("AccentHoverBrush", panelDark ? Color.FromRgb(117, 166, 195) : Color.FromRgb(66, 111, 138));
        SetBrush("AccentPressedBrush", panelDark ? Color.FromRgb(82, 132, 160) : Color.FromRgb(54, 94, 118));
        SetBrush("PrimaryBrush", panelDark ? Mix(panel, Colors.White, 0.10) : Color.FromRgb(37, 38, 37));
        SetBrush("PrimaryHoverBrush", panelDark ? Mix(panel, Colors.White, 0.17) : Color.FromRgb(52, 53, 52));
        SetBrush("PrimaryPressedBrush", panelDark ? Mix(panel, Colors.Black, 0.08) : Color.FromRgb(21, 22, 21));
        SetBrush("PrimaryTextBrush", Colors.White);
        SetBrush("CutBrush", panelDark ? Color.FromRgb(226, 111, 106) : Color.FromRgb(196, 82, 78));
        SetBrush("CutFillBrush", Color.FromArgb(panelDark ? (byte)94 : (byte)70, 214, 92, 87));
        SetBrush("PendingBrush", panelDark ? Color.FromRgb(231, 181, 91) : Color.FromRgb(174, 122, 43));
        SetBrush("PendingFillBrush", Color.FromArgb(panelDark ? (byte)72 : (byte)58, 214, 157, 64));
        SetBrush("SuccessBrush", panelDark ? Color.FromRgb(112, 193, 137) : Color.FromRgb(75, 138, 93));
        SetBrush("DangerBrush", panelDark ? Color.FromRgb(238, 125, 120) : Color.FromRgb(185, 74, 70));
    }

    private static void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is not int i || i != 0;
        }
        catch { return true; }
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Mix(Color a, Color b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte M(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return Color.FromRgb(M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
    }
}
