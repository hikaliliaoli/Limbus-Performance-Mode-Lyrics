using System.Globalization;
using System.Windows.Media;

namespace NeteaseLyricsOverlay;

internal static class ImportedFontManager
{
    private static string FontsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NeteaseLyricsOverlay",
        "Fonts");

    public static bool TryImport(string sourcePath, out string copiedPath, out string familyName, out string error)
    {
        copiedPath = string.Empty;
        familyName = string.Empty;
        error = string.Empty;
        try
        {
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (extension is not ".ttf" and not ".otf")
                throw new InvalidOperationException("仅支持 .ttf 和 .otf 字体文件。");

            Directory.CreateDirectory(FontsDirectory);
            copiedPath = Path.Combine(FontsDirectory, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, copiedPath, true);

            var typeface = new GlyphTypeface(new Uri(copiedPath, UriKind.Absolute));
            familyName = ReadFamilyName(typeface);
            if (string.IsNullOrWhiteSpace(familyName))
                throw new InvalidOperationException("无法读取字体的内部家族名称。");
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    public static FontFamily Resolve(OverlayConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.CustomFontPath) && File.Exists(config.CustomFontPath))
        {
            try
            {
                var directory = Path.GetDirectoryName(config.CustomFontPath)! + Path.DirectorySeparatorChar;
                return new FontFamily(new Uri(directory, UriKind.Absolute), $"./#{config.FontFamily}");
            }
            catch
            {
                // Fall through to the installed system font.
            }
        }
        return new FontFamily(config.FontFamily);
    }

    private static string ReadFamilyName(GlyphTypeface typeface)
    {
        var preferred = new[]
        {
            CultureInfo.CurrentUICulture,
            CultureInfo.GetCultureInfo("zh-CN"),
            CultureInfo.GetCultureInfo("en-US")
        };
        foreach (var language in preferred)
            if (typeface.FamilyNames.TryGetValue(language, out var name) && !string.IsNullOrWhiteSpace(name))
                return name;
        return typeface.FamilyNames.Values.FirstOrDefault() ?? string.Empty;
    }
}
