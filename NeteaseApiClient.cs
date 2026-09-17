using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeteaseLyricsOverlay;

internal sealed class NeteaseApiClient
{
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public NeteaseApiClient()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "NeteaseLyricsOverlay/1.0 (Windows desktop lyrics overlay)");
        _http.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
    }

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(
        string title,
        string artist,
        long? songId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<LyricLine> neteaseLyrics = [];
        try
        {
            neteaseLyrics = songId is > 0
                ? await GetNeteaseLyricsByIdAsync(songId.Value, cancellationToken)
                : await GetNeteaseLyricsBySearchAsync(title, artist, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // LRCLIB is the fallback when the official NetEase lyrics are unavailable.
        }
        if (neteaseLyrics.Count > 0) return neteaseLyrics;

        try
        {
            return await GetLrcLibLyricsAsync(title, artist, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<LyricLine>> GetNeteaseLyricsBySearchAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        var songId = await FindSongIdAsync(title, artist, cancellationToken);
        if (songId is null) return [];
        return await GetNeteaseLyricsByIdAsync(songId.Value, cancellationToken);
    }

    private async Task<IReadOnlyList<LyricLine>> GetNeteaseLyricsByIdAsync(
        long songId,
        CancellationToken cancellationToken)
    {
        var url = $"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=1";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        var original = ReadNestedString(root, "lrc", "lyric");
        var translated = ReadNestedString(root, "tlyric", "lyric");
        return LrcParser.Parse(original, translated);
    }

    private async Task<IReadOnlyList<LyricLine>> GetLrcLibLyricsAsync(
        string title,
        string artist,
        CancellationToken cancellationToken)
    {
        var url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(CleanTitle(title)) +
                  "&artist_name=" + Uri.EscapeDataString(artist);
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

        var wantedTitle = Normalize(CleanTitle(title));
        var wantedArtist = Normalize(artist);
        string? bestLyrics = null;
        var bestScore = int.MinValue;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("syncedLyrics", out var lyricsElement) ||
                string.IsNullOrWhiteSpace(lyricsElement.GetString()))
                continue;

            var candidateTitle = item.TryGetProperty("trackName", out var trackName)
                ? trackName.GetString() ?? string.Empty
                : string.Empty;
            var candidateArtist = item.TryGetProperty("artistName", out var artistName)
                ? artistName.GetString() ?? string.Empty
                : string.Empty;
            var score = SimilarityScore(wantedTitle, Normalize(CleanTitle(candidateTitle))) * 3 +
                        SimilarityScore(wantedArtist, Normalize(candidateArtist)) * 2;
            if (score > bestScore)
            {
                bestScore = score;
                bestLyrics = lyricsElement.GetString();
            }
        }

        return bestScore >= 350 ? LrcParser.Parse(bestLyrics, null) : [];
    }

    private async Task<long?> FindSongIdAsync(string title, string artist, CancellationToken cancellationToken)
    {
        var query = string.Join(' ', new[] { CleanTitle(title), artist }.Where(x => !string.IsNullOrWhiteSpace(x)));
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["s"] = query,
            ["type"] = "1",
            ["offset"] = "0",
            ["limit"] = "15"
        });
        using var response = await _http.PostAsync("https://music.163.com/api/search/get/web", form, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));

        if (!document.RootElement.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs))
            return null;

        var wantedTitle = Normalize(CleanTitle(title));
        var wantedArtist = Normalize(artist);
        long? bestId = null;
        var bestScore = int.MinValue;

        foreach (var song in songs.EnumerateArray())
        {
            if (!song.TryGetProperty("id", out var idElement) || !song.TryGetProperty("name", out var nameElement))
                continue;

            var candidateTitle = nameElement.GetString() ?? string.Empty;
            var candidateArtists = ReadArtists(song);
            var score = SimilarityScore(wantedTitle, Normalize(candidateTitle)) * 3 +
                        SimilarityScore(wantedArtist, Normalize(candidateArtists)) * 2;
            if (score > bestScore)
            {
                bestScore = score;
                bestId = idElement.GetInt64();
            }
        }

        return bestScore >= 400 ? bestId : null;
    }

    private static string ReadArtists(JsonElement song)
    {
        var names = new List<string>();
        foreach (var propertyName in new[] { "artists", "ar" })
        {
            if (!song.TryGetProperty(propertyName, out var artists) || artists.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var artist in artists.EnumerateArray())
                if (artist.TryGetProperty("name", out var name)) names.Add(name.GetString() ?? string.Empty);
            if (names.Count > 0) break;
        }
        return string.Join(' ', names);
    }

    private static int SimilarityScore(string wanted, string candidate)
    {
        if (string.IsNullOrEmpty(wanted)) return 0;
        if (wanted == candidate) return 100;
        if (candidate.Contains(wanted) || wanted.Contains(candidate)) return 70;

        var wantedParts = wanted.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return wantedParts.Length == 0 ? 0 : wantedParts.Count(candidate.Contains) * 40 / wantedParts.Length;
    }

    private static string CleanTitle(string value) =>
        Regex.Replace(value, @"\s*[\(（\[].*?(?:\)|）|\])\s*$", string.Empty).Trim();

    private static string Normalize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static string? ReadNestedString(JsonElement root, string objectName, string valueName)
    {
        return root.TryGetProperty(objectName, out var obj) &&
               obj.ValueKind == JsonValueKind.Object &&
               obj.TryGetProperty(valueName, out var value)
            ? value.GetString()
            : null;
    }
}
