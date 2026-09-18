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
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int HtClient = 1;

    private readonly Grid _root = new();
    private readonly StackPanel _standardPanel = new()
    {
        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };
    private readonly Canvas _performanceCanvas = new() { IsHitTestVisible = false };
    private readonly TextBlock _title = NewTextBlock();
    private readonly TextBlock _previous = NewTextBlock();
    private readonly TextBlock _next = NewTextBlock();
    private readonly TextBlock _status = NewTextBlock();
    private readonly Canvas _standardLyricsCanvas = new() { IsHitTestVisible = true };
    private readonly List<StandardLineVisual> _standardLines = [];
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
    private Forms.ToolStripMenuItem? _noAnimationItem;
    private Forms.ToolStripMenuItem? _limbusAnimationItem;
    private Forms.ToolStripMenuItem? _positionLockItem;
    private Forms.ToolStripMenuItem? _visibleLyricsMenu;
    private Forms.TrackBar? _visibleLyricsSlider;
    private Forms.NumericUpDown? _visibleLyricsNumber;
    private bool _updatingVisibleLyricsSlider;
    private Forms.TrackBar? _opacitySlider;
    private Forms.NumericUpDown? _opacityNumber;
    private bool _updatingOpacitySlider;
    private Forms.TrackBar? _fontScaleSlider;
    private Forms.TextBox? _fontScaleText;
    private bool _updatingFontScaleSlider;
    private Forms.ToolStripMenuItem? _keywordMenu;
    private Forms.ToolStripMenuItem? _keywordEnabledItem;
    private Forms.TrackBar? _keywordScaleSlider;
    private Forms.NumericUpDown? _keywordScaleNumber;
    private bool _updatingKeywordScaleSlider;

    private DispatcherTimer _pollTimer = null!;
    private DispatcherTimer _animationTimer = null!;
    private OverlayConfig _config = new();
    private FontFamily _activeFont = new("Microsoft YaHei UI");
    private IReadOnlyList<LyricLine> _lyrics = [];
    private IReadOnlyDictionary<int, IReadOnlyList<KeywordSpan>> _keywordSelections =
        new Dictionary<int, IReadOnlyList<KeywordSpan>>();
    private string _keywordSelectionKey = string.Empty;
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
    private readonly int? _startupAnimationStyleOverride;
    private HwndSource? _windowSource;
    private StandardLineVisual? _draggingStandardLine;

    public OverlayWindow(
        bool startInDemo = false,
        string? startupModeOverride = null,
        int? startupAnimationStyleOverride = null)
    {
        _startInDemo = startInDemo;
        _startupModeOverride = startupModeOverride;
        _startupAnimationStyleOverride = startupAnimationStyleOverride;
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

        var currentHost = new Grid { HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        currentHost.Children.Add(_status);
        _standardPanel.Children.Add(_title);
        _standardPanel.Children.Add(_previous);
        _standardPanel.Children.Add(currentHost);
        _standardPanel.Children.Add(_next);
        _root.Children.Add(_standardLyricsCanvas);
        _root.Children.Add(_standardPanel);
        _root.Children.Add(_performanceCanvas);
        _root.Background = System.Windows.Media.Brushes.Transparent;
        Content = _root;

        _tray = BuildTrayIcon();
        Loaded += OnLoaded;
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        LostMouseCapture += OnLostMouseCapture;
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
        if (_startupAnimationStyleOverride is not null)
        {
            _config.AnimationStyle = Math.Clamp(_startupAnimationStyleOverride.Value, 0, 1);
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

            var key = snapshot.SongId is > 0
                ? $"netease:{snapshot.SongId.Value}"
                : $"{snapshot.Title}\n{snapshot.Artist}";
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
                BeginLyricsLoad(snapshot.Title, snapshot.Artist, snapshot.SongId, key);
                return;
            }

            if (_lyrics.Count == 0 && !_lyricsLoading && DateTimeOffset.Now >= _nextLyricsRetry)
                BeginLyricsLoad(snapshot.Title, snapshot.Artist, snapshot.SongId, key);

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

    private void BeginLyricsLoad(string title, string artist, long? songId, string expectedSongKey)
    {
        if (_lyricsLoading && string.Equals(expectedSongKey, _lyricsLoadingSongKey, StringComparison.Ordinal)) return;
        CancelLyricsLoad();
        var cancellation = new CancellationTokenSource();
        _lyricsCancellation = cancellation;
        _lyricsLoading = true;
        _lyricsLoadingSongKey = expectedSongKey;
        ShowStatus("正在获取歌词…", keepTitle: true);
        _ = LoadLyricsAsync(title, artist, songId, expectedSongKey, cancellation);
    }

    private async Task LoadLyricsAsync(
        string title,
        string artist,
        long? songId,
        string expectedSongKey,
        CancellationTokenSource cancellation)
    {
        try
        {
            var result = await _api.GetLyricsAsync(title, artist, songId, cancellation.Token);
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
        _standardLyricsCanvas.Visibility = Visibility.Visible;
        _status.Visibility = Visibility.Collapsed;
        while (_standardLines.Count >= VisibleLyricsLimit)
        {
            _standardLyricsCanvas.Children.Remove(_standardLines[0].Container);
            _standardLines.RemoveAt(0);
        }

        var text = GetDisplayText(_lyrics[index]);
        var glyphPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var glyphs = new List<AnimatedGlyph>();
        CreateGlyphs(glyphPanel, glyphs, text, index);
        var viewbox = NewViewbox();
        var layoutPadding = ScaledLayoutPixels(16);
        viewbox.MaxWidth = Math.Max(100, Width - layoutPadding * 2);
        viewbox.MaxHeight = Math.Min(
            Math.Max(40, Height - layoutPadding * 2),
            ScaledCurrentFontSize * 1.9 + ScaledLayoutPixels(16));
        viewbox.Child = glyphPanel;
        var container = new Grid
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = _config.PositionLocked ? null : Cursors.SizeAll
        };
        container.Children.Add(viewbox);

        var estimatedWidth = Math.Min(
            Math.Max(100, Width - layoutPadding * 2),
            Math.Max(ScaledLayoutPixels(180), text.Length * ScaledCurrentFontSize * 0.72));
        var estimatedHeight = Math.Min(
            Math.Max(40, Height - layoutPadding * 2),
            ScaledCurrentFontSize * 1.9 + ScaledLayoutPixels(16));
        var visual = new StandardLineVisual(
            index, container, glyphs, _activeLineStart, _activeLineEnd,
            estimatedWidth, estimatedHeight);
        PositionStandardLine(visual, StandardSlotForLine(index, VisibleLyricsLimit));
        container.SizeChanged += (_, _) =>
        {
            if (!visual.IsDragging) PositionStandardLine(visual, visual.SlotIndex);
        };
        _standardLines.Add(visual);
        _standardLyricsCanvas.Children.Add(container);
        _previous.Text = string.Empty;
        _next.Text = string.Empty;
        AnimateStandard(CurrentPlaybackPosition());
    }

    private void AnimateStandard(TimeSpan position)
    {
        if (_standardLines.Count == 0 || _activeLineIndex < 0) return;
        if (!UsesLimbusAnimation())
        {
            foreach (var line in _standardLines)
            {
                SetGlyphsStatic(line.Glyphs);
                line.Container.Opacity = 1;
            }
            return;
        }
        var current = _standardLines[^1];
        var currentDuration = Math.Max(0.65, (current.End - current.Start).TotalSeconds);
        var currentProgress = Math.Clamp((position - current.Start).TotalSeconds / currentDuration, 0, 1);
        var fadingLine = _standardLines.Count >= VisibleLyricsLimit && _standardLines.Count > 1
            ? _standardLines[0]
            : null;

        foreach (var line in _standardLines)
        {
            var timing = CalculatePerformanceTiming(position, line.Start, line.End, line.Glyphs.Count);
            AnimateGlyphs(line.Glyphs, timing, useIndividualDrift: true);
            var opacity = 1.0;
            if (ReferenceEquals(line, fadingLine))
            {
                var fadeStart = Math.Clamp(_config.PerformancePreviousFadeStart, 0.1, 0.95);
                opacity = currentProgress <= fadeStart
                    ? 1
                    : 1 - Math.Clamp((currentProgress - fadeStart) / (1 - fadeStart), 0, 1);
            }
            else if (ReferenceEquals(line, current) &&
                     line.LineIndex == _lyrics.Count - 1 && position > line.End)
            {
                opacity = 1 - Math.Clamp((position - line.End).TotalSeconds / 0.9, 0, 1);
            }
            line.Container.Opacity = opacity;
        }
    }

    private void BeginPerformanceLine(int index)
    {
        _standardPanel.Visibility = Visibility.Collapsed;
        _performanceCanvas.Visibility = Visibility.Visible;
        while (_performanceLines.Count >= VisibleLyricsLimit)
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
        viewbox.MaxHeight = Math.Min(
            SystemParameters.PrimaryScreenHeight * 0.72,
            ScaledCurrentFontSize * 1.8 + ScaledLayoutPixels(16));
        viewbox.Child = glyphPanel;
        var container = new Grid { Opacity = 1 };
        container.Children.Add(viewbox);

        var random = new Random(HashCode.Combine(_songKey, index, text));
        var edgeMargin = Math.Min(ScaledLayoutPixels(30), SystemParameters.PrimaryScreenWidth * 0.22);
        var trailingMargin = Math.Min(ScaledLayoutPixels(40), SystemParameters.PrimaryScreenWidth * 0.22);
        var estimatedWidth = Math.Min(SystemParameters.PrimaryScreenWidth * 0.72,
            Math.Max(ScaledLayoutPixels(180), text.Length * ScaledCurrentFontSize * 0.72));
        var maxX = Math.Max(edgeMargin, SystemParameters.PrimaryScreenWidth - estimatedWidth - trailingMargin);
        var x = edgeMargin + random.NextDouble() * Math.Max(1, maxX - edgeMargin);
        var minY = SystemParameters.PrimaryScreenHeight * 0.13;
        var maxY = SystemParameters.PrimaryScreenHeight * 0.78;
        var y = minY + random.NextDouble() * Math.Max(1, maxY - minY);
        var verticalRange = Math.Max(1, maxY - minY);
        var separation = Math.Min(Math.Abs(ScaledMotionPixels(130)), verticalRange * 0.45);
        var separationShift = Math.Min(Math.Abs(ScaledMotionPixels(180)), verticalRange * 0.55);
        if (_performanceLines.Count > 0 && Math.Abs(y - _performanceLines[^1].BaseY) < separation)
            y = y + separationShift < maxY ? y + separationShift : Math.Max(minY, y - separationShift);

        Canvas.SetLeft(container, x);
        Canvas.SetTop(container, y);
        var direction = random.Next(0, 2) == 0 ? -1 : 1;
        var baseAngle = UsesLimbusAnimation()
            ? (random.NextDouble() * 2 - 1) * _config.PerformanceTiltDegrees
            : 0;
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
        if (!UsesLimbusAnimation())
        {
            foreach (var line in _performanceLines)
            {
                SetGlyphsStatic(line.Glyphs);
                line.Container.Opacity = 1;
                line.Translate.X = 0;
                line.Translate.Y = 0;
                line.Rotate.Angle = 0;
            }
            return;
        }
        var current = _performanceLines[^1];
        var currentDuration = Math.Max(0.65, (current.End - current.Start).TotalSeconds);
        var currentProgress = Math.Clamp((position - current.Start).TotalSeconds / currentDuration, 0, 1);
        var motionTime = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var fadingLine = _performanceLines.Count >= VisibleLyricsLimit && _performanceLines.Count > 1
            ? _performanceLines[0]
            : null;

        for (var i = 0; i < _performanceLines.Count; i++)
        {
            var line = _performanceLines[i];
            var isCurrent = ReferenceEquals(line, current);
            var opacity = 1.0;
            if (ReferenceEquals(line, fadingLine))
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
            line.Translate.X = Math.Sin(motionTime * 0.8 + line.LineIndex) * ScaledMotionPixels(1.5);
            line.Translate.Y = line.Direction * ScaledMotionPixels(_config.PerformanceRisePixels) * progress +
                               Math.Sin(motionTime * 1.1 + line.LineIndex * 0.7) * ScaledMotionPixels(1.2);
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
            var jitterStrength = ScaledMotionPixels(_config.CharacterJitterPixels) * item.RelativeScale * eased;
            var jitterX = Math.Sin(motionTime * item.Frequency * 0.73 + item.Phase) * jitterStrength * 0.55;
            var jitterY = Math.Sin(motionTime * item.Frequency + item.Phase * 1.31) * jitterStrength;
            var drift = useIndividualDrift
                ? item.Direction * ScaledMotionPixels(_config.CharacterDriftPixels) * item.RelativeScale *
                  (timing.Progress - 0.5)
                : 0;
            var entrance = -item.Direction * ScaledMotionPixels(_config.CharacterDriftPixels) *
                           item.RelativeScale * 1.25 * (1 - eased);
            item.Text.Opacity = eased * timing.GroupOpacity;
            item.Translate.X = jitterX;
            item.Translate.Y = drift + entrance + jitterY;
            item.Rotate.Angle = Math.Sin(motionTime * item.Frequency * 0.41 + item.Phase) *
                                _config.CharacterJitterPixels * 0.25 * eased;
        }
    }

    private static void SetGlyphsStatic(IEnumerable<AnimatedGlyph> glyphs)
    {
        foreach (var glyph in glyphs)
        {
            glyph.Text.Opacity = 1;
            glyph.Translate.X = 0;
            glyph.Translate.Y = 0;
            glyph.Rotate.Angle = 0;
        }
    }

    private void CreateGlyphs(StackPanel panel, List<AnimatedGlyph> target, string text, int lineIndex)
    {
        var units = KeywordSelector.BuildAnimationUnits(text, GetKeywordSpans(lineIndex));
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            var relativeScale = unit.IsHighlighted ? EffectiveKeywordScale : 1.0;
            var translate = new TranslateTransform();
            var rotate = new RotateTransform();
            var transforms = new TransformGroup();
            transforms.Children.Add(rotate);
            transforms.Children.Add(translate);
            var glyph = new TextBlock
            {
                Text = unit.Text == " " ? "\u00A0" : unit.Text,
                FontFamily = _activeFont,
                FontSize = SafeFontSize(ScaledCurrentFontSize * relativeScale),
                FontWeight = unit.IsHighlighted ? FontWeights.Bold : FontWeights.SemiBold,
                Foreground = unit.IsHighlighted
                    ? ParseBrush(_config.HighlightKeywordColor, System.Windows.Media.Color.FromRgb(255, 196, 77))
                    : ParseBrush(_config.CurrentColor, Colors.White),
                Opacity = UsesLimbusAnimation() ? 0 : 1,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transforms,
                Effect = CreateShadow(relativeScale)
            };
            TextOptions.SetTextRenderingMode(glyph, TextRenderingMode.Grayscale);
            panel.Children.Add(glyph);
            var seed = unchecked(lineIndex * 397 + i * 97 + text.Length * 17);
            target.Add(new AnimatedGlyph(
                glyph, translate, rotate, seed % 2 == 0 ? -1 : 1,
                6.4 + Math.Abs(seed % 17) * 0.17,
                Math.Abs(seed % 101) / 101.0 * Math.PI * 2,
                relativeScale));
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

    private IReadOnlyList<KeywordSpan>? GetKeywordSpans(int lineIndex)
    {
        if (!_config.HighlightKeywordsEnabled || lineIndex < 0 || lineIndex >= _lyrics.Count) return null;
        EnsureKeywordSelections();
        return _keywordSelections.GetValueOrDefault(lineIndex);
    }

    private void EnsureKeywordSelections()
    {
        var key = $"{_songKey}|{_config.LyricLanguage}|{_lyrics.Count}|" +
                  $"{(_lyrics.Count > 0 ? _lyrics[0].Time.Ticks : 0)}|" +
                  $"{(_lyrics.Count > 0 ? _lyrics[^1].Time.Ticks : 0)}";
        if (string.Equals(key, _keywordSelectionKey, StringComparison.Ordinal)) return;
        var lines = _lyrics.Select(GetDisplayText).ToArray();
        _keywordSelections = KeywordSelector.Select(lines, key);
        _keywordSelectionKey = key;
    }

    private void InvalidateKeywordSelections()
    {
        _keywordSelectionKey = string.Empty;
        _keywordSelections = new Dictionary<int, IReadOnlyList<KeywordSpan>>();
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
        _standardLyricsCanvas.Visibility = Visibility.Collapsed;
        _performanceCanvas.Visibility = Visibility.Collapsed;
        _status.Visibility = Visibility.Visible;
        _status.Text = text;
        _status.Opacity = 1;
    }

    private void ShowTimelineWaiting() => ShowStatus(
        "已检测到网易云，正在寻找实时播放进度…\n首次同步请保持歌曲播放数秒",
        keepTitle: true);

    private void ClearAllLyrics()
    {
        if (_draggingStandardLine is not null)
            FinishStandardLineDrag(savePosition: true);
        _standardLines.Clear();
        _standardLyricsCanvas.Children.Clear();
        _previous.Text = string.Empty;
        _next.Text = string.Empty;
        _performanceLines.Clear();
        _performanceCanvas.Children.Clear();
    }

    private void ReloadConfig()
    {
        _config = OverlayConfig.Load();
        InvalidateKeywordSelections();
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
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        var layoutPadding = ScaledLayoutPixels(24);
        _status.MaxWidth = Math.Max(100, Width - layoutPadding);
        _status.MaxHeight = Math.Max(40, Height - layoutPadding);
    }

    private bool IsPerformanceMode() =>
        string.Equals(_config.DisplayMode, "Performance", StringComparison.OrdinalIgnoreCase);

    private bool UsesLimbusAnimation() => _config.AnimationStyle == 1;

    private System.Windows.Media.Effects.DropShadowEffect CreateShadow(double relativeScale = 1.0) => new()
    {
        Color = ((SolidColorBrush)ParseBrush(_config.ShadowColor, Colors.Black)).Color,
        BlurRadius = Math.Min(Math.Abs(ScaledMotionPixels(8)) * relativeScale, 300),
        ShadowDepth = Math.Min(Math.Abs(ScaledMotionPixels(2)) * relativeScale, 160),
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

        var animationMenu = new Forms.ToolStripMenuItem("动画选择");
        _noAnimationItem = new Forms.ToolStripMenuItem(
            "0：无动画", null, (_, _) => Dispatcher.Invoke(() => SetAnimationStyle(0)));
        _limbusAnimationItem = new Forms.ToolStripMenuItem(
            "1：Limbus演出", null, (_, _) => Dispatcher.Invoke(() => SetAnimationStyle(1)));
        animationMenu.DropDownItems.Add(_noAnimationItem);
        animationMenu.DropDownItems.Add(_limbusAnimationItem);
        animationMenu.DropDownOpening += (_, _) => Dispatcher.Invoke(RefreshMenuChecks);
        menu.Items.Add(animationMenu);

        _visibleLyricsMenu = new Forms.ToolStripMenuItem("屏幕歌词数量");
        _visibleLyricsSlider = new Forms.TrackBar
        {
            Minimum = 1,
            Maximum = 10,
            Value = 2,
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = 1,
            AutoSize = false,
            Width = 220,
            Height = 48
        };
        _visibleLyricsSlider.ValueChanged += (_, _) =>
        {
            if (!_updatingVisibleLyricsSlider)
                Dispatcher.Invoke(() => SetVisibleLyricsLimit(_visibleLyricsSlider.Value));
        };
        _visibleLyricsMenu.DropDownItems.Add(new Forms.ToolStripControlHost(_visibleLyricsSlider)
        {
            AutoSize = false,
            Width = 230,
            Height = 50,
            Margin = new Forms.Padding(4, 2, 4, 2)
        });
        _visibleLyricsNumber = NewPercentageInput(1, 10, 2);
        _visibleLyricsNumber.ValueChanged += (_, _) =>
        {
            if (!_updatingVisibleLyricsSlider)
                Dispatcher.Invoke(() => SetVisibleLyricsLimit((int)_visibleLyricsNumber.Value));
        };
        _visibleLyricsMenu.DropDownItems.Add(NewInputHost(_visibleLyricsNumber, "数量：", "条"));
        _visibleLyricsMenu.DropDownOpening += (_, _) => Dispatcher.Invoke(RefreshMenuChecks);
        menu.Items.Add(_visibleLyricsMenu);

        _positionLockItem = new Forms.ToolStripMenuItem(
            "锁定歌词位置", null, (_, _) => Dispatcher.Invoke(TogglePositionLock));
        menu.Items.Add(_positionLockItem);

        var appearanceMenu = new Forms.ToolStripMenuItem("字体与颜色");
        appearanceMenu.DropDownItems.Add("导入字体…", null, (_, _) => ImportFont());
        appearanceMenu.DropDownItems.Add("恢复默认字体", null, (_, _) => ResetFont());
        appearanceMenu.DropDownItems.Add("选择歌词颜色…", null, (_, _) => ChooseLyricColor());
        _keywordMenu = new Forms.ToolStripMenuItem("重点词");
        _keywordEnabledItem = new Forms.ToolStripMenuItem(
            "启用重点词", null, (_, _) => Dispatcher.Invoke(ToggleKeywordHighlighting));
        _keywordMenu.DropDownItems.Add(_keywordEnabledItem);
        _keywordMenu.DropDownItems.Add("选择重点词颜色…", null, (_, _) => ChooseKeywordColor());
        var keywordScaleMenu = new Forms.ToolStripMenuItem("重点词大小");
        _keywordScaleSlider = new Forms.TrackBar
        {
            Minimum = 100,
            Maximum = 200,
            Value = 140,
            TickFrequency = 10,
            SmallChange = 1,
            LargeChange = 10,
            AutoSize = false,
            Width = 220,
            Height = 48
        };
        _keywordScaleSlider.ValueChanged += (_, _) =>
        {
            if (!_updatingKeywordScaleSlider)
                Dispatcher.Invoke(() => SetKeywordScale(_keywordScaleSlider.Value));
        };
        keywordScaleMenu.DropDownItems.Add(new Forms.ToolStripControlHost(_keywordScaleSlider)
        {
            AutoSize = false,
            Width = 230,
            Height = 50,
            Margin = new Forms.Padding(4, 2, 4, 2)
        });
        _keywordScaleNumber = NewPercentageInput(100, 200, 140);
        _keywordScaleNumber.ValueChanged += (_, _) =>
        {
            if (!_updatingKeywordScaleSlider)
                Dispatcher.Invoke(() => SetKeywordScale((int)_keywordScaleNumber.Value));
        };
        keywordScaleMenu.DropDownItems.Add(NewPercentageInputHost(_keywordScaleNumber));
        keywordScaleMenu.DropDownOpening += (_, _) => Dispatcher.Invoke(RefreshMenuChecks);
        _keywordMenu.DropDownItems.Add(keywordScaleMenu);
        _keywordMenu.DropDownOpening += (_, _) => Dispatcher.Invoke(RefreshMenuChecks);
        appearanceMenu.DropDownItems.Add(_keywordMenu);
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
        InvalidateKeywordSelections();
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
        if (_noAnimationItem is not null) _noAnimationItem.Checked = !UsesLimbusAnimation();
        if (_limbusAnimationItem is not null) _limbusAnimationItem.Checked = UsesLimbusAnimation();
        if (_visibleLyricsMenu is not null)
            _visibleLyricsMenu.Text = $"屏幕歌词数量（最多 {VisibleLyricsLimit} 条）";
        if (_visibleLyricsSlider is not null)
        {
            _updatingVisibleLyricsSlider = true;
            _visibleLyricsSlider.Value = VisibleLyricsLimit;
            if (_visibleLyricsNumber is not null) _visibleLyricsNumber.Value = VisibleLyricsLimit;
            _updatingVisibleLyricsSlider = false;
        }
        if (_positionLockItem is not null)
        {
            _positionLockItem.Checked = _config.PositionLocked;
            _positionLockItem.Enabled = !IsPerformanceMode();
            _positionLockItem.Text = _config.PositionLocked
                ? "锁定歌词位置（已锁定）"
                : "锁定歌词位置（可拖动）";
        }
        if (_keywordMenu is not null)
            _keywordMenu.Text = _config.HighlightKeywordsEnabled ? "重点词（已开启）" : "重点词（已关闭）";
        if (_keywordEnabledItem is not null)
        {
            _keywordEnabledItem.Checked = _config.HighlightKeywordsEnabled;
            _keywordEnabledItem.Text = _config.HighlightKeywordsEnabled ? "启用重点词（已开启）" : "启用重点词（已关闭）";
        }
        var keywordScalePercentage = (int)Math.Round(
            Math.Clamp(_config.HighlightKeywordScale, 1.0, 2.0) * 100);
        if (_keywordScaleSlider is not null)
        {
            _updatingKeywordScaleSlider = true;
            _keywordScaleSlider.Value = keywordScalePercentage;
            if (_keywordScaleNumber is not null) _keywordScaleNumber.Value = keywordScalePercentage;
            _updatingKeywordScaleSlider = false;
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

    private void SetAnimationStyle(int style)
    {
        style = Math.Clamp(style, 0, 1);
        if (_config.AnimationStyle == style) return;
        _config.AnimationStyle = style;
        _config.Save();
        RefreshMenuChecks();
        RebuildVisibleLyrics();
    }

    private void ToggleKeywordHighlighting()
    {
        _config.HighlightKeywordsEnabled = !_config.HighlightKeywordsEnabled;
        _config.Save();
        InvalidateKeywordSelections();
        RefreshMenuChecks();
        RebuildVisibleLyrics();
    }

    private void SetKeywordScale(int percentage)
    {
        percentage = Math.Clamp(percentage, 100, 200);
        _updatingKeywordScaleSlider = true;
        if (_keywordScaleSlider is not null && _keywordScaleSlider.Value != percentage)
            _keywordScaleSlider.Value = percentage;
        if (_keywordScaleNumber is not null && _keywordScaleNumber.Value != percentage)
            _keywordScaleNumber.Value = percentage;
        _updatingKeywordScaleSlider = false;
        _config.HighlightKeywordScale = percentage / 100.0;
        _config.Save();
        RebuildVisibleLyrics();
    }

    private void RebuildVisibleLyrics()
    {
        _activeLineIndex = int.MinValue;
        ClearAllLyrics();
        if (_lyrics.Count > 0 && _clockInitialized) EnsureActiveLine(CurrentPlaybackPosition());
    }

    private void SetVisibleLyricsLimit(int value)
    {
        value = Math.Clamp(value, 1, 10);
        _updatingVisibleLyricsSlider = true;
        if (_visibleLyricsSlider is not null && _visibleLyricsSlider.Value != value)
            _visibleLyricsSlider.Value = value;
        if (_visibleLyricsNumber is not null && _visibleLyricsNumber.Value != value)
            _visibleLyricsNumber.Value = value;
        _updatingVisibleLyricsSlider = false;
        if (_config.MaxVisibleLyrics == value) return;
        _config.MaxVisibleLyrics = value;
        _config.Save();
        ApplyWindowMode();
        RefreshMenuChecks();
        _activeLineIndex = int.MinValue;
        ClearAllLyrics();
        if (_lyrics.Count > 0 && _clockInitialized) EnsureActiveLine(CurrentPlaybackPosition());
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
        ApplyWindowMode();
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

    private double EffectiveKeywordScale =>
        Math.Clamp(
            double.IsFinite(_config.HighlightKeywordScale) ? _config.HighlightKeywordScale : 1.4,
            1.0,
            2.0);

    private int VisibleLyricsLimit => Math.Clamp(_config.MaxVisibleLyrics, 1, 10);

    internal static int StandardSlotForLine(int lineIndex, int visibleLyricsLimit) =>
        Math.Max(0, lineIndex) % Math.Clamp(visibleLyricsLimit, 1, 10);

    private double ScaledMotionPixels(double value)
    {
        var screenLimit = Math.Max(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight) * 2;
        return Math.Clamp(value * EffectiveFontScale, -screenLimit, screenLimit);
    }

    private double ScaledLayoutPixels(double value)
    {
        var screenLimit = Math.Max(40, Math.Min(
            SystemParameters.PrimaryScreenWidth,
            SystemParameters.PrimaryScreenHeight) * 0.22);
        return Math.Clamp(value * EffectiveFontScale, 0, screenLimit);
    }

    private static double SafeFontSize(double value) => Math.Clamp(value, 0.1, 35_000);

    private void ApplyConfiguredFontSizes()
    {
        _title.FontSize = SafeFontSize((_config.ContextFontSize - 2) * EffectiveFontScale);
        _previous.FontSize = ScaledContextFontSize;
        _next.FontSize = ScaledContextFontSize;
        _status.FontSize = ScaledCurrentFontSize;
        var margin = new Thickness(
            ScaledLayoutPixels(8),
            ScaledLayoutPixels(1),
            ScaledLayoutPixels(8),
            ScaledLayoutPixels(2));
        foreach (var block in new[] { _title, _previous, _next, _status })
            block.Margin = margin;
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

    private static Forms.ToolStripControlHost NewPercentageInputHost(Forms.Control input) =>
        NewInputHost(input, "百分比：", "%");

    private static Forms.ToolStripControlHost NewInputHost(
        Forms.Control input,
        string label,
        string suffix)
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
            Text = label,
            AutoSize = true,
            Margin = new Forms.Padding(0, 4, 2, 0)
        });
        panel.Controls.Add(input);
        panel.Controls.Add(new Forms.Label
        {
            Text = suffix,
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

    private void PositionStandardLine(StandardLineVisual visual, int slotIndex)
    {
        slotIndex = Math.Clamp(slotIndex, 0, VisibleLyricsLimit - 1);
        visual.SlotIndex = slotIndex;
        var position = GetStandardSlotPosition(slotIndex);
        var padding = ScaledLayoutPixels(16);
        var width = visual.Container.ActualWidth > 1
            ? visual.Container.ActualWidth
            : visual.EstimatedWidth;
        var height = visual.Container.ActualHeight > 1
            ? visual.Container.ActualHeight
            : visual.EstimatedHeight;
        var availableWidth = Math.Max(0, ActualWidth - width - padding * 2);
        var availableHeight = Math.Max(0, ActualHeight - height - padding * 2);
        Canvas.SetLeft(visual.Container, padding + availableWidth * position.X);
        Canvas.SetTop(visual.Container, padding + availableHeight * position.Y);
    }

    private LyricSlotPosition GetStandardSlotPosition(int slotIndex)
    {
        if (slotIndex < _config.StandardLyricPositions.Count)
        {
            var saved = _config.StandardLyricPositions[slotIndex];
            if (saved.X >= 0 && saved.Y >= 0 &&
                double.IsFinite(saved.X) && double.IsFinite(saved.Y))
            {
                return new LyricSlotPosition
                {
                    X = Math.Clamp(saved.X, 0, 1),
                    Y = Math.Clamp(saved.Y, 0, 1)
                };
            }
        }

        var defaultY = VisibleLyricsLimit == 1
            ? 0.68
            : 0.18 + 0.64 * slotIndex / (VisibleLyricsLimit - 1.0);
        return new LyricSlotPosition { X = 0.5, Y = defaultY };
    }

    private void SaveStandardSlotPosition(StandardLineVisual visual)
    {
        var padding = ScaledLayoutPixels(16);
        var width = Math.Max(1, visual.Container.ActualWidth);
        var height = Math.Max(1, visual.Container.ActualHeight);
        var availableWidth = Math.Max(1, ActualWidth - width - padding * 2);
        var availableHeight = Math.Max(1, ActualHeight - height - padding * 2);
        var left = Canvas.GetLeft(visual.Container);
        var top = Canvas.GetTop(visual.Container);
        var normalizedX = Math.Clamp((left - padding) / availableWidth, 0, 1);
        var normalizedY = Math.Clamp((top - padding) / availableHeight, 0, 1);
        while (_config.StandardLyricPositions.Count <= visual.SlotIndex)
            _config.StandardLyricPositions.Add(new LyricSlotPosition());
        _config.StandardLyricPositions[visual.SlotIndex] = new LyricSlotPosition
        {
            X = normalizedX,
            Y = normalizedY
        };
        _config.Save();
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (IsPerformanceMode() || _config.PositionLocked ||
            eventArgs.ChangedButton != MouseButton.Left) return;
        var visual = FindStandardLineAt(eventArgs.GetPosition(this));
        if (visual is null) return;
        _draggingStandardLine = visual;
        visual.IsDragging = true;
        visual.DragStart = eventArgs.GetPosition(_standardLyricsCanvas);
        visual.OriginalLeft = Canvas.GetLeft(visual.Container);
        visual.OriginalTop = Canvas.GetTop(visual.Container);
        if (!double.IsFinite(visual.OriginalLeft)) visual.OriginalLeft = 0;
        if (!double.IsFinite(visual.OriginalTop)) visual.OriginalTop = 0;
        CaptureMouse();
        eventArgs.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs eventArgs)
    {
        var visual = _draggingStandardLine;
        if (visual is null || eventArgs.LeftButton != MouseButtonState.Pressed) return;
        var point = eventArgs.GetPosition(_standardLyricsCanvas);
        var width = Math.Max(1, visual.Container.ActualWidth);
        var height = Math.Max(1, visual.Container.ActualHeight);
        var padding = ScaledLayoutPixels(16);
        var left = Math.Clamp(
            visual.OriginalLeft + point.X - visual.DragStart.X,
            padding,
            Math.Max(padding, ActualWidth - width - padding));
        var top = Math.Clamp(
            visual.OriginalTop + point.Y - visual.DragStart.Y,
            padding,
            Math.Max(padding, ActualHeight - height - padding));
        Canvas.SetLeft(visual.Container, left);
        Canvas.SetTop(visual.Container, top);
        eventArgs.Handled = true;
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs eventArgs)
    {
        if (_draggingStandardLine is null) return;
        FinishStandardLineDrag(savePosition: true);
        eventArgs.Handled = true;
    }

    private void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs eventArgs)
    {
        if (_draggingStandardLine is not null)
            FinishStandardLineDrag(savePosition: true, releaseCapture: false);
    }

    private void FinishStandardLineDrag(bool savePosition, bool releaseCapture = true)
    {
        var visual = _draggingStandardLine;
        if (visual is null) return;
        if (savePosition) SaveStandardSlotPosition(visual);
        visual.IsDragging = false;
        _draggingStandardLine = null;
        if (releaseCapture && IsMouseCaptured) ReleaseMouseCapture();
    }

    private StandardLineVisual? FindStandardLineAt(Point windowPoint)
    {
        for (var i = _standardLines.Count - 1; i >= 0; i--)
        {
            var line = _standardLines[i];
            var bounds = GetStandardLineHitBounds(line);
            if (bounds?.Contains(windowPoint) == true) return line;
        }
        return null;
    }

    private Rect? GetStandardLineHitBounds(StandardLineVisual line)
    {
        if (!line.Container.IsVisible || line.Container.Opacity <= 0.02 ||
            line.Container.ActualWidth <= 0 || line.Container.ActualHeight <= 0) return null;
        try
        {
            var origin = line.Container.TranslatePoint(new Point(0, 0), this);
            var bounds = new Rect(origin, new Size(
                line.Container.ActualWidth,
                line.Container.ActualHeight));
            foreach (var glyph in line.Glyphs)
            {
                if (glyph.Text.ActualWidth <= 0 || glyph.Text.ActualHeight <= 0) continue;
                var glyphBounds = glyph.Text.TransformToAncestor(this).TransformBounds(
                    new Rect(new Point(0, 0), glyph.Text.RenderSize));
                bounds.Union(glyphBounds);
            }
            var maximumRelativeScale = line.Glyphs.Count == 0
                ? 1.0
                : line.Glyphs.Max(glyph => glyph.RelativeScale);
            var shadowPadding = Math.Min(
                Math.Abs(ScaledMotionPixels(12)) * maximumRelativeScale,
                160);
            bounds.Inflate(shadowPadding, shadowPadding);
            return bounds;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(hwnd);
        _windowSource?.AddHook(WindowMessageHook);
        UpdateInteractionMode();
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmNcHitTest || IsPerformanceMode() || _config.PositionLocked)
            return IntPtr.Zero;

        var packedPoint = lParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packedPoint & 0xFFFF)),
            unchecked((short)((packedPoint >> 16) & 0xFFFF)));
        Point windowPoint;
        try
        {
            windowPoint = PointFromScreen(screenPoint);
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }

        if (FindStandardLineAt(windowPoint) is not null)
        {
            handled = true;
            return new IntPtr(HtClient);
        }

        handled = true;
        return new IntPtr(HtTransparent);
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

    private void ChooseKeywordColor()
    {
        using var dialog = new Forms.ColorDialog { FullOpen = true, AnyColor = true };
        try
        {
            var color = ((SolidColorBrush)ParseBrush(_config.HighlightKeywordColor,
                System.Windows.Media.Color.FromRgb(255, 196, 77))).Color;
            dialog.Color = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        catch
        {
        }
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        _config.HighlightKeywordColor = $"#FF{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        _config.Save();
        RebuildVisibleLyrics();
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
        foreach (var line in _standardLines)
            line.Container.Cursor = clickThrough ? null : Cursors.SizeAll;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing) return;
        _isClosing = true;
        _pollTimer?.Stop();
        _animationTimer?.Stop();
        CancelLyricsLoad();
        _media.Dispose();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
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
        double Phase,
        double RelativeScale);

    private sealed class StandardLineVisual(
        int lineIndex,
        Grid container,
        List<AnimatedGlyph> glyphs,
        TimeSpan start,
        TimeSpan end,
        double estimatedWidth,
        double estimatedHeight)
    {
        public int LineIndex { get; } = lineIndex;
        public Grid Container { get; } = container;
        public List<AnimatedGlyph> Glyphs { get; } = glyphs;
        public TimeSpan Start { get; } = start;
        public TimeSpan End { get; } = end;
        public double EstimatedWidth { get; } = estimatedWidth;
        public double EstimatedHeight { get; } = estimatedHeight;
        public int SlotIndex { get; set; }
        public bool IsDragging { get; set; }
        public Point DragStart { get; set; }
        public double OriginalLeft { get; set; }
        public double OriginalTop { get; set; }
    }

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
