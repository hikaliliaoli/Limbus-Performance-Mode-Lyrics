using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace NeteaseLyricsOverlay;

internal sealed record NeteaseProgressSnapshot(
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset CapturedAt,
    bool IsMoving,
    string Source);

/// <summary>
/// Reads the progress shown by the NetEase desktop client through Windows UI Automation.
/// The scan is isolated on an STA worker so a slow Chromium accessibility tree never
/// blocks the WPF dispatcher or the media-session polling loop.
/// </summary>
internal sealed partial class NeteaseProgressReader : IDisposable
{
    private static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MovingHoldTime = TimeSpan.FromMilliseconds(1800);

    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private string _requestedTrack = string.Empty;
    private long _generation;
    private NeteaseProgressSnapshot? _latest;
    private bool _disposed;

    public NeteaseProgressReader()
    {
        _worker = new Thread(SafeWorkerLoop)
        {
            IsBackground = true,
            Name = "NeteaseProgressUIA"
        };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    private void SafeWorkerLoop()
    {
        try { WorkerLoop(); }
        catch
        {
            // UI Automation is implemented by the target application's accessibility
            // provider. A provider failure must never bring down the lyric overlay.
        }
    }

    public void SetTrack(string? trackKey)
    {
        trackKey ??= string.Empty;
        lock (_gate)
        {
            if (string.Equals(_requestedTrack, trackKey, StringComparison.Ordinal)) return;
            _requestedTrack = trackKey;
            _generation++;
            _latest = null;
        }
        _wake.Set();
    }

    public bool TryGetLatest(string trackKey, out NeteaseProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            if (string.Equals(trackKey, _requestedTrack, StringComparison.Ordinal) &&
                _latest is not null &&
                DateTimeOffset.UtcNow - _latest.CapturedAt <= MaximumSnapshotAge)
            {
                snapshot = _latest;
                return true;
            }
        }

        snapshot = null!;
        return false;
    }

    private void WorkerLoop()
    {
        long observedGeneration = -1;
        var scanAllowedAt = DateTimeOffset.MinValue;
        TimeSpan? previousPosition = null;
        var lastMovementAt = DateTimeOffset.MinValue;

        while (true)
        {
            string track;
            long generation;
            lock (_gate)
            {
                if (_disposed) return;
                track = _requestedTrack;
                generation = _generation;
            }

            if (generation != observedGeneration)
            {
                observedGeneration = generation;
                previousPosition = null;
                lastMovementAt = DateTimeOffset.MinValue;
                // The Chromium accessibility tree briefly rebuilds after changing songs.
                scanAllowedAt = DateTimeOffset.UtcNow.AddMilliseconds(450);
            }

            if (string.IsNullOrEmpty(track))
            {
                _wake.WaitOne();
                continue;
            }

            var remaining = scanAllowedAt - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                _wake.WaitOne(Math.Min(350, Math.Max(25, (int)remaining.TotalMilliseconds)));
                continue;
            }

            if (TryReadProgress(out var position, out var duration, out var source))
            {
                var capturedAt = DateTimeOffset.UtcNow;
                if (previousPosition.HasValue &&
                    Math.Abs((position - previousPosition.Value).TotalMilliseconds) >= 500)
                {
                    lastMovementAt = capturedAt;
                }
                previousPosition = position;

                var sample = new NeteaseProgressSnapshot(
                    position,
                    duration,
                    capturedAt,
                    capturedAt - lastMovementAt <= MovingHoldTime,
                    source);
                lock (_gate)
                {
                    if (!_disposed && generation == _generation &&
                        string.Equals(track, _requestedTrack, StringComparison.Ordinal))
                    {
                        _latest = sample;
                    }
                }
            }

            _wake.WaitOne(300);
        }
    }

    private static bool TryReadProgress(
        out TimeSpan position,
        out TimeSpan duration,
        out string source)
    {
        position = TimeSpan.Zero;
        duration = TimeSpan.Zero;
        source = string.Empty;

        Process[] processes;
        try { processes = Process.GetProcessesByName("cloudmusic"); }
        catch { return false; }

        try
        {
            var desktop = AutomationElement.RootElement;
            var processIds = processes.Select(process => process.Id).ToHashSet();
            var scannedHandles = new HashSet<IntPtr>();
            foreach (var process in processes)
            {
                AutomationElementCollection windows;
                try
                {
                    var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id);
                    windows = desktop.FindAll(TreeScope.Children, condition);
                }
                catch
                {
                    continue;
                }

                foreach (AutomationElement window in windows)
                {
                    try
                    {
                        var handle = new IntPtr(window.Current.NativeWindowHandle);
                        if (handle != IntPtr.Zero) scannedHandles.Add(handle);
                    }
                    catch
                    {
                    }
                    if (TryReadWindow(window, out position, out duration, out source)) return true;
                }
            }

            // Chromium windows can be untitled, cloaked, or owned by a helper process;
            // Process.MainWindowHandle and the desktop UIA children may then omit them.
            foreach (var handle in EnumerateTopLevelWindows(processIds))
            {
                if (!scannedHandles.Add(handle)) continue;
                try
                {
                    var window = AutomationElement.FromHandle(handle);
                    if (window is not null &&
                        TryReadWindow(window, out position, out duration, out source))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return false;
    }

    private static IReadOnlyList<IntPtr> EnumerateTopLevelWindows(IReadOnlySet<int> processIds)
    {
        var visible = new List<IntPtr>();
        var hidden = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (!processIds.Contains(unchecked((int)processId))) return true;
            if (IsWindowVisible(handle)) visible.Add(handle);
            else hidden.Add(handle);
            return true;
        }, IntPtr.Zero);
        visible.AddRange(hidden);
        return visible;
    }

    private static bool TryReadWindow(
        AutomationElement window,
        out TimeSpan position,
        out TimeSpan duration,
        out string source)
    {
        position = TimeSpan.Zero;
        duration = TimeSpan.Zero;
        source = string.Empty;

        AutomationElementCollection elements;
        try
        {
            var textCondition = new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Text);
            elements = window.FindAll(TreeScope.Descendants, textCondition);
        }
        catch
        {
            return false;
        }

        var separateTimes = new List<TimeElement>();
        foreach (AutomationElement element in elements)
        {
            string name;
            try { name = element.Current.Name?.Trim() ?? string.Empty; }
            catch { continue; }

            if (TryParseCombinedProgress(name, out position, out duration))
            {
                source = "网易云界面时间";
                return true;
            }

            if (!TryParseSingleTime(name, out var value)) continue;
            try
            {
                var bounds = element.Current.BoundingRectangle;
                if (!bounds.IsEmpty) separateTimes.Add(new TimeElement(value, bounds));
            }
            catch
            {
            }
        }

        if (TryPairSeparateTimes(window, separateTimes, out position, out duration))
        {
            source = "网易云界面时间";
            return true;
        }

        return false;
    }

    private static bool TryPairSeparateTimes(
        AutomationElement window,
        IReadOnlyList<TimeElement> candidates,
        out TimeSpan position,
        out TimeSpan duration)
    {
        position = TimeSpan.Zero;
        duration = TimeSpan.Zero;
        if (candidates.Count < 2) return false;

        System.Windows.Rect windowBounds;
        try { windowBounds = window.Current.BoundingRectangle; }
        catch { return false; }
        if (windowBounds.IsEmpty) return false;

        var bestScore = double.MinValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = 0; j < candidates.Count; j++)
            {
                if (i == j) continue;
                var current = candidates[i];
                var total = candidates[j];
                if (current.Bounds.Left >= total.Bounds.Left ||
                    Math.Abs(current.Bounds.Top - total.Bounds.Top) > 18 ||
                    total.Value <= TimeSpan.FromSeconds(20) ||
                    total.Value > TimeSpan.FromHours(8) ||
                    current.Value > total.Value + TimeSpan.FromSeconds(2))
                {
                    continue;
                }

                var centerY = (current.Bounds.Top + total.Bounds.Top) / 2;
                if (centerY < windowBounds.Top + windowBounds.Height * 0.55) continue;
                var horizontalGap = total.Bounds.Left - current.Bounds.Right;
                if (horizontalGap < 20 || horizontalGap > windowBounds.Width * 0.95) continue;

                // Player time labels are normally the lowest same-row pair in the window.
                var score = centerY + Math.Min(horizontalGap, 800) * 0.05;
                if (score <= bestScore) continue;
                bestScore = score;
                position = current.Value;
                duration = total.Value;
            }
        }

        return bestScore > double.MinValue;
    }

    internal static bool TryParseCombinedProgress(
        string? text,
        out TimeSpan position,
        out TimeSpan duration)
    {
        position = TimeSpan.Zero;
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var match = CombinedProgressRegex().Match(text);
        if (!match.Success ||
            !TryParseClock(match.Groups[1].Value, out position) ||
            !TryParseClock(match.Groups[2].Value, out duration))
        {
            return false;
        }

        return duration > TimeSpan.Zero &&
               duration <= TimeSpan.FromHours(8) &&
               position <= duration + TimeSpan.FromSeconds(2);
    }

    private static bool TryParseSingleTime(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        return SingleTimeRegex().IsMatch(text) && TryParseClock(text, out value);
    }

    private static bool TryParseClock(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return false;
        if (!parts.All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return false;

        var values = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        if (values[^1] >= 60 || (values.Length == 3 && values[1] >= 60)) return false;
        value = values.Length == 2
            ? TimeSpan.FromMinutes(values[0]) + TimeSpan.FromSeconds(values[1])
            : TimeSpan.FromHours(values[0]) + TimeSpan.FromMinutes(values[1]) + TimeSpan.FromSeconds(values[2]);
        return value >= TimeSpan.Zero;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _wake.Set();
        if (Thread.CurrentThread != _worker && _worker.IsAlive) _worker.Join(1000);
        _wake.Dispose();
    }

    private sealed record TimeElement(TimeSpan Value, System.Windows.Rect Bounds);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [GeneratedRegex(@"(?<!\d)(\d{1,3}:[0-5]\d(?::[0-5]\d)?)\s*[/|／]\s*(\d{1,3}:[0-5]\d(?::[0-5]\d)?)(?!\d)")]
    private static partial Regex CombinedProgressRegex();

    [GeneratedRegex(@"^\d{1,3}:[0-5]\d(?::[0-5]\d)?$")]
    private static partial Regex SingleTimeRegex();
}
