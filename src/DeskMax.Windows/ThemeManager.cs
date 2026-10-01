using System.IO;
using System.Text.Json;
using System.Windows;

namespace DeskMax.Windows;

public static class ThemeManager
{
    private static readonly string Preferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskMax", "preferences.json");
    public static bool IsDark { get; private set; } = true;
    public static void Load()
    {
        bool dark = true;
        try
        {
            if (File.Exists(Preferences)) dark = JsonSerializer.Deserialize<ThemePreferences>(File.ReadAllText(Preferences))?.Dark ?? true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        Apply(dark, false);
    }
    public static void Apply(bool dark, bool save = true)
    {
        var dictionary = new ResourceDictionary { Source = new Uri($"/DeskMax;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;
        foreach (var old in merged.Where(d => d.Source?.OriginalString.Contains("/Themes/", StringComparison.Ordinal) == true).ToArray()) merged.Remove(old);
        merged.Add(dictionary); IsDark = dark;
        if (!save) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Preferences)!);
            File.WriteAllText(Preferences, JsonSerializer.Serialize(new ThemePreferences(dark)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private sealed record ThemePreferences(bool Dark);
}
