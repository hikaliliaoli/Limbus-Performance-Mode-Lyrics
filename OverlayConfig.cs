using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NeteaseLyricsOverlay;

internal sealed class OverlayConfig
{
    public int ConfigVersion { get; set; } = 7;
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 240;
    public double CurrentFontSize { get; set; } = 46;
    public double ContextFontSize { get; set; } = 22;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public string CustomFontPath { get; set; } = string.Empty;
    public string CurrentColor { get; set; } = "#FFF3F0E8";
    public string ContextColor { get; set; } = "#C0D7D2C8";
    public string ShadowColor { get; set; } = "#F0000000";
    public double LyricOpacity { get; set; } = 1.0;
    public double FontScale { get; set; } = 1.0;
    public int MaxVisibleLyrics { get; set; } = 2;
    public List<LyricSlotPosition> StandardLyricPositions { get; set; } = [];
    public bool PositionLocked { get; set; } = true;
    public bool ShowSongTitle { get; set; } = false;
    public bool ShowTranslation { get; set; } = true;
    public bool ShowPreviousLine { get; set; } = false;
    public bool ShowNextLine { get; set; } = false;
    public int LyricOffsetMilliseconds { get; set; } = 0;
    public int PollIntervalMilliseconds { get; set; } = 250;
    public int AnimationFps { get; set; } = 45;
    public double CharactersPerSecond { get; set; } = 13;
    public int CharacterFadeInMilliseconds { get; set; } = 170;
    public int LineFadeOutMilliseconds { get; set; } = 650;
    public double CharacterDriftPixels { get; set; } = 9;
    public double CharacterJitterPixels { get; set; } = 0.9;
    public string DisplayMode { get; set; } = "Standard";
    public string LyricLanguage { get; set; } = "Original";
    public double PerformanceRisePixels { get; set; } = 30;
    public double PerformanceTiltDegrees { get; set; } = 2.4;
    public double PerformancePreviousFadeStart { get; set; } = 0.7;
    public string LineFormat { get; set; } = "{lyric}";
    public string TranslationFormat { get; set; } = "{translation}";
    public string TitleFormat { get; set; } = "{title}  ·  {artist}";

    [JsonIgnore]
    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NeteaseLyricsOverlay",
        "config.json");

    public static OverlayConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                var defaults = new OverlayConfig();
                defaults.Save();
                return defaults;
            }

            var json = File.ReadAllText(ConfigPath);
            using var document = JsonDocument.Parse(json);
            var hasVersion = document.RootElement.TryGetProperty(nameof(ConfigVersion), out _);
            var config = JsonSerializer.Deserialize<OverlayConfig>(json, JsonOptions())
                         ?? new OverlayConfig();
            var savedVersion = hasVersion
                ? document.RootElement.GetProperty(nameof(ConfigVersion)).GetInt32()
                : 0;
            if (savedVersion < 2)
            {
                config.ApplyPerformanceStyleDefaults();
            }
            if (savedVersion < 3) config.ApplyVersion3Defaults();
            if (savedVersion < 4) config.ApplyVersion4Defaults();
            if (savedVersion < 5) config.ApplyVersion5Defaults();
            if (savedVersion < 6) config.ApplyVersion6Defaults();
            if (savedVersion < 7) config.ApplyVersion7Defaults();
            config.MaxVisibleLyrics = Math.Clamp(config.MaxVisibleLyrics, 1, 10);
            config.StandardLyricPositions ??= [];
            foreach (var position in config.StandardLyricPositions)
            {
                position.X = NormalizePosition(position.X);
                position.Y = NormalizePosition(position.Y);
            }
            if (savedVersion < 7) config.Save();
            return config;
        }
        catch
        {
            return new OverlayConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions()));
    }

    private void ApplyPerformanceStyleDefaults()
    {
        ConfigVersion = 2;
        Top = -1;
        Width = 1200;
        Height = 240;
        CurrentFontSize = 46;
        ContextFontSize = 22;
        CurrentColor = "#FFF3F0E8";
        ContextColor = "#C0D7D2C8";
        ShadowColor = "#F0000000";
        ShowSongTitle = false;
        ShowPreviousLine = false;
        ShowNextLine = false;
        PollIntervalMilliseconds = 250;
        AnimationFps = 45;
        CharactersPerSecond = 13;
        CharacterFadeInMilliseconds = 170;
        LineFadeOutMilliseconds = 650;
        CharacterDriftPixels = 9;
        CharacterJitterPixels = 0.9;
    }

    private void ApplyVersion3Defaults()
    {
        ConfigVersion = 3;
        CustomFontPath = string.Empty;
        DisplayMode = "Standard";
        LyricLanguage = "Original";
        PerformanceRisePixels = 30;
        PerformanceTiltDegrees = 2.4;
        PerformancePreviousFadeStart = 0.7;
    }

    private void ApplyVersion4Defaults()
    {
        ConfigVersion = 4;
        Left = -1;
        LyricOpacity = 1.0;
        PositionLocked = true;
    }

    private void ApplyVersion5Defaults()
    {
        ConfigVersion = 5;
        FontScale = 1.0;
    }

    private void ApplyVersion6Defaults()
    {
        ConfigVersion = 6;
        MaxVisibleLyrics = 2;
    }

    private void ApplyVersion7Defaults()
    {
        ConfigVersion = 7;
        StandardLyricPositions = [];
    }

    private static double NormalizePosition(double value) =>
        double.IsFinite(value) && value >= 0 ? Math.Clamp(value, 0, 1) : -1;

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}

internal sealed class LyricSlotPosition
{
    public double X { get; set; } = -1;
    public double Y { get; set; } = -1;
}
