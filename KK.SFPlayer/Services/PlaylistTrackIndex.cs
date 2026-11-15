using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using KK.SFPlayer.Models;
using KK.SFPlayer.Models.Records;

namespace KK.SFPlayer.Services
{
    internal sealed class PlaylistTrackIndex(IReadOnlyList<TrackInfo> tracks, string snapshotId = "")
    {
        private readonly ConcurrentDictionary<int, TrackInfo[]> _orders = new();

        public string SnapshotId { get; } = snapshotId;

        public IReadOnlyList<TrackInfo> Tracks { get; } = tracks;

        public long DurationMs { get; } = tracks.Sum(track => (long)track.DurationMs);

        public TrackInfo[] GetOrder(int sort) => _orders.GetOrAdd(sort, value => Tracks.Order(TrackSorting.For(value)).ToArray());
    }
}
