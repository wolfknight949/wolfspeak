using System.Windows;
using System.Windows.Media;

namespace WolfSpeak;

static class TimePalette
{
    internal static int Period(int hour) => hour < 6 || hour >= 20 ? 3 : hour < 10 ? 0 : hour < 17 ? 1 : 2;

    internal static void Apply(Window window, int period)
    {
        // Keep the night palette's slate foundation; daylight adds restrained natural accents.
        string[][] palettes =
        [
            ["#19232D", "#23303B", "#30414E", "#141E28", "#3C5060", "#657F91", "#EDF2F5", "#B7C6D0", "#9EB2C1", "#ACD0C0", "#ACD0C0"],
            ["#18242E", "#22323D", "#30434F", "#131F29", "#3B5261", "#638393", "#EDF2F5", "#B7C6D0", "#9EB2C1", "#ACD0C0", "#ACD0C0"],
            ["#1C222E", "#27303D", "#35404F", "#161C28", "#465063", "#738096", "#EDF2F5", "#B7C6D0", "#9EB2C1", "#ACD0C0", "#ACD0C0"],
            ["#0B0F16", "#141925", "#232B3B", "#10141E", "#293246", "#526480", "#F0F3FA", "#A6B4CA", "#8C9DB8", "#2DCAA0", "#20C493"]
        ];
        string[] keys = ["BgBrush", "CardBrush", "CardHiBrush", "InputBrush", "LineBrush", "LineHiBrush", "TextBrush", "SubTextBrush", "FaintTextBrush", "TalkBrush", "CallActionBrush"];
        var colors = palettes[period];
        for (int i = 0; i < keys.Length; i++) window.Resources[keys[i]] = Brush(colors[i]);
        var pageBackground = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        pageBackground.GradientStops.Add(new GradientStop(ColorOf(colors[0]), 0));
        pageBackground.GradientStops.Add(new GradientStop(ColorOf(colors[0]), .56));
        pageBackground.GradientStops.Add(new GradientStop(ColorOf("#111522"), .78));
        pageBackground.GradientStops.Add(new GradientStop(ColorOf("#0D121C"), 1));
        pageBackground.Freeze();
        window.Resources["BgBrush"] = pageBackground;
        window.Resources["CyanBrush"] = Brush("#22C4DF");
        window.Resources["OnActionBrush"] = Brush("#09291F");
        // A consistent slate foundation keeps the app comfortable; the scene supplies the color.
        Color background = ColorOf(colors[0]);
        // The scenery draws stepped fog itself, after the complete landscape.
        window.Resources["SceneryComfortBrush"] = Brushes.Transparent;
        var settingsBlend = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        string[] skies = ["#AFB7A1", "#70B6DA", "#BE8361", "#0B0F16"];
        window.Resources["SkyBrush"] = Brush(skies[period]);
        settingsBlend.GradientStops.Add(new GradientStop(ColorOf(skies[period]), 0));
        settingsBlend.GradientStops.Add(new GradientStop(ColorOf(skies[period]), .30));
        settingsBlend.GradientStops.Add(new GradientStop(background, .90));
        settingsBlend.GradientStops.Add(new GradientStop(background, 1));
        settingsBlend.Freeze();
        window.Resources["SettingsSceneryBrush"] = settingsBlend;
        window.Resources["HeaderTextBrush"] = Brush(period == 3 ? colors[6] : "#182B31");
        window.Resources["HeaderSubBrush"] = Brush(period == 3 ? colors[7] : "#304852");
        foreach (string key in new[] { "AccentBrush", "AccentHBrush" })
        {
            var gradient = new LinearGradientBrush(ColorOf("#22C4DF"), ColorOf("#9460F5"), 0);
            gradient.Freeze();
            window.Resources[key] = gradient;
        }
    }

    static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);
    static Brush Brush(string value) { var brush = new SolidColorBrush(ColorOf(value)); brush.Freeze(); return brush; }
}
