using System.Collections.Generic;

namespace KK.SFPlayer.Models.Records
{
    internal sealed record CachedPlaylistData(string PlaylistId, string SnapshotId, IReadOnlyList<TrackInfo> Tracks, int FormatVersion = 1);
}
