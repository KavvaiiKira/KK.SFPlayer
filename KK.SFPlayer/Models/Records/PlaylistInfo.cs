namespace KK.SFPlayer.Models.Records
{
    public sealed record PlaylistInfo(string Id, string Uri, string Name, string Owner, string? ImageUrl, string? SnapshotId = null);
}
