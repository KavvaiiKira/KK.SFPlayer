using System.Collections.Generic;

namespace KK.SFPlayer.Models.Records
{
    internal sealed record AppSettings
    {
        public string ClientId { get; init; } = string.Empty;

        public bool StartWithWindows { get; init; }

        public bool AlwaysOnTop { get; init; }

        public Dictionary<string, int> PlaylistSorts { get; init; } = [];
    }
}
