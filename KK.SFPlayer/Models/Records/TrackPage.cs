using System.Collections.Generic;

namespace KK.SFPlayer.Models.Records
{
    public sealed record TrackPage(IReadOnlyList<TrackInfo> Tracks, int NextOffset, bool HasMore, int Total, long? TotalDurationMs = null);
}
