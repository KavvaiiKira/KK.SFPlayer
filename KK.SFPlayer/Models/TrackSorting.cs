using System;
using System.Collections.Generic;

namespace KK.SFPlayer.Models
{
    internal static class TrackSorting
    {
        public static IComparer<TrackInfo> For(int sort) => Comparer<TrackInfo>.Create((left, right) => Compare(sort, left, right));

        public static int Compare(int sort, TrackInfo left, TrackInfo right)
        {
            var comparison = sort switch
            {
                1 => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name),
                2 => StringComparer.CurrentCultureIgnoreCase.Compare(left.Artists, right.Artists),
                3 => left.DurationMs.CompareTo(right.DurationMs),
                4 => Nullable.Compare(right.AddedAt, left.AddedAt),
                5 => Nullable.Compare(left.AddedAt, right.AddedAt),
                _ => 0,
            };
            return comparison == 0 ? left.Position.CompareTo(right.Position) : comparison;
        }
    }
}
