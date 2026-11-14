using System.Collections.Generic;

namespace KK.SFPlayer.Models
{
    public sealed record PlaylistInfo(string Id, string Uri, string Name, string Owner, string? ImageUrl);

    public sealed record TrackInfo(string Uri, string Name, string Artists, string Duration, string? ImageUrl, int Position, int DurationMs = 0)
    {
        public string DisplayName => $"{Name} — {Artists}";
    }

    public sealed record PlaylistPreview(IReadOnlyList<TrackInfo> Tracks, string TotalDuration);

    public sealed record PlaylistPage(IReadOnlyList<PlaylistInfo> Playlists, int NextOffset, bool HasMore);

    internal sealed record AppSettings
    {
        public string ClientId { get; init; } = string.Empty;

        public bool StartWithWindows { get; init; }

        public bool AlwaysOnTop { get; init; }
    }
}
