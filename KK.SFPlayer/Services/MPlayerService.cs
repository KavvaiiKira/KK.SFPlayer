using KK.SFPlayer.Constants;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Models;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;

namespace KK.SFPlayer.Services
{
    internal sealed class MPlayerService : IMPlayerService
    {
        private const string ListenerPrefix = "http://127.0.0.1:5543/";
        private const string CallbackPath = "/callback/";
        private const int PageSize = 50;
        private const int AuthorizationTimeoutSeconds = 180;

        private readonly SettingsStore _settings;
        private readonly SpotifyClientConfig _configuration;
        private readonly OAuthClient _oauth;
        private readonly HttpClient? _httpClient;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private PKCETokenResponse? _token;
        private string _clientId = string.Empty;
        private SpotifyClient? _client;

        public MPlayerService(SettingsStore settings, SpotifyClientConfig? configuration = null)
        {
            _settings = settings;
            if (configuration is null)
            {
                _httpClient = new HttpClient(new TransientRetryHandler()) { Timeout = TimeSpan.FromSeconds(20) };
                configuration = SpotifyClientConfig.CreateDefault().WithHTTPClient(new NetHttpClient(_httpClient));
            }

            _configuration = configuration;
            _oauth = new OAuthClient(_configuration);
        }

        public bool IsConnected => _client is not null;

        public async Task<bool> RestoreAsync(string clientId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return false;
            }

            _token = _settings.LoadToken(clientId);
            _client = null;
            if (_token is null)
            {
                return false;
            }

            _clientId = clientId;
            await GetClientAsync(cancellationToken);
            return true;
        }

        public async Task ConnectAsync(string clientId, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(AuthorizationTimeoutSeconds));
            using var listener = new HttpListener();
            listener.Prefixes.Add(ListenerPrefix);
            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                throw new InvalidOperationException(AuthenticationConstants.AuthListenerFailed);
            }

            using var registration = timeout.Token.Register(listener.Stop);
            var (verifier, challenge) = PKCEUtil.GenerateCodes();
            var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var request = new LoginRequest(new Uri(AuthenticationConstants.RedirectUri), clientId, LoginRequest.ResponseType.Code)
            {
                CodeChallenge = challenge,
                CodeChallengeMethod = "S256",
                State = state,
                Scope = [Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative, Scopes.UserModifyPlaybackState],
            };

            Process.Start(new ProcessStartInfo(request.ToUri().AbsoluteUri) { UseShellExecute = true });
            try
            {
                while (true)
                {
                    var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                    var query = context.Request.QueryString;
                    if (context.Request.Url?.AbsolutePath != CallbackPath || query["state"] != state)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                        context.Response.Close();
                        continue;
                    }

                    var code = query["code"];
                    var success = query["error"] is null && !string.IsNullOrWhiteSpace(code);
                    context.Response.ContentType = "text/plain; charset=utf-8";
                    var bytes = Encoding.UTF8.GetBytes(success ? AuthenticationConstants.AuthCallbackSuccess : AuthenticationConstants.AuthCallbackFailed);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, timeout.Token);
                    context.Response.Close();
                    if (!success)
                    {
                        throw new InvalidOperationException(AuthenticationConstants.AuthDenied);
                    }

                    var token = await _oauth.RequestToken(
                        new PKCETokenRequest(clientId, code!, new Uri(AuthenticationConstants.RedirectUri), verifier),
                        timeout.Token);
                    _settings.SaveToken(clientId, token);
                    _clientId = clientId;
                    _token = token;
                    _client = new SpotifyClient(_configuration.WithToken(token.AccessToken));
                    return;
                }
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                timeout.IsCancellationRequested &&
                exception is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                throw new InvalidOperationException(AuthenticationConstants.AuthTimeout);
            }
            catch (Exception exception) when (
                cancellationToken.IsCancellationRequested &&
                exception is HttpListenerException or ObjectDisposedException)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        public void Disconnect()
        {
            _client = null;
            _token = null;
            _clientId = string.Empty;
            _settings.ClearToken();
        }

        public async Task<IReadOnlyList<PlaylistInfo>> GetPlaylistsAsync(CancellationToken cancellationToken)
        {
            var client = await GetClientAsync(cancellationToken);
            var result = new List<PlaylistInfo>();
            var offset = 0;
            while (true)
            {
                var page = await client.Playlists.CurrentUsers(
                    new PlaylistCurrentUsersRequest { Limit = PageSize, Offset = offset },
                    cancellationToken);
                var items = page.Items ?? [];
                foreach (var item in items)
                {
                    if (item is null || string.IsNullOrEmpty(item.Id))
                    {
                        continue;
                    }

                    result.Add(new PlaylistInfo(
                        item.Id,
                        item.Uri ?? $"spotify:playlist:{item.Id}",
                        item.Name ?? PlaybackConstants.UnnamedPlaylist,
                        item.Owner?.DisplayName ?? string.Empty,
                        item.Images?.FirstOrDefault()?.Url));
                }

                if (string.IsNullOrEmpty(page.Next) || items.Count == 0)
                {
                    return result;
                }

                offset += items.Count;
            }
        }

        public async Task<IReadOnlyList<TrackInfo>> GetTracksAsync(string playlistId, CancellationToken cancellationToken)
        {
            var client = await GetClientAsync(cancellationToken);
            var result = new List<TrackInfo>();
            var offset = 0;
            while (true)
            {
                var page = await client.Playlists.GetPlaylistItems(
                    playlistId,
                    new PlaylistGetItemsRequest { Limit = PageSize, Offset = offset },
                    cancellationToken);
                var items = page.Items ?? [];
                for (var index = 0; index < items.Count; index++)
                {
                    var track = MapTrack(items[index], offset + index);
                    if (track is null)
                    {
                        continue;
                    }

                    result.Add(track);
                }

                if (string.IsNullOrEmpty(page.Next) || items.Count == 0)
                {
                    return result;
                }

                offset += items.Count;
            }
        }

        public async Task<PlaylistPage> GetPlaylistPageAsync(int offset, CancellationToken cancellationToken)
        {
            var client = await GetClientAsync(cancellationToken);
            var page = await client.Playlists.CurrentUsers(
                new PlaylistCurrentUsersRequest { Limit = PlaylistConstants.PageSize, Offset = offset },
                cancellationToken);
            var items = page.Items ?? [];
            var playlists = items
                .Where(item => item is not null && !string.IsNullOrEmpty(item.Id))
                .Select(item => new PlaylistInfo(
                    item.Id!,
                    item.Uri ?? $"spotify:playlist:{item.Id}",
                    item.Name ?? PlaybackConstants.UnnamedPlaylist,
                    item.Owner?.DisplayName ?? string.Empty,
                    item.Images?.FirstOrDefault()?.Url))
                .ToArray();
            return new PlaylistPage(playlists, offset + items.Count, items.Count > 0 && !string.IsNullOrEmpty(page.Next));
        }

        public async Task<PlaylistPreview> GetTrackPreviewAsync(string playlistId, CancellationToken cancellationToken)
        {
            var client = await GetClientAsync(cancellationToken);
            var page = await client.Playlists.GetPlaylistItems(
                playlistId,
                new PlaylistGetItemsRequest { Limit = PlaylistConstants.PreviewTrackLimit, Offset = 0 },
                cancellationToken);
            var items = page.Items ?? [];
            var tracks = new List<TrackInfo>();
            for (var index = 0; index < Math.Min(items.Count, PlaylistConstants.PreviewTrackLimit); index++)
            {
                var track = MapTrack(items[index], index);
                if (track is not null)
                {
                    tracks.Add(track);
                }
            }

            var isComplete = string.IsNullOrEmpty(page.Next) && page.Total == items.Count && tracks.Count == items.Count;
            var totalDuration = isComplete ? FormatDuration(tracks.Sum(track => (long)track.DurationMs)) : string.Empty;
            return new PlaylistPreview(tracks, totalDuration);
        }

        private static TrackInfo? MapTrack(PlaylistTrack<IPlayableItem>? entry, int position)
        {
            if (entry is null || entry.IsLocal ||
                (entry.Item ?? entry.Track) is not FullTrack track ||
                string.IsNullOrEmpty(track.Uri))
            {
                return null;
            }

            return new TrackInfo(
                track.Uri,
                track.Name ?? PlaybackConstants.UnnamedTrack,
                track.Artists is { Count: > 0 } ?
                    string.Join(", ", track.Artists.Select(artist => artist.Name)) :
                    PlaybackConstants.UnknownArtist,
                FormatDuration(track.DurationMs),
                track.Album?.Images?.FirstOrDefault()?.Url,
                position,
                track.DurationMs);
        }

        private static string FormatDuration(long milliseconds)
        {
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)duration.TotalMinutes, duration.Seconds);
        }

        public async Task PlayAsync(string playlistUri, int? position, CancellationToken cancellationToken)
        {
            var client = await GetClientAsync(cancellationToken);
            await client.Player.ResumePlayback(new PlayerResumePlaybackRequest
            {
                ContextUri = playlistUri,
                OffsetParam = position.HasValue ? new PlayerResumePlaybackRequest.Offset { Position = position.Value } : null,
            }, cancellationToken);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
            _tokenLock.Dispose();
        }

        private async Task<SpotifyClient> GetClientAsync(CancellationToken cancellationToken)
        {
            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                if (_token is null)
                {
                    throw new InvalidOperationException(AuthenticationConstants.NotConnected);
                }

                if (_token.IsExpired)
                {
                    var refreshed = await _oauth.RequestToken(
                        new PKCETokenRefreshRequest(_clientId, _token.RefreshToken),
                        cancellationToken);
                    if (string.IsNullOrEmpty(refreshed.RefreshToken))
                    {
                        refreshed.RefreshToken = _token.RefreshToken;
                    }

                    _settings.SaveToken(_clientId, refreshed);
                    _token = refreshed;
                    _client = new SpotifyClient(_configuration.WithToken(refreshed.AccessToken));
                }

                return _client ??= new SpotifyClient(_configuration.WithToken(_token.AccessToken));
            }
            finally
            {
                _tokenLock.Release();
            }
        }
    }
}
