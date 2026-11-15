namespace KK.SFPlayer.Models.Records
{
    public sealed record PlaybackProgress(int PositionMs, int DurationMs, bool CanSeek, string? TrackUri, string? ContextUri = null, string? TrackName = null, string? Artists = null, string? OriginalTrackUri = null, string? ArtworkUrl = null);
}
