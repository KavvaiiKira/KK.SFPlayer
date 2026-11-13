namespace KK.SFPlayer.Models
{
    public sealed record PlaylistInfo(string Id, string Uri, string Name, string Owner, string? ImageUrl);

    public sealed record TrackInfo(string Uri, string Name, string Artists, string Duration, string? ImageUrl, int Position);

    internal sealed record AppSettings
    {
        public string ClientId { get; init; } = string.Empty;

        public bool StartWithWindows { get; init; }

        public bool AlwaysOnTop { get; init; }
    }
}
