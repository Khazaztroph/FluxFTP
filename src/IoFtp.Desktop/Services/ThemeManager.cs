using System.Windows;
using System.Windows.Media;
using IoFtp.Desktop.Models;

namespace IoFtp.Desktop.Services;

internal static class ThemeManager
{
    public static ThemeSettings Default => new();

    public static void Apply(ThemeSettings? value)
    {
        var theme = value ?? Default;
        var resources = Application.Current.Resources;
        SetBrush(resources, "WindowBrush", theme.Window);
        SetBrush(resources, "SurfaceBrush", theme.Surface);
        SetBrush(resources, "SurfaceRaisedBrush", theme.SurfaceRaised);
        SetBrush(resources, "BorderBrush", theme.Border);
        SetBrush(resources, "AccentBrush", theme.Accent);
        SetBrush(resources, "AccentStrongBrush", theme.AccentStrong);
        SetBrush(resources, "TextBrush", theme.Text);
        SetBrush(resources, "MutedTextBrush", theme.MutedText);
        SetBrush(resources, "SelectionBrush", theme.Selection);
        SetBrush(resources, "HoverBrush", theme.Hover);
        resources["AppFontFamily"] = new FontFamily(theme.FontFamily);
        resources["AppMonospaceFontFamily"] = new FontFamily(theme.MonospaceFontFamily);
        resources["AppFontSize"] = theme.FontSize;

        foreach (Window window in Application.Current.Windows)
        {
            window.FontFamily = new FontFamily(theme.FontFamily);
            window.FontSize = theme.FontSize;
        }
    }

    public static bool TryValidate(ThemeSettings theme, out string error)
    {
        foreach (var pair in Colors(theme))
        {
            try { _ = (Color)ColorConverter.ConvertFromString(pair.Value); }
            catch { error = $"{pair.Key} is not a valid color. Use #RRGGBB or #AARRGGBB."; return false; }
        }
        if (string.IsNullOrWhiteSpace(theme.FontFamily) || string.IsNullOrWhiteSpace(theme.MonospaceFontFamily))
        { error = "Font names cannot be empty."; return false; }
        if (theme.FontSize is < 9 or > 28)
        { error = "Font size must be between 9 and 28."; return false; }
        error = "";
        return true;
    }

    private static IEnumerable<KeyValuePair<string, string>> Colors(ThemeSettings t)
    {
        yield return new("Window", t.Window); yield return new("Surface", t.Surface);
        yield return new("Raised surface", t.SurfaceRaised); yield return new("Border", t.Border);
        yield return new("Accent", t.Accent); yield return new("Strong accent", t.AccentStrong);
        yield return new("Text", t.Text); yield return new("Muted text", t.MutedText);
        yield return new("Selection", t.Selection); yield return new("Hover", t.Hover);
    }

    private static void SetBrush(ResourceDictionary resources, string key, string color)
    {
        var parsed = (Color)ColorConverter.ConvertFromString(color);
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen) brush.Color = parsed;
        else resources[key] = new SolidColorBrush(parsed);
    }
}
