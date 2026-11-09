using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkView;

/// <summary>User preferences, kept in %LOCALAPPDATA%\MarkView\settings.json (removed by the uninstaller).</summary>
sealed class Settings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string FontFamily { get; set; } = "Consolas";
    public float FontSize { get; set; } = 11f;
    public bool WordWrap { get; set; }
    public bool StatusBar { get; set; } = true;
    public int[]? WindowBounds { get; set; }  // x, y, width, height of the restored window
    public bool Maximized { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkView", "settings.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new(); }
        catch { return new(); }  // first run, or a damaged file: start from defaults
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch { /* preferences are a convenience; never fail a command over them */ }
    }
}
