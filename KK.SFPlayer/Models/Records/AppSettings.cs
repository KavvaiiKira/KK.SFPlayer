using System.Collections.Generic;
using KK.SFPlayer.Constants;

namespace KK.SFPlayer.Models.Records
{
    internal sealed record AppSettings
    {
        public string ClientId { get; init; } = string.Empty;

        public bool StartWithWindows { get; init; }

        public bool AlwaysOnTop { get; init; }

        public bool ShuffleEnabled { get; init; }

        public int RepeatMode { get; init; }

        public double Volume { get; init; } = StreamingConstants.DefaultVolume * 100;

        public double WindowWidth { get; init; } = WindowConstants.MinimumWidth;

        public double WindowHeight { get; init; } = WindowConstants.MinimumHeight;

        public Dictionary<string, int> PlaylistSorts { get; init; } = [];
    }
}
