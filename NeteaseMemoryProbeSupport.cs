// Compatibility types used by the MIT-licensed NetEase memory timeline probe.
// See THIRD-PARTY-NOTICES.txt for attribution.

namespace HorizonRadioOverlay.Models
{
    public sealed class TrackInfo
    {
        public required string Name { get; init; }
        public required string Artist { get; init; }
        public double DurationSeconds { get; init; }
    }
}

namespace HorizonRadioOverlay.Services
{
    using HorizonRadioOverlay.Models;

    public interface IPlaybackStateProvider
    {
        Task<(TimeSpan Position, bool IsPlaying)?> GetPlaybackStateAsync();
    }

    public interface ITrackAwarePlaybackStateProvider : IPlaybackStateProvider
    {
        void SetTrackContext(TrackInfo? track);
    }

    public static class TrackIdentity
    {
        public static string BuildNeteaseTrackKey(TrackInfo track) =>
            $"{track.Name.Trim()}|{track.Artist.Trim()}";
    }

    public sealed class DiagnosticService
    {
        public bool Enabled { get; set; }
        public void Info(string message)
        {
            if (Enabled) NeteaseLyricsOverlay.AppDiagnostics.Write("MemoryTimeline", message);
        }
    }
}
