using System;
using System.Collections.Generic;

namespace KK.SFPlayer.Models
{
    public sealed record PlaylistInfo(string Id, string Uri, string Name, string Owner, string? ImageUrl);

    public sealed record TrackInfo(string Uri, string Name, string Artists, string Duration, string? ImageUrl, int Position, int DurationMs = 0, DateTime? AddedAt = null)
    {
        public string DisplayName => $"{Name} — {Artists}";
    }

    public sealed record PlaylistPreview(IReadOnlyList<TrackInfo> Tracks, string TotalDuration);

    public sealed record PlaylistPage(IReadOnlyList<PlaylistInfo> Playlists, int NextOffset, bool HasMore);

    public sealed record TrackPage(IReadOnlyList<TrackInfo> Tracks, int NextOffset, bool HasMore, int Total);

    internal sealed record AppSettings
    {
        public string ClientId { get; init; } = string.Empty;

        public bool StartWithWindows { get; init; }

        public bool AlwaysOnTop { get; init; }

        public Dictionary<string, int> PlaylistSorts { get; init; } = [];
    }
}
