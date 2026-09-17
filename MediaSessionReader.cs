using Windows.Media.Control;
using HorizonRadioOverlay.Models;
using HorizonRadioOverlay.Services;

namespace NeteaseLyricsOverlay;

internal sealed record PlaybackSnapshot(
    string Title,
    string Artist,
    TimeSpan Position,
    bool IsPlaying,
    DateTimeOffset LastUpdatedTime,
    TimeSpan EndTime,
    string PlaybackStatus,
    string SourceId,
    bool HasReliablePosition,
    string PositionSource,
    DateTimeOffset PositionCapturedAt,
    long? SongId);

internal sealed class MediaSessionReader : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly NeteaseProgressReader _progressReader = new();
    private readonly NeteaseElogPlaybackReader _elogReader = new();
    private readonly NeteaseMemoryPlaybackProbe _memoryProbe = new(new DiagnosticService { Enabled = true });
    private bool _disposed;

    public async Task<PlaybackSnapshot?> ReadAsync()
    {
        var candidates = await ReadAllAsync();
        return candidates
            .OrderByDescending(x => x.IsPlaying)
            .ThenByDescending(x => string.Equals(x.PlaybackStatus, "Paused", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.LastUpdatedTime)
            .ThenByDescending(x => x.Position)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<PlaybackSnapshot>> ReadAllAsync()
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await ReadAllCoreAsync();
            }
            catch (Exception exception)
            {
                lastError = exception;
                _manager = null;
                AppDiagnostics.Write("MediaSessionReconnect", exception);
            }
        }

        throw lastError ?? new InvalidOperationException("无法连接 Windows 媒体会话。");
    }

    private async Task<IReadOnlyList<PlaybackSnapshot>> ReadAllCoreAsync()
    {
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var result = new List<PlaybackSnapshot>();
        foreach (var session in _manager.GetSessions().Where(IsNeteaseSession))
        {
            try
            {
                var media = await session.TryGetMediaPropertiesAsync();
                if (string.IsNullOrWhiteSpace(media.Title)) continue;

                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                var position = timeline.Position;
                if (timeline.EndTime > TimeSpan.Zero && position > timeline.EndTime) position = timeline.EndTime;
                var systemTimelineIsReliable = timeline.EndTime > TimeSpan.Zero &&
                                               timeline.LastUpdatedTime.Year >= 2000;
                result.Add(new PlaybackSnapshot(
                    media.Title.Trim(),
                    media.Artist.Trim(),
                    position,
                    playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    timeline.LastUpdatedTime,
                    timeline.EndTime,
                    playback.PlaybackStatus.ToString(),
                    session.SourceAppUserModelId ?? string.Empty,
                    systemTimelineIsReliable,
                    systemTimelineIsReliable ? "Windows 媒体时间轴" : "未提供",
                    timeline.LastUpdatedTime,
                    null));
            }
            catch
            {
                // A player process can disappear while sessions are being enumerated.
            }
        }

        var primary = result
            .OrderByDescending(x => x.IsPlaying)
            .ThenByDescending(x => string.Equals(x.PlaybackStatus, "Paused", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        var elogState = await Task.Run(_elogReader.Read);
        if (elogState is not null)
        {
            var capturedAt = DateTimeOffset.UtcNow;
            var title = !string.IsNullOrWhiteSpace(elogState.Title)
                ? elogState.Title
                : primary?.Title ?? string.Empty;
            var metadataMatchesLog = primary is not null &&
                                     string.Equals(primary.Title, title, StringComparison.OrdinalIgnoreCase);
            var artist = metadataMatchesLog && !string.IsNullOrWhiteSpace(primary!.Artist)
                ? primary.Artist
                : !string.IsNullOrWhiteSpace(elogState.Artist)
                    ? elogState.Artist
                    : primary?.Artist ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(title))
            {
                var duration = elogState.Duration > TimeSpan.Zero
                    ? elogState.Duration
                    : primary?.EndTime ?? TimeSpan.Zero;
                return
                [
                    new PlaybackSnapshot(
                        title,
                        artist,
                        elogState.Position,
                        elogState.IsPlaying,
                        elogState.LastEventAt,
                        duration,
                        elogState.IsPlaying ? "Playing" : "Paused",
                        "cloudmusic.elog",
                        true,
                        "网易云本地播放日志",
                        capturedAt,
                        elogState.SongId)
                ];
            }
        }

        if (primary is null)
        {
            _memoryProbe.SetTrackContext(null);
            _progressReader.SetTrack(null);
            return result;
        }

        var trackKey = $"{primary.Title}\n{primary.Artist}";
        _memoryProbe.SetTrackContext(new TrackInfo
        {
            Name = primary.Title,
            Artist = primary.Artist,
            DurationSeconds = primary.EndTime.TotalSeconds
        });
        var memoryState = await _memoryProbe.GetPlaybackStateAsync();
        if (memoryState is { } memory)
        {
            var capturedAt = DateTimeOffset.UtcNow;
            return result.Select(candidate => candidate with
            {
                Position = memory.Position,
                LastUpdatedTime = capturedAt,
                IsPlaying = memory.IsPlaying,
                HasReliablePosition = true,
                PositionSource = "网易云只读时间轴",
                PositionCapturedAt = capturedAt
            }).ToArray();
        }

        _progressReader.SetTrack(trackKey);
        if (!_progressReader.TryGetLatest(trackKey, out var progress)) return result;

        return result.Select(candidate => candidate with
        {
            Position = progress.Position,
            EndTime = progress.Duration,
            LastUpdatedTime = progress.CapturedAt,
            IsPlaying = candidate.IsPlaying || progress.IsMoving,
            HasReliablePosition = true,
            PositionSource = progress.Source,
            PositionCapturedAt = progress.CapturedAt
        }).ToArray();
    }

    public void InvalidateSession() => _manager = null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _elogReader.Dispose();
        _progressReader.Dispose();
    }

    private static bool IsNeteaseSession(GlobalSystemMediaTransportControlsSession session)
    {
        var id = session.SourceAppUserModelId ?? string.Empty;
        return id.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("netease", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("网易云", StringComparison.OrdinalIgnoreCase);
    }
}
