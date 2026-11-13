using KK.SFPlayer.Constants;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Models;
using KK.SFPlayer.Services;
using KK.SFPlayer.ViewModels;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed class SpotifyIntegrationTests
    {
        private const string ClientId = "0123456789abcdef0123456789abcdef";
        private const string TokenJson = """{"access_token":"new-access","refresh_token":"rotated-refresh","token_type":"Bearer","expires_in":3600}""";
        private const string TrackJson = """{"type":"track","uri":"spotify:track:one","name":"First track","duration_ms":185000,"artists":[{"name":"Test artist"}],"album":{"images":[{"url":"https://example.com/cover.png"}]}}""";

        [Fact]
        public async Task FirstLaunchStaysEmptyAndDoesNotCallSpotify()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);

            await viewModel.InitializeAsync();

            Assert.False(viewModel.IsConnected);
            Assert.False(viewModel.ShowPlaylists);
            Assert.False(viewModel.HasClientId);
            Assert.Empty(viewModel.Playlists);
            Assert.Empty(viewModel.Tracks);
            Assert.Empty(viewModel.Status);
            Assert.Empty(context.Handler.Requests);
        }

        [Fact]
        public async Task SettingsSaveUserClientIdWithoutSigningIn()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            viewModel.OpenSettingsCommand.Execute(null);
            viewModel.Settings.ClientId = ClientId;

            viewModel.Settings.SaveCommand.Execute(null);

            Assert.Equal(ClientId, context.Store.Load().ClientId);
            Assert.True(viewModel.HasClientId);
            Assert.False(viewModel.IsSettingsOpen);
            Assert.False(viewModel.IsConnected);
            Assert.Equal(AuthenticationConstants.SignIn, viewModel.AccountTooltip);
            Assert.Empty(context.Handler.Requests);
        }

        [Fact]
        public async Task ClearingClientIdHidesAccountButtonAgain()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            context.Store.Save(new AppSettings { ClientId = ClientId });
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            Assert.True(viewModel.HasClientId);
            viewModel.OpenSettingsCommand.Execute(null);
            viewModel.Settings.ClientId = string.Empty;

            viewModel.Settings.SaveCommand.Execute(null);

            Assert.False(viewModel.HasClientId);
            Assert.Empty(context.Store.Load().ClientId);
            Assert.Empty(context.Handler.Requests);
        }

        [Fact]
        public async Task StartupSettingIsAppliedOnlyOnSave()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            viewModel.OpenSettingsCommand.Execute(null);
            viewModel.Settings.StartWithWindows = true;
            viewModel.Settings.CancelCommand.Execute(null);
            Assert.False(context.Startup.IsEnabled);
            Assert.Equal(0, context.Startup.SetCalls);

            viewModel.OpenSettingsCommand.Execute(null);
            Assert.False(viewModel.Settings.StartWithWindows);
            viewModel.Settings.StartWithWindows = true;
            viewModel.Settings.SaveCommand.Execute(null);
            Assert.True(context.Startup.IsEnabled);
            Assert.True(context.Store.Load().StartWithWindows);

            viewModel.OpenSettingsCommand.Execute(null);
            Assert.True(viewModel.Settings.StartWithWindows);
            viewModel.Settings.StartWithWindows = false;
            viewModel.Settings.SaveCommand.Execute(null);
            Assert.False(context.Startup.IsEnabled);
            Assert.False(context.Store.Load().StartWithWindows);
            Assert.Equal(2, context.Startup.SetCalls);
        }

        [Fact]
        public async Task StartupFailureKeepsSettingsOpenAndDoesNotSave()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            context.Startup.FailOnSet = true;
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            viewModel.OpenSettingsCommand.Execute(null);
            viewModel.Settings.ClientId = ClientId;
            viewModel.Settings.StartWithWindows = true;

            viewModel.Settings.SaveCommand.Execute(null);

            Assert.True(viewModel.IsSettingsOpen);
            Assert.Equal(SettingsConstants.StartupError, viewModel.Settings.Error);
            Assert.False(context.Startup.IsEnabled);
            Assert.False(viewModel.HasClientId);
            Assert.Empty(context.Store.Load().ClientId);
        }

        [Fact]
        public async Task CancellingSettingsDoesNotSaveDraftClientId()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            viewModel.OpenSettingsCommand.Execute(null);
            viewModel.Settings.ClientId = ClientId;

            viewModel.Settings.CancelCommand.Execute(null);

            Assert.False(viewModel.IsSettingsOpen);
            Assert.Empty(context.Store.Load().ClientId);
            viewModel.OpenSettingsCommand.Execute(null);
            Assert.Empty(viewModel.Settings.ClientId);
        }

        [Fact]
        public async Task SettingsRejectInvalidClientIdWithoutSaving()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            viewModel.OpenSettingsCommand.Execute(null);
            viewModel.Settings.ClientId = "invalid";

            viewModel.Settings.SaveCommand.Execute(null);

            Assert.True(viewModel.IsSettingsOpen);
            Assert.Equal(SettingsConstants.InvalidClientId, viewModel.Settings.Error);
            Assert.Empty(context.Store.Load().ClientId);
            Assert.Empty(context.Handler.Requests);
        }

        [Fact]
        public async Task AccountWithoutConfiguredClientIdDoesNotCallSpotify()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();

            await viewModel.AccountCommand.ExecuteAsync(null);

            Assert.Equal(SettingsConstants.ClientIdRequired, viewModel.Status);
            Assert.Empty(context.Handler.Requests);
        }

        [Fact]
        public async Task SavedSessionRestoresAfterRestartAndSignOutClearsToken()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path switch
            {
                "/api/token" => Json(TokenJson),
                "/v1/me/playlists" => Json("""{"items":[],"next":null}"""),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken();
            using (var firstSession = new MainViewModel(context.Player, context.Store, context.Startup))
            {
                await firstSession.InitializeAsync();
                Assert.True(firstSession.IsConnected);
            }

            using var nextPlayer = new MPlayerService(context.Store, SpotifyClientConfig.CreateDefault()
                .WithHTTPClient(new NetHttpClient(new HttpClient(context.Handler))));
            using var nextSession = new MainViewModel(nextPlayer, context.Store, context.Startup);
            await nextSession.InitializeAsync();
            Assert.True(nextSession.IsConnected);
            Assert.Equal(AuthenticationConstants.SignOut, nextSession.AccountTooltip);
            Assert.Single(context.Handler.Requests.FindAll(request => request.Path == "/api/token"));

            await nextSession.AccountCommand.ExecuteAsync(null);
            Assert.True(nextSession.IsLogoutConfirmationOpen);
            Assert.True(nextSession.IsConnected);
            Assert.NotNull(context.Store.LoadToken(ClientId));
            Assert.False(nextSession.AccountCommand.CanExecute(null));

            nextSession.CancelLogoutCommand.Execute(null);
            Assert.False(nextSession.IsLogoutConfirmationOpen);
            Assert.True(nextSession.IsConnected);
            Assert.NotNull(context.Store.LoadToken(ClientId));
            Assert.False(nextSession.ConfirmLogoutCommand.CanExecute(null));

            await nextSession.AccountCommand.ExecuteAsync(null);
            await nextSession.ConfirmLogoutCommand.ExecuteAsync(null);

            Assert.False(nextSession.IsLogoutConfirmationOpen);
            Assert.False(nextSession.IsConnected);
            Assert.Equal(AuthenticationConstants.SignIn, nextSession.AccountTooltip);
            Assert.Null(context.Store.LoadToken(ClientId));
            Assert.Empty(nextSession.Playlists);
            Assert.True(context.Store.Load().AlwaysOnTop);
            Assert.Equal(ClientId, context.Store.Load().ClientId);
        }

        [Fact]
        public async Task ValidSavedTokenIsReusedWithoutChangingItsTimestamp()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected token refresh."));
            context.SeedToken(false);
            var original = context.Store.LoadToken(ClientId)!;

            Assert.True(await context.Player.RestoreAsync(ClientId, CancellationToken.None));

            Assert.Empty(context.Handler.Requests);
            var restored = context.Store.LoadToken(ClientId)!;
            Assert.Equal(original.CreatedAt, restored.CreatedAt);
            Assert.Equal(original.AccessToken, restored.AccessToken);
        }

        [Fact]
        public async Task FailedPlaylistReadCanBeRetriedWithoutAnotherLogin()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var available = false;
            using var context = new TestContext(request => request.Path == "/v1/me/playlists" ?
                available ? Json("""{"items":[],"next":null}""") : Json("""{"error":{"status":503,"message":"Unavailable"}}""", HttpStatusCode.ServiceUnavailable) :
                throw new InvalidOperationException("Unexpected request."));
            context.SeedToken(false);
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            Assert.True(viewModel.HasRetry);
            Assert.True(viewModel.IsConnected);

            available = true;
            await viewModel.RetryCommand.ExecuteAsync(null);

            Assert.False(viewModel.HasRetry);
            Assert.Equal(PlaybackConstants.NoPlaylists, viewModel.Status);
            Assert.True(viewModel.IsConnected);
            Assert.DoesNotContain(context.Handler.Requests, request => request.Path == "/api/token");
        }

        [Fact]
        public void ClientIdPersistsInUserSettings()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            System.IO.Directory.CreateDirectory(context.Directory);
            var path = Path.Combine(context.Directory, "settings.json");
            File.WriteAllText(path, """{"ClientId":"old-client","AlwaysOnTop":true}""");

            Assert.True(context.Store.Load().AlwaysOnTop);

            using var migrated = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("old-client", migrated.RootElement.GetProperty("ClientId").GetString());
            Assert.True(migrated.RootElement.GetProperty("AlwaysOnTop").GetBoolean());
        }

        [Fact]
        public async Task InvalidConfiguredClientIdDoesNotStartAuthorization()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            context.Store.Save(new AppSettings { ClientId = "invalid" });
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();

            await viewModel.AccountCommand.ExecuteAsync(null);

            Assert.Equal(SettingsConstants.ClientIdRequired, viewModel.Status);
            Assert.False(viewModel.IsConnected);
            Assert.Empty(context.Handler.Requests);
        }

        [Fact]
        public async Task AlwaysOnTopPersistsAndUpdatesTooltip()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();

            viewModel.ToggleAlwaysOnTopCommand.Execute(null);

            Assert.True(viewModel.AlwaysOnTop);
            Assert.Equal(WindowConstants.TopmostOn, viewModel.TopmostTooltip);
            Assert.True(context.Store.Load().AlwaysOnTop);
            viewModel.ToggleAlwaysOnTopCommand.Execute(null);
            Assert.False(context.Store.Load().AlwaysOnTop);
            Assert.Equal(WindowConstants.TopmostOff, viewModel.TopmostTooltip);
        }

        [Fact]
        public async Task PlaylistsLoadAllPagesAndRefreshTokenIsEncryptedAndRotated()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path switch
            {
                "/api/token" => Json(TokenJson),
                "/v1/me/playlists" when request.Query.Contains("offset=0", StringComparison.Ordinal) => Json("""
                    {"items":[{"id":"one","uri":"spotify:playlist:one","name":"First playlist","owner":{"display_name":"Demo"},"images":[]}],"next":"https://api.spotify.com/v1/me/playlists?offset=1"}
                    """),
                "/v1/me/playlists" => Json("""{"items":[{"id":"two","name":"Second playlist","images":[]}],"next":null}"""),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken();

            Assert.True(await context.Player.RestoreAsync(ClientId, CancellationToken.None));
            var playlists = await context.Player.GetPlaylistsAsync(CancellationToken.None);

            Assert.Equal(2, playlists.Count);
            Assert.Equal("First playlist", playlists[0].Name);
            Assert.Equal("spotify:playlist:two", playlists[1].Uri);
            Assert.Equal("rotated-refresh", context.Store.LoadToken(ClientId)?.RefreshToken);
            var tokenFile = File.ReadAllBytes(Path.Combine(context.Directory, "spotify-token.dat"));
            Assert.DoesNotContain("rotated-refresh", Encoding.UTF8.GetString(tokenFile));
            Assert.Null(context.Store.LoadToken("different-client"));
        }

        [Fact]
        public async Task PlaylistItemsUseNewEndpointAndKeepOriginalPlaybackPositions()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path switch
            {
                "/api/token" => Json(TokenJson),
                "/v1/playlists/demo/items" when request.Query.Contains("offset=0", StringComparison.Ordinal) => Json(
                    "{\"items\":[{\"item\":" + TrackJson + "},{\"item\":null},{\"item\":{\"type\":\"episode\",\"uri\":\"spotify:episode:one\"}},{\"is_local\":true,\"item\":" + TrackJson + "}],\"next\":\"https://api.spotify.com/next\"}"),
                "/v1/playlists/demo/items" => Json("{\"items\":[{\"track\":" + TrackJson + "}],\"next\":null}"),
                "/v1/me/player/play" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken();
            await context.Player.RestoreAsync(ClientId, CancellationToken.None);

            var tracks = await context.Player.GetTracksAsync("demo", CancellationToken.None);
            Assert.Equal(2, tracks.Count);
            Assert.Equal(0, tracks[0].Position);
            Assert.Equal(4, tracks[1].Position);
            Assert.Equal("3:05", tracks[0].Duration);
            Assert.Equal("Test artist", tracks[0].Artists);
            Assert.Equal("https://example.com/cover.png", tracks[0].ImageUrl);

            await context.Player.PlayAsync("spotify:playlist:demo", tracks[1].Position, CancellationToken.None);
            var playRequest = context.Handler.Requests.FindLast(request => request.Method == HttpMethod.Put)!;
            using var body = JsonDocument.Parse(playRequest.Body);
            Assert.Equal("spotify:playlist:demo", body.RootElement.GetProperty("context_uri").GetString());
            Assert.Equal(4, body.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
        }

        [Fact]
        public async Task RevokedAuthorizationOffersReconnectAndClearsLibrary()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path == "/api/token" ?
                Json(TokenJson) :
                Json("""{"error":{"status":401,"message":"Expired token"}}""", HttpStatusCode.Unauthorized));
            context.SeedToken();
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);

            await viewModel.InitializeAsync();

            Assert.Equal(AuthenticationConstants.Reconnect, viewModel.Status);
            Assert.False(viewModel.IsConnected);
            Assert.False(context.Player.IsConnected);
            Assert.Empty(viewModel.Playlists);
            Assert.False(viewModel.IsBusy);
        }

        [Fact]
        public async Task MissingPlaybackDeviceShowsActionableMessage()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path switch
            {
                "/api/token" => Json(TokenJson),
                "/v1/me/playlists" => Json("""{"items":[{"id":"demo","uri":"spotify:playlist:demo","name":"Demo"}],"next":null}"""),
                "/v1/playlists/demo/items" => Json("{\"items\":[{\"item\":" + TrackJson + "}],\"next\":null}"),
                "/v1/me/player/play" => Json("""{"error":{"status":404,"message":"No active device"}}""", HttpStatusCode.NotFound),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken();
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            await viewModel.OpenPlaylistCommand.ExecuteAsync(viewModel.Playlists[0]);

            await viewModel.PlayPlaylistCommand.ExecuteAsync(null);

            Assert.Equal(PlaybackConstants.NoDevice, viewModel.Status);
            Assert.True(viewModel.IsConnected);
            Assert.False(viewModel.IsBusy);
        }

        private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

        private sealed class TestContext : IDisposable
        {
            public TestContext(Func<CapturedRequest, HttpResponseMessage> respond)
            {
                Directory = Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests", Guid.NewGuid().ToString("N"));
                Store = new SettingsStore(Directory);
                Handler = new FakeSpotifyHandler(respond);
                Player = new MPlayerService(Store, SpotifyClientConfig.CreateDefault()
                    .WithHTTPClient(new NetHttpClient(new HttpClient(Handler))));
            }

            public string Directory { get; }

            public SettingsStore Store { get; }

            public FakeStartupService Startup { get; } = new();

            public FakeSpotifyHandler Handler { get; }

            public MPlayerService Player { get; }

            public void SeedToken(bool expired = true)
            {
                Store.Save(new AppSettings { ClientId = ClientId, AlwaysOnTop = true });
                Store.SaveToken(ClientId, new PKCETokenResponse
                {
                    AccessToken = "old-access",
                    RefreshToken = "old-refresh",
                    ExpiresIn = 3600,
                    TokenType = "Bearer",
                    CreatedAt = expired ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow,
                });
            }

            public void Dispose()
            {
                Player.Dispose();
                Handler.Dispose();
                var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests")) + Path.DirectorySeparatorChar;
                if (Path.GetFullPath(Directory).StartsWith(testRoot, StringComparison.OrdinalIgnoreCase) &&
                    System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, true);
                }
            }
        }

        private sealed class FakeStartupService : IStartupService
        {
            public bool IsSupported => true;

            public bool IsEnabled { get; private set; }

            public int SetCalls { get; private set; }

            public bool FailOnSet { get; set; }

            public void SetEnabled(bool enabled)
            {
                if (FailOnSet)
                {
                    throw new UnauthorizedAccessException();
                }

                SetCalls++;
                IsEnabled = enabled;
            }
        }

        private sealed record CapturedRequest(HttpMethod Method, string Path, string Query, string Body);

        private sealed class FakeSpotifyHandler(Func<CapturedRequest, HttpResponseMessage> respond) : HttpMessageHandler
        {
            public List<CapturedRequest> Requests { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var captured = new CapturedRequest(
                    request.Method,
                    request.RequestUri!.AbsolutePath,
                    request.RequestUri.Query,
                    request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
                Requests.Add(captured);
                return respond(captured);
            }
        }
    }
}
