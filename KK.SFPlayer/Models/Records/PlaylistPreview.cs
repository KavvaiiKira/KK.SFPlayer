using System.Collections.Generic;

namespace KK.SFPlayer.Models.Records
{
    public sealed record PlaylistPreview(IReadOnlyList<TrackInfo> Tracks, string TotalDuration);
}
