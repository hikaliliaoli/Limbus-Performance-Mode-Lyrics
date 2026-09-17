using System.Globalization;
using System.Text.RegularExpressions;

namespace NeteaseLyricsOverlay;

internal sealed record LyricLine(TimeSpan Time, string Text, string? Translation = null);

internal static partial class LrcParser
{
    [GeneratedRegex(@"\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[\.:](?<f>\d{1,3}))?\]")]
    private static partial Regex TimestampRegex();

    public static IReadOnlyList<LyricLine> Parse(string? original, string? translated)
    {
        var primary = ParseOne(original);
        var translations = ParseOne(translated)
            .GroupBy(x => RoundKey(x.Time))
            .ToDictionary(g => g.Key, g => g.First().Text);

        return primary
            .Select(line => line with
            {
                Translation = translations.GetValueOrDefault(RoundKey(line.Time))
            })
            .OrderBy(x => x.Time)
            .ToArray();
    }

    private static List<LyricLine> ParseOne(string? lrc)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrc)) return result;

        foreach (var rawLine in lrc.Replace("\r", string.Empty).Split('\n'))
        {
            var matches = TimestampRegex().Matches(rawLine);
            if (matches.Count == 0) continue;

            var text = TimestampRegex().Replace(rawLine, string.Empty).Trim();
            foreach (Match match in matches)
            {
                var minutes = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
                var fractionText = match.Groups["f"].Value;
                var milliseconds = fractionText.Length switch
                {
                    1 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fractionText, CultureInfo.InvariantCulture) * 10,
                    3 => int.Parse(fractionText, CultureInfo.InvariantCulture),
                    _ => 0
                };
                result.Add(new LyricLine(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds) +
                                         TimeSpan.FromMilliseconds(milliseconds), text));
            }
        }

        return result;
    }

    private static long RoundKey(TimeSpan time) => (long)Math.Round(time.TotalMilliseconds / 10.0) * 10;
}
