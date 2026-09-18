using System.Threading;
using System.Text.Json;
using System.Windows;

namespace NeteaseLyricsOverlay;

internal static class Program
{
    private static Mutex? _singleInstance;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            RunSelfTest();
            return;
        }

        var diagnosticIndex = Array.FindIndex(args,
            arg => string.Equals(arg, "--diagnose", StringComparison.OrdinalIgnoreCase));
        if (diagnosticIndex >= 0)
        {
            var outputPath = diagnosticIndex + 1 < args.Length
                ? Path.GetFullPath(args[diagnosticIndex + 1])
                : Path.Combine(AppContext.BaseDirectory, "diagnostic.json");
            try
            {
                RunDiagnosticsAsync(outputPath).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, JsonSerializer.Serialize(new
                {
                    Error = exception.ToString()
                }, new JsonSerializerOptions { WriteIndented = true }));
                Environment.ExitCode = 3;
            }
            return;
        }

        _singleInstance = new Mutex(true, "NeteaseLyricsOverlay.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            System.Windows.MessageBox.Show("歌词悬浮层已经在运行。", "网易云歌词悬浮层",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var shutdownTest = args.Contains("--shutdown-test", StringComparer.OrdinalIgnoreCase);
        Exception? unhandledException = null;
        app.DispatcherUnhandledException += (_, args) =>
        {
            if (shutdownTest)
            {
                unhandledException = args.Exception;
                args.Handled = true;
                app.Shutdown(5);
                return;
            }
            System.Windows.MessageBox.Show(args.Exception.Message, "歌词悬浮层错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var performanceDemo = args.Contains("--demo-performance", StringComparer.OrdinalIgnoreCase);
        var standardDemo = args.Contains("--demo-standard", StringComparer.OrdinalIgnoreCase);
        var startInDemo = performanceDemo || standardDemo ||
                          args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var startupMode = performanceDemo ? "Performance" : standardDemo ? "Standard" : null;
        var startupAnimationStyle = args.Contains("--animation-none", StringComparer.OrdinalIgnoreCase)
            ? 0
            : args.Contains("--animation-limbus", StringComparer.OrdinalIgnoreCase)
                ? 1
                : (int?)null;
        var window = new OverlayWindow(startInDemo, startupMode, startupAnimationStyle);
        if (shutdownTest)
        {
            window.Loaded += async (_, _) =>
            {
                await Task.Delay(9500);
                window.Close();
            };
        }
        app.Run(window);
        if (shutdownTest && unhandledException is not null)
        {
            AppDiagnostics.Write("ShutdownTest", unhandledException);
            Environment.ExitCode = 5;
        }
        _singleInstance.ReleaseMutex();
    }

    private static void RunSelfTest()
    {
        var lines = LrcParser.Parse(
            "[00:01.20]第一句\n[00:03.045]第二句",
            "[00:01.20]Line one\n[00:03.045]Line two");
        if (lines.Count != 2 ||
            lines[0].Time != TimeSpan.FromMilliseconds(1200) ||
            lines[1].Time != TimeSpan.FromMilliseconds(3045) ||
            lines[0].Translation != "Line one")
        {
            Environment.ExitCode = 2;
        }

        if (!NeteaseProgressReader.TryParseCombinedProgress(
                "01:42 / 03:58", out var position, out var duration) ||
            position != TimeSpan.FromSeconds(102) ||
            duration != TimeSpan.FromSeconds(238))
        {
            Environment.ExitCode = 4;
        }

        if (OverlayWindow.StandardSlotForLine(0, 2) != 0 ||
            OverlayWindow.StandardSlotForLine(1, 2) != 1 ||
            OverlayWindow.StandardSlotForLine(2, 2) != 0 ||
            OverlayWindow.StandardSlotForLine(3, 2) != 1)
        {
            Environment.ExitCode = 6;
        }

        var chineseLyrics = new[]
        {
            "黑夜里的梦想仍在燃烧",
            "穿过沉默寻找最后的微光",
            "世界尽头留下遥远的回声"
        };
        var chineseKeywords = KeywordSelector.Select(chineseLyrics, "self-test-chinese")
            .SelectMany(pair => pair.Value)
            .ToArray();
        if (chineseKeywords.Length != 2 ||
            chineseKeywords.Any(span => !span.Text.All(IsHanForSelfTest)))
        {
            Environment.ExitCode = 7;
        }

        var japaneseLyrics = new[]
        {
            "ミッドナイトのノイズをこえて",
            "ココロのシグナルがひびく",
            "エンドレスなリズムをきざむ"
        };
        var japaneseKeywords = KeywordSelector.Select(japaneseLyrics, "self-test-japanese")
            .SelectMany(pair => pair.Value)
            .ToArray();
        if (japaneseKeywords.Length != 2 || japaneseKeywords.Any(span =>
                !(span.Text.All(IsHanForSelfTest) || span.Text.All(IsKatakanaForSelfTest))))
        {
            Environment.ExitCode = 8;
        }

        var japaneseVerbLyrics = new[]
        {
            "飲み込んでしまえばもう",
            "眠れない夜を越えて",
            "会いたいを認めざるを得ない"
        };
        var japaneseVerbKeywords = KeywordSelector.Select(japaneseVerbLyrics, "self-test-japanese-verbs")
            .SelectMany(pair => pair.Value)
            .ToArray();
        if (japaneseVerbKeywords.Length < 2 ||
            japaneseVerbKeywords.Any(span => span.Length != 1 || !span.Text.All(IsHanForSelfTest)))
        {
            Environment.ExitCode = 10;
        }

        var japaneseDenseLyrics = new[]
        {
            "青い空を越えて赤い花が咲く",
            "月を見ながら遠い夢を抱く",
            "声を重ねて夜の道を歩く"
        };
        var japaneseDenseKeywords = KeywordSelector.Select(
                japaneseDenseLyrics, "self-test-japanese-density")
            .SelectMany(pair => pair.Value)
            .ToArray();
        if (japaneseDenseKeywords.Length < 4)
        {
            Environment.ExitCode = 11;
        }

        var englishLyrics = new[]
        {
            "Carry the final flame through endless night",
            "Broken memories awaken under silver skies",
            "Follow every heartbeat beyond the silence"
        };
        var englishSelections = KeywordSelector.Select(englishLyrics, "self-test-english");
        var englishKeywords = englishSelections.SelectMany(pair => pair.Value).ToArray();
        var highlightedEnglishUnits = englishSelections.Sum(pair =>
            KeywordSelector.BuildAnimationUnits(englishLyrics[pair.Key], pair.Value)
                .Count(unit => unit.IsHighlighted));
        if (englishKeywords.Length != 2 || highlightedEnglishUnits != englishKeywords.Length ||
            englishKeywords.Any(span => span.Text.Any(character => !IsEnglishWordCharacter(character))))
        {
            Environment.ExitCode = 9;
        }
    }

    private static bool IsHanForSelfTest(char value) =>
        value is >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF' or
            >= '\uF900' and <= '\uFAFF' or '\u3005';

    private static bool IsKatakanaForSelfTest(char value) =>
        value is >= '\u30A1' and <= '\u30FA' or >= '\u30FD' and <= '\u30FF' or
            >= '\u31F0' and <= '\u31FF' or >= '\uFF66' and <= '\uFF9D' or '\u30FC' or '\uFF70';

    private static bool IsEnglishWordCharacter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '\'' or '-';

    private static async Task RunDiagnosticsAsync(string outputPath)
    {
        using var reader = new MediaSessionReader();
        var samples = new List<object>();
        PlaybackSnapshot? first = null;
        const int diagnosticSampleCount = 20;
        for (var i = 0; i < diagnosticSampleCount; i++)
        {
            var candidates = await reader.ReadAllAsync();
            var snapshot = candidates
                .OrderByDescending(x => x.IsPlaying)
                .ThenByDescending(x => x.LastUpdatedTime)
                .FirstOrDefault();
            first ??= snapshot;
            samples.Add(new
            {
                CapturedAt = DateTimeOffset.Now,
                Candidates = candidates.Select(candidate => new
                {
                    candidate.Title,
                    candidate.Artist,
                    PositionMilliseconds = candidate.Position.TotalMilliseconds,
                    candidate.IsPlaying,
                    candidate.LastUpdatedTime,
                    EndMilliseconds = candidate.EndTime.TotalMilliseconds,
                    candidate.PlaybackStatus,
                    candidate.SourceId,
                    candidate.HasReliablePosition,
                    candidate.PositionSource,
                    candidate.PositionCapturedAt,
                    candidate.SongId
                })
            });
            if (i < diagnosticSampleCount - 1) await Task.Delay(750);
        }

        var lyricCount = 0;
        var translatedLineCount = 0;
        if (first is not null)
        {
            var client = new NeteaseApiClient();
            var lyrics = await client.GetLyricsAsync(
                first.Title, first.Artist, first.SongId, CancellationToken.None);
            lyricCount = lyrics.Count;
            translatedLineCount = lyrics.Count(line => !string.IsNullOrWhiteSpace(line.Translation));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            Samples = samples,
            LyricLineCount = lyricCount,
            TranslatedLineCount = translatedLineCount,
            LogPath = AppDiagnostics.LogPath
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
