using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeteaseLyricsOverlay;

internal sealed record NeteaseElogPlaybackSnapshot(
    long SongId,
    string Title,
    string Artist,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPlaying,
    DateTimeOffset LastEventAt);

/// <summary>
/// Reads NetEase Cloud Music's local event log. Recent desktop releases no longer
/// publish a usable Windows media timeline, but still record track, seek and
/// play/pause events in cloudmusic.elog. The log is read only; no injection or
/// modification of the player process is involved.
/// </summary>
internal sealed partial class NeteaseElogPlaybackReader : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetEase", "CloudMusic", "cloudmusic.elog");
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private string _pendingText = string.Empty;
    private long _fileOffset;
    private bool _initialized;
    private bool _disposed;

    private bool _available;
    private long _songId = -1;
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private double _positionSeconds;
    private double _durationSeconds;
    private bool _isPlaying;
    private DateTimeOffset _anchorTime;
    private DateTimeOffset _lastEventAt;

    public NeteaseElogPlaybackSnapshot? Read()
    {
        lock (_gate)
        {
            if (_disposed || !File.Exists(_path) || !IsNeteaseRunning()) return null;

            try
            {
                RefreshFromFile();
                if (!_available || _songId < 0) return null;

                var now = DateTimeOffset.Now;
                var seconds = PositionAt(now);
                if (_durationSeconds > 0) seconds = Math.Min(seconds, _durationSeconds);
                return new NeteaseElogPlaybackSnapshot(
                    _songId,
                    _title,
                    _artist,
                    TimeSpan.FromSeconds(Math.Max(0, seconds)),
                    TimeSpan.FromSeconds(Math.Max(0, _durationSeconds)),
                    _isPlaying,
                    _lastEventAt);
            }
            catch (Exception exception)
            {
                AppDiagnostics.Write("NeteaseElogRead", exception);
                return null;
            }
        }
    }

    private void RefreshFromFile()
    {
        using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.SequentialScan);

        if (!_initialized || stream.Length < _fileOffset)
        {
            ResetParser();
            _initialized = true;
        }

        if (stream.Length == _fileOffset) return;
        stream.Position = _fileOffset;
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            DecodeInPlace(buffer, read);
            var characters = new char[_decoder.GetCharCount(buffer, 0, read, flush: false)];
            var characterCount = _decoder.GetChars(
                buffer, 0, read, characters, 0, flush: false);
            ConsumeText(new string(characters, 0, characterCount));
            _fileOffset += read;
        }
    }

    private void ConsumeText(string text)
    {
        text = _pendingText + text;
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0) break;
            ProcessLine(text.AsSpan(start, newline - start).Trim().ToString());
            start = newline + 1;
        }

        _pendingText = start < text.Length ? text[start..] : string.Empty;
        // A corrupt or partial file should not retain an unbounded buffer forever.
        if (_pendingText.Length > 1024 * 1024) _pendingText = _pendingText[^4096..];
    }

    private void ProcessLine(string line)
    {
        if (line.Length == 0 || !TryGetEventTime(line, out var eventTime)) return;

        if (line.Contains("【app】,{\"actionId\":\"exitApp\"}", StringComparison.Ordinal))
        {
            ResetPlaybackState();
            return;
        }

        if (line.Contains("【playing】,\"setPlayingPosition\"", StringComparison.Ordinal))
        {
            var match = PositionRegex().Match(line);
            if (!match.Success ||
                !double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var position)) return;
            _positionSeconds = Math.Max(0, position);
            _anchorTime = eventTime;
            _lastEventAt = eventTime;
            return;
        }

        if (line.Contains("【playing】,\"native播放state\"", StringComparison.Ordinal))
        {
            var match = StatusRegex().Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var status)) return;
            _positionSeconds = PositionAt(eventTime);
            _anchorTime = eventTime;
            _isPlaying = status == 1;
            _lastEventAt = eventTime;
            return;
        }

        if (line.Contains("【playing】,\"playOneTrackInPlayingList\"", StringComparison.Ordinal))
        {
            if (TryReadTrack(line, preferNestedTrack: true, out var track))
                SetTrack(track, eventTime, isPlaying: true);
            return;
        }

        if (line.Contains("【playing】,\"checkPlayPrivilege\",", StringComparison.Ordinal))
        {
            if (TryReadTrack(line, preferNestedTrack: false, out var track) && track.Id != _songId)
                SetTrack(track, eventTime, isPlaying: false);
            return;
        }

        if (line.Contains("【playing】,\"native播放资源load完成，开始播放\"", StringComparison.Ordinal))
        {
            var match = SongIdRegex().Match(line);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, out var songId)) return;
            if (songId != _songId)
                SetTrack(new ParsedTrack(songId, string.Empty, string.Empty, 0), eventTime, isPlaying: true);
        }
    }

    private void SetTrack(ParsedTrack track, DateTimeOffset eventTime, bool isPlaying)
    {
        _available = true;
        _songId = track.Id;
        if (!string.IsNullOrWhiteSpace(track.Title)) _title = track.Title.Trim();
        if (!string.IsNullOrWhiteSpace(track.Artist)) _artist = track.Artist.Trim();
        if (track.DurationSeconds > 0) _durationSeconds = track.DurationSeconds;
        _positionSeconds = 0;
        _anchorTime = eventTime;
        _lastEventAt = eventTime;
        _isPlaying = isPlaying;
    }

    private double PositionAt(DateTimeOffset time)
    {
        if (!_isPlaying || _anchorTime == default) return _positionSeconds;
        return Math.Max(0, _positionSeconds + Math.Max(0, (time - _anchorTime).TotalSeconds));
    }

    private static bool TryReadTrack(string line, bool preferNestedTrack, out ParsedTrack track)
    {
        track = default;
        var jsonMatch = JsonRegex().Match(line);
        if (!jsonMatch.Success) return false;
        try
        {
            using var document = JsonDocument.Parse(jsonMatch.Value);
            var root = document.RootElement;
            if (preferNestedTrack && root.TryGetProperty("track", out var nested)) root = nested;
            if (!TryReadInt64(root, "id", out var id) || id < 0) return false;

            var title = TryReadString(root, "name");
            var durationMilliseconds = TryReadDouble(root, "duration");
            var artists = new List<string>();
            if (root.TryGetProperty("artists", out var artistArray) &&
                artistArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in artistArray.EnumerateArray())
                {
                    var name = TryReadString(item, "name");
                    if (!string.IsNullOrWhiteSpace(name)) artists.Add(name);
                }
            }

            track = new ParsedTrack(
                id, title, string.Join(" / ", artists),
                durationMilliseconds > 0 ? durationMilliseconds / 1000.0 : 0);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetEventTime(string line, out DateTimeOffset eventTime)
    {
        eventTime = default;
        var match = HeaderRegex().Match(line);
        if (!match.Success) return false;
        if (!DateTime.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)) return false;
        eventTime = new DateTimeOffset(local);
        return true;
    }

    private static string TryReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static double TryReadDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            ? number
            : 0;
    }

    private static bool TryReadInt64(JsonElement element, string property, out long value)
    {
        value = -1;
        if (!element.TryGetProperty(property, out var item)) return false;
        if (item.ValueKind == JsonValueKind.Number) return item.TryGetInt64(out value);
        return item.ValueKind == JsonValueKind.String && long.TryParse(item.GetString(), out value);
    }

    internal static void DecodeInPlace(byte[] buffer, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var value = buffer[index];
            var highNibble = ((value / 16) ^ ((value % 16) + 8)) % 16;
            buffer[index] = (byte)(
                highNibble * 16 + value / 64 * 4 + ((~(value / 16)) & 3));
        }
    }

    private void ResetParser()
    {
        _decoder = Encoding.UTF8.GetDecoder();
        _pendingText = string.Empty;
        _fileOffset = 0;
        ResetPlaybackState();
    }

    private void ResetPlaybackState()
    {
        _available = false;
        _songId = -1;
        _title = string.Empty;
        _artist = string.Empty;
        _positionSeconds = 0;
        _durationSeconds = 0;
        _isPlaying = false;
        _anchorTime = default;
        _lastEventAt = default;
    }

    private static bool IsNeteaseRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("cloudmusic");
            try { return processes.Length > 0; }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
    }

    private readonly record struct ParsedTrack(
        long Id, string Title, string Artist, double DurationSeconds);

    [GeneratedRegex(@"^\[.*?\]\s+\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex("【playing】,\\\"setPlayingPosition\\\",(\\d+(?:\\.\\d+)?)")]
    private static partial Regex PositionRegex();

    [GeneratedRegex("【playing】,\\\"native播放state\\\",(\\d+),")]
    private static partial Regex StatusRegex();

    [GeneratedRegex("\\\"songId\\\"\\s*:\\s*\\\"?(\\d+)\\\"?")]
    private static partial Regex SongIdRegex();

    [GeneratedRegex(@"\{.*\}")]
    private static partial Regex JsonRegex();
}
