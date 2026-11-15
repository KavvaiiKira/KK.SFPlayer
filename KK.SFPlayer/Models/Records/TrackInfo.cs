using System;

namespace KK.SFPlayer.Models.Records
{
    public sealed record TrackInfo(string Uri, string Name, string Artists, string Duration, string? ImageUrl, int Position, int DurationMs = 0, DateTime? AddedAt = null)
    {
        public string DisplayName => $"{Name} — {Artists}";
    }
}
