using SpotifyAPI.Web;

namespace KK.SFPlayer.Models.Records
{
    internal sealed record SavedToken(string ClientId, PKCETokenResponse Token);
}
