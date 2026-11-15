using System.Collections.Generic;

namespace KK.SFPlayer.Models.Records
{
    public sealed record PlaylistPage(IReadOnlyList<PlaylistInfo> Playlists, int NextOffset, bool HasMore);
}
