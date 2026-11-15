using KK.SFPlayer.Constants;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Models;
using KK.SFPlayer.Models.Records;
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
        private readonly ILocalPlaybackEngine? _playback;
        private readonly PlaylistCacheStore _cache;
        private CancellationTokenSource _background = new();
        private readonly ConcurrentBag<Task> _backgroundTasks = new();
        private readonly ConcurrentDictionary<string, string> _snapshots = new();
        private readonly ConcurrentDictionary<string, Task> _refreshes = new();
        private readonly ConcurrentDictionary<string, bool> _stale = new();
        private bool _disposed;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private readonly SemaphoreSlim _indexLock = new(1, 1);
        private readonly ConcurrentDictionary<string, TrackIndex> _trackIndexes = new();
        private int _indexGeneration;
        private PKCETokenResponse? _token;
        private string _clientId = string.Empty;
        private SpotifyClient? _client;
        private string? _queuePlaylistUri;
        private HashSet<string> _queueTrackUris = [];
        private bool _shuffleRequested;
        private int _repeatRequested;
        private string? _playbackPlaylistUri;
        private string? _playbackTrackUri;
        private int? _playbackPosition;
        private int _playbackSort;
        private bool _isPlaying;
        private readonly SemaphoreSlim _skipLock = new(1, 1);

        private sealed class TrackIndex(IReadOnlyList<TrackInfo> tracks, string snapshotId = "")
        {
            public string SnapshotId { get; } = snapshotId;
            public IReadOnlyList<TrackInfo> Tracks { get; } = tracks;

            public ConcurrentDictionary<int, TrackInfo[]> Orders { get; } = new();

            public long DurationMs { get; } = tracks.Sum(track => (long)track.DurationMs);
        }

        public void InvalidateTrackCache(string? playlistId = null)
        {
            lock (_trackIndexes)
            {
                Interlocked.Increment(ref _indexGeneration);
                if (playlistId is null)
                {
                    _trackIndexes.Clear();
                }
                else
                {
                    _trackIndexes.TryRemove(playlistId, out _);
                }
            }
        }

        public MPlayerService(SettingsStore settings, SpotifyClientConfig? configuration = null, ILocalPlaybackEngine? playback = null)
        {
            _settings = settings;
            _cache = new PlaylistCacheStore(settings.DataDirectory);
            _playback = playback;
            if (_playback is not null)
            {
                _playback.PlayingChanged += OnPlayingChanged;
                _playback.ModesChanged += OnModesChanged;
                _playback.ProgressChanged += OnProgressChanged;
            }
            if (configuration is null)
            {
                _httpClient = new HttpClient(new TransientRetryHandler()) { Timeout = TimeSpan.FromSeconds(20) };
                configuration = SpotifyClientConfig.CreateDefault().WithHTTPClient(new NetHttpClient(_httpClient));
            }

            _configuration = configuration;
            _oauth = new OAuthClient(_configuration);
        }

        public bool IsConnected => _client is not null;

        public event EventHandler<bool>? PlayingChanged;

        public event EventHandler<PlaybackModes>? ModesChanged;

        public event EventHandler<PlaybackProgress>? ProgressChanged;

        public event EventHandler<string>? PlaylistChanged;

        public event EventHandler<string>? CacheWarning;

        public PlaylistPage? GetCachedPlaylists() => IsConnected ? _cache.LoadLibrary(_clientId) : null;

        public async Task EnsurePlaylistFreshAsync(string playlistId, CancellationToken cancellationToken)
        {
            if (_stale.ContainsKey(playlistId) && _refreshes.TryGetValue(playlistId, out var refresh))
            {
                await refresh.WaitAsync(cancellationToken);
            }
        }

        public async Task CheckPlaylistVersionAsync(string playlistId, CancellationToken cancellationToken)
        {
            var client = await GetClientAsync(cancellationToken);
            var playlist = await client.Playlists.Get(playlistId, new PlaylistGetRequest { Fields = { "snapshot_id" } }, cancellationToken);
            RegisterSnapshot(playlistId, playlist.SnapshotId);
        }

        private void RegisterSnapshot(string playlistId, string? snapshot)
        {
            if (string.IsNullOrEmpty(snapshot))
            {
                return;
            }

            _snapshots[playlistId] = snapshot;
            if (_trackIndexes.TryGetValue(playlistId, out var cached) && cached.SnapshotId != snapshot)
            {
                _stale[playlistId] = true;
                QueueRefresh(playlistId);
            }
        }

        private void QueueRefresh(string playlistId, TrackPage? seed = null)
        {
            if (_disposed || (_refreshes.TryGetValue(playlistId, out var pending) && !pending.IsCompleted))
            {
                return;
            }

            var lifetime = _background.Token;
            var task = RefreshIndexAsync(playlistId, seed, lifetime);
            _refreshes[playlistId] = task;
            _backgroundTasks.Add(task);
            _ = ObserveBackgroundAsync(task, lifetime);
        }

        private async Task ObserveBackgroundAsync(Task task, CancellationToken lifetime)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                if (!_disposed && !lifetime.IsCancellationRequested)
                {
                    CacheWarning?.Invoke(this, PlaylistConstants.CacheRefreshFailed);
                }
            }
        }

        private async Task RefreshIndexAsync(string playlistId, TrackPage? seed, CancellationToken cancellationToken)
        {
            var changed = _stale.ContainsKey(playlistId);
            await _indexLock.WaitAsync(cancellationToken);
            try
            {
                await BuildTrackIndexAsync(playlistId, seed, cancellationToken);
                _stale.TryRemove(playlistId, out _);
            }
            finally
            {
                _indexLock.Release();
            }

            if (changed && !_disposed && !cancellationToken.IsCancellationRequested)
            {
                PlaylistChanged?.Invoke(this, playlistId);
            }
        }

        private async Task ValidateOtherCachedPlaylistsAsync(IReadOnlySet<string> checkedIds, CancellationToken cancellationToken)
        {
            foreach (var id in _trackIndexes.Keys.Where(id => !checkedIds.Contains(id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await CheckPlaylistVersionAsync(id, cancellationToken);
                }
                catch (APIException exception) when (exception.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    _trackIndexes.TryRemove(id, out _);
                    _snapshots.TryRemove(id, out _);
                    _stale.TryRemove(id, out _);
                    TrySaveCache(() => _cache.RemoveTracks(_clientId, id));
                }
            }
        }

        internal async Task WaitForBackgroundWorkAsync()
        {
            while (true)
            {
                try
                {
                    await Task.WhenAll(_backgroundTasks.ToArray()).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                if (_backgroundTasks.All(task => task.IsCompleted))
                {
                    return;
                }
            }
        }

        private void OnProgressChanged(object? sender, PlaybackProgress progress)
        {
            if (progress.TrackUri is { } currentUri)
            {
                _playbackTrackUri = currentUri;
            }

            if (string.IsNullOrEmpty(progress.ContextUri) && progress.TrackUri is { } uri && _queueTrackUris.Contains(uri))
            {
                progress = progress with { ContextUri = _queuePlaylistUri };
            }

            ProgressChanged?.Invoke(this, progress);
        }

        public void Seek(int positionMs) => _playback?.Seek(Math.Max(0, positionMs));

        private void OnModesChanged(object? sender, PlaybackModes modes) => ModesChanged?.Invoke(this, modes);

        public Task NextTrackAsync(CancellationToken cancellationToken) => SkipTrackAsync(1, cancellationToken);

        public Task PreviousTrackAsync(CancellationToken cancellationToken) => SkipTrackAsync(-1, cancellationToken);

        private async Task SkipTrackAsync(int direction, CancellationToken cancellationToken)
        {
            await _skipLock.WaitAsync(cancellationToken);
            try
            {
                if (_shuffleRequested || _playbackPlaylistUri is not { } playlistUri)
                {
                    if (direction > 0)
                    {
                        _playback?.NextTrack();
                    }
                    else
                    {
                        _playback?.PreviousTrack();
                    }
                    return;
                }

                var playlistId = playlistUri.Split(':').Last();
                var index = await GetTrackIndexAsync(playlistId, cancellationToken);
                var ordered = index.Orders.GetOrAdd(_playbackSort, sort => index.Tracks.Order(TrackSorting.For(sort)).ToArray());
                var current = Array.FindIndex(ordered, track => track.Position == _playbackPosition && track.Uri == _playbackTrackUri);
                if (current < 0)
                {
                    current = Array.FindIndex(ordered, track => track.Uri == _playbackTrackUri);
                }

                if (current < 0 || ordered.Length == 0)
                {
                    return;
                }

                var next = current + direction;
                if (next < 0 || next >= ordered.Length)
                {
                    if (_repeatRequested == 1)
                    {
                        next = (next + ordered.Length) % ordered.Length;
                    }
                    else
                    {
                        if (direction < 0)
                        {
                            Seek(0);
                        }
                        else if (_isPlaying)
                        {
                            _playback?.TogglePause();
                        }
                        return;
                    }
                }

                var track = ordered[next];
                await PlayAsync(playlistUri, track.Position, cancellationToken, track.Uri, _playbackSort);
            }
            finally
            {
                _skipLock.Release();
            }
        }

        public async Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken)
        {
            var device = await GetLocalDeviceAsync(cancellationToken);
            var client = await GetClientAsync(cancellationToken);
            await client.Player.SetShuffle(new PlayerShuffleRequest(enabled) { DeviceId = device }, cancellationToken);
            _shuffleRequested = enabled;
        }

        public async Task SetRepeatAsync(int mode, CancellationToken cancellationToken)
        {
            var state = mode switch
            {
                0 => PlayerSetRepeatRequest.State.Off,
                1 => PlayerSetRepeatRequest.State.Context,
                2 => PlayerSetRepeatRequest.State.Track,
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };
            var device = await GetLocalDeviceAsync(cancellationToken);
            var client = await GetClientAsync(cancellationToken);
            await client.Player.SetRepeat(new PlayerSetRepeatRequest(state) { DeviceId = device }, cancellationToken);
            _repeatRequested = mode;
        }

        private Task<string> GetLocalDeviceAsync(CancellationToken cancellationToken) => _playback is not null ?
            _playback.GetDeviceAsync(GetAccessTokenAsync, cancellationToken) :
            throw new InvalidOperationException(StreamingConstants.Unsupported);

        private void OnPlayingChanged(object? sender, bool playing)
        {
            _isPlaying = playing;
            PlayingChanged?.Invoke(this, playing);
        }

        public void TogglePause()
        {
            if (!IsConnected || _playback is null)
            {
                throw new InvalidOperationException(StreamingConstants.DeviceUnavailable);
            }

            _playback.TogglePause();
        }

        public void SetVolume(double volume) => _playback?.SetVolume(Math.Clamp(volume, 0, 1));

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
            foreach (var cached in _cache.LoadTracks(clientId))
            {
                _trackIndexes[cached.PlaylistId] = new TrackIndex(cached.Tracks, cached.SnapshotId);
            }
            PreparePlayback();
            return true;
        }

        private void PreparePlayback()
        {
            if (_playback is null || !OperatingSystem.IsWindows() || !HasStreamingConsent())
            {
                return;
            }

            _backgroundTasks.Add(PreparePlaybackAsync(_background.Token));
        }

        private bool HasStreamingConsent()
        {
            var scopes = (_token?.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new[] { Scopes.Streaming, Scopes.UserReadEmail, Scopes.UserReadPrivate }.All(scopes.Contains);
        }

        private async Task PreparePlaybackAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _playback!.GetDeviceAsync(GetAccessTokenAsync, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                // Preparation is optional; Play retries initialization and reports any failure.
            }
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
                Scope = [Scopes.PlaylistReadPrivate, Scopes.PlaylistReadCollaborative, Scopes.UserModifyPlaybackState,
                    Scopes.Streaming, Scopes.UserReadEmail, Scopes.UserReadPrivate],
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
                    _cache.Clear(clientId);
                    _settings.SaveToken(clientId, token);
                    _clientId = clientId;
                    _token = token;
                    _client = new SpotifyClient(_configuration.WithToken(token.AccessToken));
                    PreparePlayback();
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
            _queuePlaylistUri = null;
            _queueTrackUris.Clear();
            _shuffleRequested = false;
            _repeatRequested = 0;
            _playbackPlaylistUri = null;
            _playbackTrackUri = null;
            _playbackPosition = null;
            _background.Cancel();
            _background.Dispose();
            _background = new CancellationTokenSource();
            _snapshots.Clear();
            _stale.Clear();
            _refreshes.Clear();
            InvalidateTrackCache();
            _playback?.Reset();
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
                    item.Images?.FirstOrDefault()?.Url,
                    item.SnapshotId))
                .ToArray();
            foreach (var playlist in playlists)
            {
                RegisterSnapshot(playlist.Id, playlist.SnapshotId);
            }

            var result = new PlaylistPage(playlists, offset + items.Count, items.Count > 0 && !string.IsNullOrEmpty(page.Next));
            if (offset == 0)
            {
                TrySaveCache(() => _cache.SaveLibrary(_clientId, result));
                var lifetime = _background.Token;
                var validation = ValidateOtherCachedPlaylistsAsync(playlists.Select(playlist => playlist.Id).ToHashSet(), lifetime);
                _backgroundTasks.Add(validation);
                _ = ObserveBackgroundAsync(validation, lifetime);
            }

            return result;
        }

        public async Task<TrackPage> GetTrackPageAsync(string playlistId, int offset, CancellationToken cancellationToken)
        {
            var client = await GetClientAsync(cancellationToken);
            var page = await client.Playlists.GetPlaylistItems(
                playlistId,
                new PlaylistGetItemsRequest { Limit = PlaylistConstants.TrackPageSize, Offset = offset },
                cancellationToken);
            var items = page.Items ?? [];
            var tracks = new List<TrackInfo>();
            for (var index = 0; index < items.Count; index++)
            {
                var track = MapTrack(items[index], offset + index);
                if (track is not null)
                {
                    tracks.Add(track);
                }
            }

            return new TrackPage(tracks, offset + items.Count,
                items.Count > 0 && !string.IsNullOrEmpty(page.Next), page.Total ?? offset + items.Count);
        }

        public async Task<PlaylistPreview> GetTrackPreviewAsync(string playlistId, CancellationToken cancellationToken)
        {
            if (_trackIndexes.TryGetValue(playlistId, out var cached))
            {
                return new PlaylistPreview(cached.Tracks.OrderBy(track => track.Position).Take(PlaylistConstants.PreviewTrackLimit).ToArray(), FormatDuration(cached.DurationMs));
            }

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

        public async Task<TrackPage> GetSortedTrackPageAsync(string playlistId, int sort, int offset, CancellationToken cancellationToken)
        {
            if (sort == 0 && !_trackIndexes.ContainsKey(playlistId))
            {
                var page = await GetTrackPageAsync(playlistId, offset, cancellationToken);
                if (offset == 0 && _snapshots.ContainsKey(playlistId))
                {
                    QueueRefresh(playlistId, page);
                }

                return page;
            }

            var index = await GetTrackIndexAsync(playlistId, cancellationToken);
            var ordered = index.Orders.GetOrAdd(sort, value => index.Tracks.Order(TrackSorting.For(value)).ToArray());
            var tracks = (sort == 0 ? ordered.Where(track => track.Position >= offset) : ordered.Skip(offset))
                .Take(PlaylistConstants.TrackPageSize).ToArray();
            var next = sort == 0 ? (tracks.LastOrDefault()?.Position + 1 ?? offset) : offset + tracks.Length;
            var hasMore = sort == 0 ? ordered.Any(track => track.Position >= next) : next < ordered.Length;
            return new TrackPage(tracks, next, hasMore, ordered.Length, index.DurationMs);
        }

        private async Task<TrackIndex> GetTrackIndexAsync(string playlistId, CancellationToken cancellationToken)
        {
            if (_trackIndexes.TryGetValue(playlistId, out var cached))
            {
                return cached;
            }

            await _indexLock.WaitAsync(cancellationToken);
            try
            {
                if (_trackIndexes.TryGetValue(playlistId, out cached))
                {
                    return cached;
                }

                return await BuildTrackIndexAsync(playlistId, null, cancellationToken);
            }
            finally
            {
                _indexLock.Release();
            }
        }

        private async Task<TrackIndex> BuildTrackIndexAsync(string playlistId, TrackPage? seed, CancellationToken cancellationToken)
        {
            var generation = Volatile.Read(ref _indexGeneration);
            var snapshot = _snapshots.GetValueOrDefault(playlistId) ?? string.Empty;
            var scope = _clientId;
            var tracks = seed?.Tracks.ToList() ?? [];
            var offset = seed?.NextOffset ?? 0;
            var client = await GetClientAsync(cancellationToken);
            while (seed is null || seed.HasMore)
            {
                var page = await client.Playlists.GetPlaylistItems(playlistId, new PlaylistGetItemsRequest
                {
                    Limit = PlaylistConstants.TrackPageSize,
                    Offset = offset,
                    Fields = { "next,total,items(added_at,is_local,item(type,uri,name,duration_ms,artists(name)))" },
                }, cancellationToken);
                var items = page.Items ?? [];
                for (var position = 0; position < items.Count; position++)
                {
                    if (MapTrack(items[position], offset + position) is { } track)
                    {
                        tracks.Add(track);
                    }
                }

                offset += items.Count;
                if (items.Count == 0 || string.IsNullOrEmpty(page.Next))
                {
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (snapshot != (_snapshots.GetValueOrDefault(playlistId) ?? string.Empty))
            {
                throw new InvalidOperationException(PlaylistConstants.CacheRefreshFailed);
            }

            var index = new TrackIndex(tracks, snapshot);
            lock (_trackIndexes)
            {
                if (generation == Volatile.Read(ref _indexGeneration))
                {
                    _trackIndexes[playlistId] = index;
                    if (!string.IsNullOrEmpty(snapshot))
                    {
                        TrySaveCache(() => _cache.SaveTracks(scope, new CachedPlaylistData(playlistId, snapshot, tracks)));
                    }
                }
            }

            return index;
        }

        private void TrySaveCache(Action save)
        {
            try
            {
                save();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CacheWarning?.Invoke(this, PlaylistConstants.CacheWriteFailed);
            }
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
                track.DurationMs,
                entry.AddedAt);
        }

        private static string FormatDuration(long milliseconds)
        {
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)duration.TotalMinutes, duration.Seconds);
        }

        public async Task PlayAsync(string playlistUri, int? position, CancellationToken cancellationToken, string? trackUri = null, int sort = 0)
        {
            var playlistId = playlistUri.Split(':').Last();
            await EnsurePlaylistFreshAsync(playlistId, cancellationToken);
            if (trackUri is not null && _trackIndexes.TryGetValue(playlistId, out var index))
            {
                var track = index.Tracks.FirstOrDefault(item => item.Uri == trackUri && item.Position == position) ??
                    index.Tracks.FirstOrDefault(item => item.Uri == trackUri);
                if (track is null)
                {
                    throw new InvalidOperationException(PlaylistConstants.TrackRemoved);
                }

                position = track.Position;
            }
            if (_playback is null)
            {
                throw new InvalidOperationException(StreamingConstants.Unsupported);
            }

            await GetClientAsync(cancellationToken);
            if (!HasStreamingConsent())
            {
                throw new InvalidOperationException(StreamingConstants.Reauthorize);
            }

            var deviceId = await _playback.GetDeviceAsync(GetAccessTokenAsync, cancellationToken);
            var client = await GetClientAsync(cancellationToken);
            TrackInfo[]? queue = null;
            var queuePosition = position;
            if (sort != 0)
            {
                var trackIndex = await GetTrackIndexAsync(playlistId, cancellationToken);
                queue = trackIndex.Orders.GetOrAdd(sort, value => trackIndex.Tracks.Order(TrackSorting.For(value)).ToArray());
                queuePosition = position.HasValue ? Array.FindIndex(queue, track => track.Position == position.Value) : 0;
                if (queue.Length == 0 || queuePosition < 0)
                {
                    throw new InvalidOperationException(PlaylistConstants.TrackRemoved);
                }
            }

            try
            {
                await client.Player.ResumePlayback(new PlayerResumePlaybackRequest
                {
                    DeviceId = deviceId,
                    ContextUri = queue is null ? playlistUri : null,
                    Uris = queue?.Select(track => track.Uri).ToList(),
                    OffsetParam = queuePosition.HasValue ? new PlayerResumePlaybackRequest.Offset { Position = queuePosition.Value } : null,
                }, cancellationToken);
                _queuePlaylistUri = queue is null ? null : playlistUri;
                _queueTrackUris = queue is null ? [] : queue.Select(track => track.Uri).ToHashSet();
                _playbackPlaylistUri = playlistUri;
                _playbackSort = sort;
                _playbackPosition = position;
                _playbackTrackUri = trackUri ?? queue?.ElementAtOrDefault(queuePosition ?? 0)?.Uri;
                await client.Player.SetShuffle(new PlayerShuffleRequest(_shuffleRequested) { DeviceId = deviceId }, cancellationToken);
            }
            catch (APIException exception) when (exception.Response?.StatusCode == HttpStatusCode.NotFound)
            {
                _playback.Reset();
                throw new InvalidOperationException(StreamingConstants.DeviceUnavailable);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _background.Cancel();
            if (_backgroundTasks.Any(task => !task.IsCompleted))
            {
                _ = DisposeAfterBackgroundAsync();
            }
            else
            {
                DisposeCore();
            }
        }

        private async Task DisposeAfterBackgroundAsync()
        {
            await WaitForBackgroundWorkAsync().ConfigureAwait(false);
            DisposeCore();
        }

        private void DisposeCore()
        {
            _background.Dispose();
            if (_playback is not null)
            {
                _playback.PlayingChanged -= OnPlayingChanged;
                _playback.ModesChanged -= OnModesChanged;
                _playback.ProgressChanged -= OnProgressChanged;
            }

            _playback?.Dispose();
            _httpClient?.Dispose();
            _tokenLock.Dispose();
            _indexLock.Dispose();
            _skipLock.Dispose();
        }

        private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            await GetClientAsync(cancellationToken);
            return _token!.AccessToken;
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

                    if (string.IsNullOrEmpty(refreshed.Scope))
                    {
                        refreshed.Scope = _token.Scope;
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
