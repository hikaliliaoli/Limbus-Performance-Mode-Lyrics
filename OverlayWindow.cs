using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NeteaseLyricsOverlay;

internal sealed class OverlayWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExNoactivate = 0x08000000;

    private readonly Grid _root = new();
    private readonly StackPanel _standardPanel = new()
    {
        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Canvas _performanceCanvas = new() { IsHitTestVisible = false };
    private readonly TextBlock _title = NewTextBlock();
    private readonly TextBlock _previous = NewTextBlock();
    private readonly TextBlock _next = NewTextBlock();
    private readonly TextBlock _status = NewTextBlock();
    private readonly StackPanel _standardGlyphPanel = new() { Orientation = Orientation.Horizontal };
    private readonly Viewbox _standardGlyphViewbox = NewViewbox();
    private readonly List<AnimatedGlyph> _standardGlyphs = [];
    private readonly List<PerformanceLineVisual> _performanceLines = [];

    private readonly MediaSessionReader _media = new();
    private readonly NeteaseApiClient _api = new();
    private readonly Forms.NotifyIcon _tray;
    private Forms.ToolStripMenuItem? _nowPlayingItem;
    private Forms.ToolStripMenuItem? _syncStatusItem;
    private Forms.ToolStripMenuItem? _originalLanguageItem;
    private Forms.ToolStripMenuItem? _translationLanguageItem;
    private Forms.ToolStripMenuItem? _standardModeItem;
    private Forms.ToolStripMenuItem? _performanceModeItem;
    private Forms.ToolStripMenuItem? _positionLockItem;
    private Forms.TrackBar? _opacitySlider;
    private Forms.NumericUpDown? _opacityNumber;
    private bool _updatingOpacitySlider;
    private Forms.TrackBar? _fontScaleSlider;
    private Forms.TextBox? _fontScaleText;
    private bool _updatingFontScaleSlider;

    private DispatcherTimer _pollTimer = null!;
    private DispatcherTimer _animationTimer = null!;
    private OverlayConfig _config = new();
    private FontFamily _activeFont = new("Microsoft YaHei UI");
    private IReadOnlyList<LyricLine> _lyrics = [];
    private string _songKey = string.Empty;
    private DateTimeOffset _nextLyricsRetry = DateTimeOffset.MinValue;
    private bool _pollRunning;
    private bool _lyricsLoading;
    private string _lyricsLoadingSongKey = string.Empty;
    private CancellationTokenSource? _lyricsCancellation;
    private bool _isClosing;

    private int _activeLineIndex = int.MinValue;
    private TimeSpan _activeLineStart;
    private TimeSpan _activeLineEnd;

    private bool _clockInitialized;
    private string _clockSongKey = string.Empty;
    private TimeSpan _clockAnchorPosition;
    private DateTimeOffset _clockAnchorTime;
    private TimeSpan _lastRawPosition;
    private bool _clockPlaying;
    private bool _timelineReliable;
    private bool _demoMode;
    private readonly bool _startInDemo;
    private readonly string? _startupModeOverride;

    public OverlayWindow(bool startInDemo = false, string? startupModeOverride = null)
    {
        _startInDemo = startInDemo;
        _startupModeOverride = startupModeOverride;
        Title = "网易云歌词悬浮层";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SnapsToDevicePixels = true;

        _standardGlyphViewbox.Child = _standardGlyphPanel;
        var currentHost = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        currentHost.Children.Add(_standardGlyphViewbox);
        currentHost.Children.Add(_status);
        _standardPanel.Children.Add(_title);
        _standardPanel.Children.Add(_previous);
        _standardPanel.Children.Add(currentHost);
        _standardPanel.Children.Add(_next);
        _root.Children.Add(_standardPanel);
        _root.Children.Add(_performanceCanvas);
        _root.Background = System.Windows.Media.Brushes.Transparent;
        Content = _root;

        _tray = BuildTrayIcon();
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += (_, _) => UpdateInteractionMode();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ReloadConfig();
        if (!string.IsNullOrWhiteSpace(_startupModeOverride))
        {
            _config.DisplayMode = _startupModeOverride;
            ApplyWindowMode();
            RefreshMenuChecks();
        }
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_config.PollIntervalMilliseconds) };
        _pollTimer.Tick += async (_, _) => await PollMediaAsync();
        _pollTimer.Start();

        _animationTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(_config.AnimationFps, 15, 60)),
            DispatcherPriority.Render,
            (_, _) => AnimationTick(),
            Dispatcher);
        _animationTimer.Start();

        if (_startInDemo) StartDemo();
        else await PollMediaAsync();
    }

    private async Task PollMediaAsync()
    {
        if (_demoMode || _pollRunning) return;
        _pollRunning = true;
        try
        {
            var snapshot = await _media.ReadAsync();
            if (snapshot is null)
            {
                _clockInitialized = false;
                _timelineReliable = false;
                UpdateNowPlayingMenu(null, null);
                UpdateSyncMenu(null);
                ShowStatus("等待网易云音乐播放…");
                return;
            }

            var key = $"{snapshot.Title}\n{snapshot.Artist}";
            if (snapshot.HasReliablePosition)
            {
                _timelineReliable = true;
                UpdatePlaybackClock(snapshot, key);
            }
            else
            {
                _timelineReliable = false;
                _clockInitialized = false;
            }
            UpdateNowPlayingMenu(snapshot.Title, snapshot.Artist);
            UpdateSyncMenu(snapshot);
            var songChanged = !string.Equals(key, _songKey, StringComparison.Ordinal);
            if (songChanged)
            {
                _songKey = key;
                _lyrics = [];
                _activeLineIndex = int.MinValue;
                _nextLyricsRetry = DateTimeOffset.MinValue;
                ClearAllLyrics();
                SetTitle(snapshot.Title, snapshot.Artist);
                BeginLyricsLoad(snapshot.Title, snapshot.Artist, key);
                return;
            }

            if (_lyrics.Count == 0 && !_lyricsLoading && DateTimeOffset.Now >= _nextLyricsRetry)
                BeginLyricsLoad(snapshot.Title, snapshot.Artist, key);

            if (_lyrics.Count > 0)
            {
                if (_timelineReliable) EnsureActiveLine(CurrentPlaybackPosition());
                else ShowTimelineWaiting();
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("OverlayPoll", exception);
            _media.InvalidateSession();
            _timelineReliable = false;
            _clockInitialized = false;
            UpdateNowPlayingMenu(null, null);
            UpdateSyncMenu(null);
            ShowStatus("媒体连接暂时失效，正在自动重连…");
        }
        finally
        {
            _pollRunning = false;
        }
    }

    private void BeginLyricsLoad(string title, string artist, string expectedSongKey)
    {
        if (_lyricsLoading && string.Equals(expectedSongKey, _lyricsLoadingSongKey, StringComparison.Ordinal)) return;
        CancelLyricsLoad();
        var cancellation = new CancellationTokenSource();
        _lyricsCancellation = cancellation;
        _lyricsLoading = true;
        _lyricsLoadingSongKey = expectedSongKey;
        ShowStatus("正在获取歌词…", keepTitle: true);
        _ = LoadLyricsAsync(title, artist, expectedSongKey, cancellation);
    }

    private async Task LoadLyricsAsync(
        string title,
        string artist,
        string expectedSongKey,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await _api.GetLyricsAsync(title, artist, cancellation.Token);
            if (cancellation.IsCancellationRequested || !string.Equals(_songKey, expectedSongKey, StringComparison.Ordinal))
                return;

            _lyrics = result;
            _activeLineIndex = int.MinValue;
            _nextLyricsRetry = DateTimeOffset.Now.AddSeconds(result.Count == 0 ? 30 : 5);
            if (result.Count == 0) ShowStatus("暂无歌词", keepTitle: true);
            else if (_timelineReliable && _clockInitialized) EnsureActiveLine(CurrentPlaybackPosition());
            else ShowTimelineWaiting();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (string.Equals(_songKey, expectedSongKey, StringComparison.Ordinal))
            {
                _lyrics = [];
                _nextLyricsRetry = DateTimeOffset.Now.AddSeconds(10);
                ShowStatus("歌词获取失败，稍后自动重试", keepTitle: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_lyricsCancellation, cancellation))
            {
                _lyricsCancellation = null;
                _lyricsLoading = false;
                _lyricsLoadingSongKey = string.Empty;
            }
            cancellation.Dispose();
        }
    }

    private void AnimationTick()
    {
        if (_lyrics.Count == 0 || !_clockInitialized || !_timelineReliable) return;
        var position = CurrentPlaybackPosition();
        if (_demoMode && position > _lyrics[^1].Time + TimeSpan.FromSeconds(3.5))
        {
            _clockAnchorPosition = TimeSpan.Zero;
            _clockAnchorTime = DateTimeOffset.UtcNow;
            _activeLineIndex = int.MinValue;
            ClearAllLyrics();
            position = TimeSpan.Zero;
        }

        EnsureActiveLine(position);
        if (IsPerformanceMode()) AnimatePerformance(position);
        else AnimateStandard(position);
    }

    private void UpdatePlaybackClock(PlaybackSnapshot snapshot, string songKey)
    {
        var now = DateTimeOffset.UtcNow;
        if (!_clockInitialized || !string.Equals(songKey, _clockSongKey, StringComparison.Ordinal))
        {
            _clockInitialized = true;
            _clockSongKey = songKey;
            _clockAnchorPosition = snapshot.Position;
            _clockAnchorTime = now;
            _lastRawPosition = snapshot.Position;
            _clockPlaying = snapshot.IsPlaying;
            return;
        }

        var predicted = RawClockPosition(now);
        var rawChanged = Math.Abs((snapshot.Position - _lastRawPosition).TotalMilliseconds) >= 250;
        var drift = Math.Abs((snapshot.Position - predicted).TotalSeconds);
        if (snapshot.IsPlaying)
        {
            if (!_clockPlaying)
            {
                _clockAnchorPosition = rawChanged && drift < 3 ? snapshot.Position : predicted;
                _clockAnchorTime = now;
            }
            else if (rawChanged && drift >= 1.2)
            {
                _clockAnchorPosition = snapshot.Position;
                _clockAnchorTime = now;
            }
        }
        else if (_clockPlaying)
        {
            _clockAnchorPosition = rawChanged && drift < 3 ? snapshot.Position : predicted;
            _clockAnchorTime = now;
        }
        else if (rawChanged)
        {
            _clockAnchorPosition = snapshot.Position;
            _clockAnchorTime = now;
        }

        _lastRawPosition = snapshot.Position;
        _clockPlaying = snapshot.IsPlaying;
    }

    private TimeSpan CurrentPlaybackPosition()
    {
        var position = RawClockPosition(DateTimeOffset.UtcNow) +
                       TimeSpan.FromMilliseconds(_config.LyricOffsetMilliseconds);
        return position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    private TimeSpan RawClockPosition(DateTimeOffset now) =>
        _clockAnchorPosition + (_clockPlaying ? now - _clockAnchorTime : TimeSpan.Zero);

    private void EnsureActiveLine(TimeSpan position)
    {
        var index = FindLineIndex(position);
        if (index == _activeLineIndex) return;
        if (index < 0)
        {
            _activeLineIndex = -1;
            ShowStatus("♪", keepTitle: true, preserveLineIndex: true);
            return;
        }

        _activeLineIndex = index;
        _activeLineStart = _lyrics[index].Time;
        var minimumDuration = TimeSpan.FromSeconds(Math.Max(2.2, GetDisplayText(_lyrics[index]).Length / 9.0));
        _activeLineEnd = index + 1 < _lyrics.Count
            ? _lyrics[index + 1].Time
            : _activeLineStart + minimumDuration;
        if (_activeLineEnd - _activeLineStart < TimeSpan.FromMilliseconds(650))
            _activeLineEnd = _activeLineStart + TimeSpan.FromMilliseconds(650);

        if (IsPerformanceMode()) BeginPerformanceLine(index);
        else BeginStandardLine(index);
    }

    private int FindLineIndex(TimeSpan position)
    {
        var low = 0;
        var high = _lyrics.Count - 1;
        var answer = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (_lyrics[middle].Time <= position)
            {
                answer = middle;
                low = middle + 1;
            }
            else high = middle - 1;
        }
        return answer;
    }

    private void BeginStandardLine(int index)
    {
        _performanceCanvas.Visibility = Visibility.Collapsed;
        _standardPanel.Visibility = Visibility.Visible;
        _status.Visibility = Visibility.Collapsed;
        _standardGlyphViewbox.Visibility = Visibility.Visible;
        _standardGlyphs.Clear();
        _standardGlyphPanel.Children.Clear();
        CreateGlyphs(_standardGlyphPanel, _standardGlyphs, GetDisplayText(_lyrics[index]), index);
        _previous.Text = _config.ShowPreviousLine && index > 0 ? GetDisplayText(_lyrics[index - 1]) : string.Empty;
        _next.Text = _config.ShowNextLine && index + 1 < _lyrics.Count ? GetDisplayText(_lyrics[index + 1]) : string.Empty;
        AnimateStandard(CurrentPlaybackPosition());
    }

    private void AnimateStandard(TimeSpan position)
    {
        if (_standardGlyphs.Count == 0 || _activeLineIndex < 0) return;
        var timing = CalculateTiming(position, _activeLineStart, _activeLineEnd, _standardGlyphs.Count);
        AnimateGlyphs(_standardGlyphs, timing, useIndividualDrift: true);
        _previous.Opacity = timing.GroupOpacity * 0.55;
        _next.Opacity = timing.GroupOpacity * 0.55;
    }

    private void BeginPerformanceLine(int index)
    {
        _standardPanel.Visibility = Visibility.Collapsed;
        _performanceCanvas.Visibility = Visibility.Visible;
        while (_performanceLines.Count >= 2)
        {
            _performanceCanvas.Children.Remove(_performanceLines[0].Container);
            _performanceLines.RemoveAt(0);
        }

        var text = GetDisplayText(_lyrics[index]);
        var glyphPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var glyphs = new List<AnimatedGlyph>();
        CreateGlyphs(glyphPanel, glyphs, text, index);
        var viewbox = NewViewbox();
        viewbox.MaxWidth = Math.Max(300, SystemParameters.PrimaryScreenWidth * 0.72);
        viewbox.MaxHeight = ScaledCurrentFontSize * 1.8;
        viewbox.Child = glyphPanel;
        var container = new Grid { Opacity = 1 };
        container.Children.Add(viewbox);

        var random = new Random(HashCode.Combine(_songKey, index, text));
        var estimatedWidth = Math.Min(SystemParameters.PrimaryScreenWidth * 0.72,
            Math.Max(180, text.Length * ScaledCurrentFontSize * 0.72));
        var maxX = Math.Max(30, SystemParameters.PrimaryScreenWidth - estimatedWidth - 40);
        var x = 30 + random.NextDouble() * Math.Max(1, maxX - 30);
        var minY = SystemParameters.PrimaryScreenHeight * 0.13;
        var maxY = SystemParameters.PrimaryScreenHeight * 0.78;
        var y = minY + random.NextDouble() * Math.Max(1, maxY - minY);
        if (_performanceLines.Count > 0 && Math.Abs(y - _performanceLines[^1].BaseY) < 130)
            y = y + 180 < maxY ? y + 180 : Math.Max(minY, y - 180);

        Canvas.SetLeft(container, x);
        Canvas.SetTop(container, y);
        var direction = random.Next(0, 2) == 0 ? -1 : 1;
        var baseAngle = (random.NextDouble() * 2 - 1) * _config.PerformanceTiltDegrees;
        var rotate = new RotateTransform(baseAngle);
        var translate = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(rotate);
        transforms.Children.Add(translate);
        container.RenderTransformOrigin = new Point(0.5, 0.5);
        container.RenderTransform = transforms;

        var visual = new PerformanceLineVisual(
            index, container, glyphs, _activeLineStart, _activeLineEnd, y,
            direction, baseAngle, rotate, translate);
        _performanceLines.Add(visual);
        _performanceCanvas.Children.Add(container);
        AnimatePerformance(CurrentPlaybackPosition());
    }

    private void AnimatePerformance(TimeSpan position)
    {
        if (_performanceLines.Count == 0) return;
        var current = _performanceLines[^1];
        var currentDuration = Math.Max(0.65, (current.End - current.Start).TotalSeconds);
        var currentProgress = Math.Clamp((position - current.Start).TotalSeconds / currentDuration, 0, 1);
        var motionTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        for (var i = 0; i < _performanceLines.Count; i++)
        {
            var line = _performanceLines[i];
            var isCurrent = ReferenceEquals(line, current);
            var opacity = 1.0;
            if (!isCurrent)
            {
                var fadeStart = Math.Clamp(_config.PerformancePreviousFadeStart, 0.1, 0.95);
                opacity = currentProgress <= fadeStart
                    ? 1
                    : 1 - Math.Clamp((currentProgress - fadeStart) / (1 - fadeStart), 0, 1);
            }
            else if (line.LineIndex == _lyrics.Count - 1 && position > line.End)
            {
                opacity = 1 - Math.Clamp((position - line.End).TotalSeconds / 0.9, 0, 1);
            }

            var duration = Math.Max(0.65, (line.End - line.Start).TotalSeconds);
            var progress = Math.Clamp((position - line.Start).TotalSeconds / duration, 0, 1);
            var timing = CalculatePerformanceTiming(position, line.Start, line.End, line.Glyphs.Count);
            AnimateGlyphs(line.Glyphs, timing, useIndividualDrift: false);
            line.Container.Opacity = opacity;
            line.Translate.X = Math.Sin(motionTime * 0.8 + line.LineIndex) * 1.5;
            line.Translate.Y = line.Direction * _config.PerformanceRisePixels * progress +
                               Math.Sin(motionTime * 1.1 + line.LineIndex * 0.7) * 1.2;
            line.Rotate.Angle = line.BaseAngle + line.Direction * 0.65 * progress +
                                Math.Sin(motionTime * 0.7 + line.LineIndex) * 0.12;
        }
    }

    private AnimationTiming CalculateTiming(
        TimeSpan position,
        TimeSpan start,
        TimeSpan end,
        int glyphCount,
        double opacityMultiplier = 1)
    {
        var duration = Math.Max(0.65, (end - start).TotalSeconds);
        var local = Math.Clamp((position - start).TotalSeconds, 0, duration);
        var desiredReveal = glyphCount / Math.Max(1, _config.CharactersPerSecond);
        var revealDuration = Math.Min(duration * 0.62, Math.Max(0.45, desiredReveal));
        var fadeDuration = Math.Min(_config.LineFadeOutMilliseconds / 1000.0, duration * 0.34);
        var fadeStart = Math.Max(revealDuration, duration - fadeDuration);
        var groupOpacity = fadeDuration <= 0 || local <= fadeStart
            ? 1
            : 1 - Math.Clamp((local - fadeStart) / Math.Max(0.05, duration - fadeStart), 0, 1);
        return new AnimationTiming(
            local,
            duration,
            revealDuration,
            groupOpacity * opacityMultiplier,
            Math.Clamp(local / duration, 0, 1));
    }

    private AnimationTiming CalculatePerformanceTiming(
        TimeSpan position,
        TimeSpan start,
        TimeSpan end,
        int glyphCount)
    {
        var duration = Math.Max(0.65, (end - start).TotalSeconds);
        var local = Math.Clamp((position - start).TotalSeconds, 0, duration);
        var desiredReveal = glyphCount / Math.Max(1, _config.CharactersPerSecond);
        var revealDuration = Math.Min(duration * 0.62, Math.Max(0.45, desiredReveal));
        return new AnimationTiming(
            local,
            duration,
            revealDuration,
            1,
            Math.Clamp(local / duration, 0, 1));
    }

    private void AnimateGlyphs(List<AnimatedGlyph> glyphs, AnimationTiming timing, bool useIndividualDrift)
    {
        var stagger = timing.RevealDuration / Math.Max(1, glyphs.Count);
        var fadeIn = Math.Max(0.06, _config.CharacterFadeInMilliseconds / 1000.0);
        var motionTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        for (var i = 0; i < glyphs.Count; i++)
        {
            var item = glyphs[i];
            var appear = Math.Clamp((timing.Local - i * stagger) / fadeIn, 0, 1);
            var eased = 1 - Math.Pow(1 - appear, 3);
            var jitterStrength = _config.CharacterJitterPixels * eased;
            var jitterX = Math.Sin(motionTime * item.Frequency * 0.73 + item.Phase) * jitterStrength * 0.55;
            var jitterY = Math.Sin(motionTime * item.Frequency + item.Phase * 1.31) * jitterStrength;
            var drift = useIndividualDrift
                ? item.Direction * _config.CharacterDriftPixels * (timing.Progress - 0.5)
                : 0;
            var entrance = -item.Direction * _config.CharacterDriftPixels * 1.25 * (1 - eased);
            item.Text.Opacity = eased * timing.GroupOpacity;
            item.Translate.X = jitterX;
            item.Translate.Y = drift + entrance + jitterY;
            item.Rotate.Angle = Math.Sin(motionTime * item.Frequency * 0.41 + item.Phase) *
                                _config.CharacterJitterPixels * 0.25 * eased;
        }
    }

    private void CreateGlyphs(StackPanel panel, List<AnimatedGlyph> target, string text, int lineIndex)
    {
        var elements = EnumerateTextElements(text);
        for (var i = 0; i < elements.Count; i++)
        {
            var translate = new TranslateTransform();
            var rotate = new RotateTransform();
            var transforms = new TransformGroup();
            transforms.Children.Add(rotate);
            transforms.Children.Add(translate);
            var glyph = new TextBlock
            {
                Text = elements[i] == " " ? "\u00A0" : elements[i],
                FontFamily = _activeFont,
                FontSize = ScaledCurrentFontSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = ParseBrush(_config.CurrentColor, Colors.White),
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transforms,
                Effect = CreateShadow()
            };
            TextOptions.SetTextRenderingMode(glyph, TextRenderingMode.Grayscale);
            panel.Children.Add(glyph);
            var seed = unchecked(lineIndex * 397 + i * 97 + text.Length * 17);
            target.Add(new AnimatedGlyph(
                glyph, translate, rotate, seed % 2 == 0 ? -1 : 1,
                6.4 + Math.Abs(seed % 17) * 0.17,
                Math.Abs(seed % 101) / 101.0 * Math.PI * 2));
        }
    }

    private string GetDisplayText(LyricLine line)
    {
        if (string.Equals(_config.LyricLanguage, "Translation", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(line.Translation)
                ? (string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text)
                : line.Translation!;
        return string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text;
    }

    private static List<string> EnumerateTextElements(string text)
    {
        var result = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) result.Add(enumerator.GetTextElement());
        return result;
    }

    private void SetTitle(string title, string artist)
    {
        _title.Text = _config.ShowSongTitle
            ? _config.TitleFormat.Replace("{title}", title).Replace("{artist}", artist)
            : string.Empty;
    }

    private void ShowStatus(string text, bool keepTitle = false, bool preserveLineIndex = false)
    {
        if (!keepTitle) _title.Text = string.Empty;
        if (!preserveLineIndex) _activeLineIndex = int.MinValue;
        ClearAllLyrics();
        _standardPanel.Visibility = Visibility.Visible;
        _performanceCanvas.Visibility = Visibility.Collapsed;
        _standardGlyphViewbox.Visibility = Visibility.Collapsed;
        _status.Visibility = Visibility.Visible;
        _status.Text = text;
        _status.Opacity = 1;
    }

    private void ShowTimelineWaiting() => ShowStatus(
        "已检测到网易云，正在寻找实时播放进度…\n首次同步请保持歌曲播放数秒",
        keepTitle: true);

    private void ClearAllLyrics()
    {
        _standardGlyphs.Clear();
        _standardGlyphPanel.Children.Clear();
        _previous.Text = string.Empty;
        _next.Text = string.Empty;
        _performanceLines.Clear();
        _performanceCanvas.Children.Clear();
    }

    private void ReloadConfig()
    {
        _config = OverlayConfig.Load();
        _activeFont = ImportedFontManager.Resolve(_config);
        ApplyWindowMode();
        Opacity = Math.Clamp(_config.LyricOpacity, 0.0, 1.0);
        UpdateInteractionMode();

        var contextBrush = ParseBrush(_config.ContextColor, Colors.White);
        foreach (var block in new[] { _title, _previous, _next, _status })
        {
            block.FontFamily = _activeFont;
            block.Effect = CreateShadow();
        }
        ApplyConfiguredFontSizes();
        _status.FontWeight = FontWeights.SemiBold;
        _title.Foreground = contextBrush;
        _previous.Foreground = contextBrush;
        _next.Foreground = contextBrush;
        _status.Foreground = ParseBrush(_config.CurrentColor, Colors.White);

        if (_pollTimer is not null)
            _pollTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_config.PollIntervalMilliseconds, 100, 2000));
        if (_animationTimer is not null)
            _animationTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(_config.AnimationFps, 15, 60));
        RefreshMenuChecks();
        _activeLineIndex = int.MinValue;
        ClearAllLyrics();
        if (_lyrics.Count > 0 && _clockInitialized) EnsureActiveLine(CurrentPlaybackPosition());
    }

    private void ApplyWindowMode()
    {
        if (IsPerformanceMode())
        {
            Left = 0;
            Top = 0;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
        }
        else
        {
            Width = Math.Min(SystemParameters.PrimaryScreenWidth, Math.Max(300, _config.Width));
            Height = Math.Min(SystemParameters.PrimaryScreenHeight, Math.Max(100, _config.Height));
            Top = _config.Top >= 0
                ? Math.Clamp(_config.Top, 0, Math.Max(0, SystemParameters.PrimaryScreenHeight - Height))
                : Math.Max(0, SystemParameters.PrimaryScreenHeight * 0.64 - Height / 2);
            Left = _config.Left >= 0
                ? Math.Clamp(_config.Left, 0, Math.Max(0, SystemParameters.PrimaryScreenWidth - Width))
                : Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
        }
    }

    private bool IsPerformanceMode() =>
        string.Equals(_config.DisplayMode, "Performance", StringComparison.OrdinalIgnoreCase);

    private System.Windows.Media.Effects.DropShadowEffect CreateShadow() => new()
    {
        Color = ((SolidColorBrush)ParseBrush(_config.ShadowColor, Colors.Black)).Color,
        BlurRadius = 8,
        ShadowDepth = 2,
        Opacity = 1
    };

    private Forms.NotifyIcon BuildTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _nowPlayingItem = new Forms.ToolStripMenuItem("正在播放：未检测到歌曲") { Enabled = false };
        menu.Items.Add(_nowPlayingItem);
        _syncStatusItem = new Forms.ToolStripMenuItem("同步：尚未连接") { Enabled = false };
        menu.Items.Add(_syncStatusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var languageMenu = new Forms.ToolStripMenuItem("歌词语言");
        _originalLanguageItem = new Forms.ToolStripMenuItem("原文", null, (_, _) => SetLyricLanguage("Original"));
        _translationLanguageItem = new Forms.ToolStripMenuItem("翻译", null, (_, _) => SetLyricLanguage("Translation"));
        languageMenu.DropDownItems.Add(_originalLanguageItem);
        languageMenu.DropDownItems.Add(_translationLanguageItem);
        menu.Items.Add(languageMenu);

        var modeMenu = new Forms.ToolStripMenuItem("显示模式");
        _standardModeItem = new Forms.ToolStripMenuItem("标准模式", null, (_, _) => SetDisplayMode("Standard"));
        _performanceModeItem = new Forms.ToolStripMenuItem("演出模式", null, (_, _) => SetDisplayMode("Performance"));
        modeMenu.DropDownItems.Add(_standardModeItem);
        modeMenu.DropDownItems.Add(_performanceModeItem);
        menu.Items.Add(modeMenu);

        _positionLockItem = new Forms.ToolStripMenuItem(
            "锁定歌词位置", null, (_, _) => Dispatcher.Invoke(TogglePositionLock));
        menu.Items.Add(_positionLockItem);

        var appearanceMenu = new Forms.ToolStripMenuItem("字体与颜色");
        appearanceMenu.DropDownItems.Add("导入字体…", null, (_, _) => ImportFont());
        appearanceMenu.DropDownItems.Add("恢复默认字体", null, (_, _) => ResetFont());
        appearanceMenu.DropDownItems.Add("选择歌词颜色…", null, (_, _) => ChooseLyricColor());
        var fontScaleMenu = new Forms.ToolStripMenuItem("整体字体大小");
        _fontScaleSlider = new Forms.TrackBar
        {
            Minimum = 10,
            Maximum = 200,
            Value = 100,
            TickFrequency = 25,
            SmallChange = 1,
            LargeChange = 10,
            AutoSize = false,
            Width = 220,
            Height = 48
        };
        _fontScaleSlider.ValueChanged += (_, _) =>
        {
            if (!_updatingFontScaleSlider)
                Dispatcher.Invoke(() => SetFontScale(_fontScaleSlider.Value));
        };
        fontScaleMenu.DropDownItems.Add(new Forms.ToolStripControlHost(_fontScaleSlider)
        {
            AutoSize = false,
            Width = 230,
            Height = 50,
            Margin = new Forms.Padding(4, 2, 4, 2)
        });
        _fontScaleText = new Forms.TextBox
        {
            Text = "100",
            Width = 82,
            TextAlign = Forms.HorizontalAlignment.Right
        };
        _fontScaleText.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode != Forms.Keys.Enter) return;
            Dispatcher.Invoke(CommitFontScaleText);
            eventArgs.SuppressKeyPress = true;
        };
        _fontScaleText.Validated += (_, _) => Dispatcher.Invoke(CommitFontScaleText);
        fontScaleMenu.DropDownItems.Add(NewPercentageInputHost(_fontScaleText));
        fontScaleMenu.DropDownOpening += (_, _) => Dispatcher.Invoke(RefreshMenuChecks);
        appearanceMenu.DropDownItems.Add(fontScaleMenu);
        var opacityMenu = new Forms.ToolStripMenuItem("歌词透明度");
        _opacitySlider = new Forms.TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 100,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            AutoSize = false,
            Width = 220,
            Height = 48
        };
        _opacitySlider.ValueChanged += (_, _) =>
        {
            if (!_updatingOpacitySlider)
                Dispatcher.Invoke(() => SetLyricOpacity(_opacitySlider.Value));
        };
        opacityMenu.DropDownItems.Add(new Forms.ToolStripControlHost(_opacitySlider)
        {
            AutoSize = false,
            Width = 230,
            Height = 50,
            Margin = new Forms.Padding(4, 2, 4, 2)
        });
        _opacityNumber = NewPercentageInput(0, 100, 100);
        _opacityNumber.ValueChanged += (_, _) =>
        {
            if (!_updatingOpacitySlider)
                Dispatcher.Invoke(() => SetLyricOpacity((int)_opacityNumber.Value));
        };
        opacityMenu.DropDownItems.Add(NewPercentageInputHost(_opacityNumber));
        opacityMenu.DropDownOpening += (_, _) => Dispatcher.Invoke(RefreshMenuChecks);
        appearanceMenu.DropDownItems.Add(opacityMenu);
        menu.Items.Add(appearanceMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("动画效果预览", null, (_, _) => Dispatcher.Invoke(StartDemo));
        menu.Items.Add("返回网易云同步", null, (_, _) => Dispatcher.Invoke(ReturnToNetease));
        menu.Items.Add("歌词提前 0.5 秒", null, (_, _) => Dispatcher.Invoke(() => ShiftLyricOffset(500)));
        menu.Items.Add("歌词延后 0.5 秒", null, (_, _) => Dispatcher.Invoke(() => ShiftLyricOffset(-500)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("重新加载配置", null, (_, _) => Dispatcher.Invoke(ReloadConfig));
        menu.Items.Add("打开配置文件", null, (_, _) => OpenConfig());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));

        return new Forms.NotifyIcon
        {
            Text = "网易云歌词悬浮层",
            Icon = System.Drawing.SystemIcons.Information,
            ContextMenuStrip = menu,
            Visible = true
        };
    }

    private void UpdateNowPlayingMenu(string? title, string? artist)
    {
        var text = string.IsNullOrWhiteSpace(title)
            ? "正在播放：未检测到歌曲"
            : $"正在播放：{title} · {artist}";
        if (_nowPlayingItem is not null) _nowPlayingItem.Text = text;
        var tooltip = string.IsNullOrWhiteSpace(title) ? "网易云歌词悬浮层" : $"{title} - {artist}";
        _tray.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void UpdateSyncMenu(PlaybackSnapshot? snapshot)
    {
        if (_syncStatusItem is null) return;
        if (snapshot is null)
        {
            _syncStatusItem.Text = "同步：尚未连接";
            return;
        }

        _syncStatusItem.Text = snapshot.HasReliablePosition
            ? $"同步：{snapshot.PositionSource} {FormatClock(snapshot.Position)} / {FormatClock(snapshot.EndTime)}"
            : "同步：等待网易云主窗口进度";
    }

    private static string FormatClock(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private void SetLyricLanguage(string language)
    {
        _config.LyricLanguage = language;
        _config.Save();
        RefreshMenuChecks();
        _activeLineIndex = int.MinValue;
        ClearAllLyrics();
        if (_lyrics.Count > 0 && _clockInitialized) EnsureActiveLine(CurrentPlaybackPosition());
    }

    private void SetDisplayMode(string mode)
    {
        _config.DisplayMode = mode;
        _config.Save();
        ApplyWindowMode();
        UpdateInteractionMode();
        RefreshMenuChecks();
        _activeLineIndex = int.MinValue;
        ClearAllLyrics();
        if (_lyrics.Count > 0 && _clockInitialized) EnsureActiveLine(CurrentPlaybackPosition());
    }

    private void RefreshMenuChecks()
    {
        if (_originalLanguageItem is not null)
            _originalLanguageItem.Checked = !string.Equals(_config.LyricLanguage, "Translation", StringComparison.OrdinalIgnoreCase);
        if (_translationLanguageItem is not null)
            _translationLanguageItem.Checked = string.Equals(_config.LyricLanguage, "Translation", StringComparison.OrdinalIgnoreCase);
        if (_standardModeItem is not null) _standardModeItem.Checked = !IsPerformanceMode();
        if (_performanceModeItem is not null) _performanceModeItem.Checked = IsPerformanceMode();
        if (_positionLockItem is not null)
        {
            _positionLockItem.Checked = _config.PositionLocked;
            _positionLockItem.Enabled = !IsPerformanceMode();
            _positionLockItem.Text = _config.PositionLocked
                ? "锁定歌词位置（已锁定）"
                : "锁定歌词位置（可拖动）";
        }
        var opacityPercentage = (int)Math.Round(Math.Clamp(_config.LyricOpacity, 0.0, 1.0) * 100);
        if (_opacitySlider is not null)
        {
            _updatingOpacitySlider = true;
            _opacitySlider.Value = opacityPercentage;
            if (_opacityNumber is not null) _opacityNumber.Value = opacityPercentage;
            _updatingOpacitySlider = false;
        }
        var fontScalePercentage = Math.Max(0.01, _config.FontScale * 100);
        if (_fontScaleSlider is not null)
        {
            _updatingFontScaleSlider = true;
            _fontScaleSlider.Value = (int)Math.Round(Math.Clamp(fontScalePercentage, 10, 200));
            if (_fontScaleText is not null && !_fontScaleText.Focused)
                _fontScaleText.Text = FormatPercentage(fontScalePercentage);
            _updatingFontScaleSlider = false;
        }
    }

    private void TogglePositionLock()
    {
        if (IsPerformanceMode()) return;
        _config.PositionLocked = !_config.PositionLocked;
        _config.Save();
        UpdateInteractionMode();
        RefreshMenuChecks();
    }

    private void SetLyricOpacity(int percentage)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        _updatingOpacitySlider = true;
        if (_opacitySlider is not null && _opacitySlider.Value != percentage)
            _opacitySlider.Value = percentage;
        if (_opacityNumber is not null && _opacityNumber.Value != percentage)
            _opacityNumber.Value = percentage;
        _updatingOpacitySlider = false;
        _config.LyricOpacity = percentage / 100.0;
        Opacity = _config.LyricOpacity;
        _config.Save();
    }

    private void SetFontScale(double percentage)
    {
        if (!double.IsFinite(percentage) || percentage <= 0) return;
        _updatingFontScaleSlider = true;
        var sliderValue = (int)Math.Round(Math.Clamp(percentage, 10, 200));
        if (_fontScaleSlider is not null && _fontScaleSlider.Value != sliderValue)
            _fontScaleSlider.Value = sliderValue;
        if (_fontScaleText is not null)
            _fontScaleText.Text = FormatPercentage(percentage);
        _updatingFontScaleSlider = false;
        _config.FontScale = percentage / 100.0;
        _config.Save();
        ApplyConfiguredFontSizes();
        _activeLineIndex = int.MinValue;
        ClearAllLyrics();
        if (_lyrics.Count > 0 && _clockInitialized) EnsureActiveLine(CurrentPlaybackPosition());
    }

    private void CommitFontScaleText()
    {
        if (_fontScaleText is null || _updatingFontScaleSlider) return;
        var input = _fontScaleText.Text.Trim().TrimEnd('%').Trim();
        if ((double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out var percentage) ||
             double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out percentage)) &&
            double.IsFinite(percentage) && percentage > 0)
        {
            SetFontScale(percentage);
            return;
        }

        _fontScaleText.Text = FormatPercentage(Math.Max(0.01, _config.FontScale * 100));
    }

    private static string FormatPercentage(double percentage) =>
        percentage.ToString("0.##", CultureInfo.CurrentCulture);

    private double ScaledCurrentFontSize =>
        SafeFontSize(_config.CurrentFontSize * EffectiveFontScale);

    private double ScaledContextFontSize =>
        SafeFontSize(_config.ContextFontSize * EffectiveFontScale);

    private double EffectiveFontScale =>
        double.IsFinite(_config.FontScale) && _config.FontScale > 0 ? _config.FontScale : 1.0;

    private static double SafeFontSize(double value) => Math.Clamp(value, 0.1, 35_000);

    private void ApplyConfiguredFontSizes()
    {
        _title.FontSize = SafeFontSize((_config.ContextFontSize - 2) * EffectiveFontScale);
        _previous.FontSize = ScaledContextFontSize;
        _next.FontSize = ScaledContextFontSize;
        _status.FontSize = ScaledCurrentFontSize;
    }

    private static Forms.NumericUpDown NewPercentageInput(int minimum, int maximum, int value) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = value,
        Increment = 1,
        DecimalPlaces = 0,
        Width = 82,
        TextAlign = Forms.HorizontalAlignment.Right,
        ThousandsSeparator = false
    };

    private static Forms.ToolStripControlHost NewPercentageInputHost(Forms.Control input)
    {
        var panel = new Forms.FlowLayoutPanel
        {
            AutoSize = false,
            Width = 230,
            Height = 34,
            WrapContents = false,
            FlowDirection = Forms.FlowDirection.LeftToRight,
            Padding = new Forms.Padding(8, 4, 4, 2)
        };
        panel.Controls.Add(new Forms.Label
        {
            Text = "百分比：",
            AutoSize = true,
            Margin = new Forms.Padding(0, 4, 2, 0)
        });
        panel.Controls.Add(input);
        panel.Controls.Add(new Forms.Label
        {
            Text = "%",
            AutoSize = true,
            Margin = new Forms.Padding(2, 4, 0, 0)
        });
        return new Forms.ToolStripControlHost(panel)
        {
            AutoSize = false,
            Width = 230,
            Height = 36,
            Margin = new Forms.Padding(4, 0, 4, 3)
        };
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsPerformanceMode() || _config.PositionLocked || e.ChangedButton != MouseButton.Left) return;
        try
        {
            DragMove();
            _config.Left = Left;
            _config.Top = Top;
            _config.Save();
        }
        catch (InvalidOperationException)
        {
            // The mouse button may be released between the event and DragMove.
        }
    }

    private void ImportFont()
    {
        using var dialog = new Forms.OpenFileDialog
        {
            Title = "导入歌词字体",
            Filter = "字体文件 (*.ttf;*.otf)|*.ttf;*.otf|所有文件 (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        if (!ImportedFontManager.TryImport(dialog.FileName, out var path, out var family, out var error))
        {
            System.Windows.MessageBox.Show(error, "字体导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _config.CustomFontPath = path;
        _config.FontFamily = family;
        _config.Save();
        ReloadConfig();
    }

    private void ResetFont()
    {
        _config.CustomFontPath = string.Empty;
        _config.FontFamily = "Microsoft YaHei UI";
        _config.Save();
        ReloadConfig();
    }

    private void ChooseLyricColor()
    {
        using var dialog = new Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            var color = ((SolidColorBrush)ParseBrush(_config.CurrentColor, Colors.White)).Color;
            dialog.Color = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        catch
        {
        }
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _config.CurrentColor = $"#FF{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        _config.Save();
        ReloadConfig();
    }

    private void OpenConfig()
    {
        if (!File.Exists(OverlayConfig.ConfigPath)) new OverlayConfig().Save();
        Process.Start(new ProcessStartInfo(OverlayConfig.ConfigPath) { UseShellExecute = true });
    }

    private void StartDemo()
    {
        _demoMode = true;
        CancelLyricsLoad();
        _lyricsLoading = false;
        _lyricsLoadingSongKey = string.Empty;
        _songKey = "__performance_demo__";
        _lyrics =
        [
            new LyricLine(TimeSpan.FromSeconds(0.25), "在沉默里，点燃最后一束光", "Light the final flame in silence"),
            new LyricLine(TimeSpan.FromSeconds(4.2), "让每一个字，从深渊中醒来", "Let every word awaken from the abyss"),
            new LyricLine(TimeSpan.FromSeconds(8.3), "坠落，然后继续前行", "Fall — then keep moving forward"),
            new LyricLine(TimeSpan.FromSeconds(12.0), "直到舞台的灯光熄灭", null)
        ];
        _clockInitialized = true;
        _timelineReliable = true;
        _clockSongKey = _songKey;
        _clockAnchorPosition = TimeSpan.Zero;
        _clockAnchorTime = DateTimeOffset.UtcNow;
        _lastRawPosition = TimeSpan.Zero;
        _clockPlaying = true;
        _activeLineIndex = int.MinValue;
        ClearAllLyrics();
        UpdateNowPlayingMenu("演出动画预览", string.Empty);
        EnsureActiveLine(TimeSpan.Zero);
    }

    private async void ReturnToNetease()
    {
        _demoMode = false;
        _songKey = string.Empty;
        _lyrics = [];
        _clockInitialized = false;
        _timelineReliable = false;
        _activeLineIndex = int.MinValue;
        _nextLyricsRetry = DateTimeOffset.MinValue;
        ClearAllLyrics();
        await PollMediaAsync();
    }

    private void ShiftLyricOffset(int deltaMilliseconds)
    {
        _config.LyricOffsetMilliseconds = Math.Clamp(
            _config.LyricOffsetMilliseconds + deltaMilliseconds, -10000, 10000);
        _config.Save();
        _activeLineIndex = int.MinValue;
        ClearAllLyrics();
    }

    private static Viewbox NewViewbox() => new()
    {
        Stretch = Stretch.Uniform,
        StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock NewTextBlock() => new()
    {
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
        Margin = new Thickness(8, 1, 8, 2)
    };

    private static System.Windows.Media.Brush ParseBrush(string value, System.Windows.Media.Color fallback)
    {
        try { return (System.Windows.Media.Brush)new BrushConverter().ConvertFromString(value)!; }
        catch { return new SolidColorBrush(fallback); }
    }

    private void UpdateInteractionMode()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        style |= WsExToolwindow;
        var clickThrough = IsPerformanceMode() || _config.PositionLocked;
        if (clickThrough)
            style |= WsExTransparent | WsExNoactivate;
        else
            style &= ~(WsExTransparent | WsExNoactivate);
        SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(style));
        Cursor = clickThrough ? null : Cursors.SizeAll;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        _pollTimer?.Stop();
        _animationTimer?.Stop();
        CancelLyricsLoad();
        _media.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
    }

    private void CancelLyricsLoad()
    {
        var cancellation = _lyricsCancellation;
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException)
        {
            if (ReferenceEquals(_lyricsCancellation, cancellation)) _lyricsCancellation = null;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private sealed record AnimatedGlyph(
        TextBlock Text,
        TranslateTransform Translate,
        RotateTransform Rotate,
        int Direction,
        double Frequency,
        double Phase);

    private sealed class PerformanceLineVisual(
        int lineIndex,
        Grid container,
        List<AnimatedGlyph> glyphs,
        TimeSpan start,
        TimeSpan end,
        double baseY,
        int direction,
        double baseAngle,
        RotateTransform rotate,
        TranslateTransform translate)
    {
        public int LineIndex { get; } = lineIndex;
        public Grid Container { get; } = container;
        public List<AnimatedGlyph> Glyphs { get; } = glyphs;
        public TimeSpan Start { get; } = start;
        public TimeSpan End { get; } = end;
        public double BaseY { get; } = baseY;
        public int Direction { get; } = direction;
        public double BaseAngle { get; } = baseAngle;
        public RotateTransform Rotate { get; } = rotate;
        public TranslateTransform Translate { get; } = translate;
    }

    private sealed record AnimationTiming(
        double Local,
        double Duration,
        double RevealDuration,
        double GroupOpacity,
        double Progress);
}
