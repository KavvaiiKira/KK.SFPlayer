using KK.SFPlayer.Constants;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Models.Records;
using KK.SFPlayer.Services;
using KK.SFPlayer.ViewModels;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed partial class SpotifyIntegrationTests
    {
        private const string ClientId = "0123456789abcdef0123456789abcdef";
        private const string TokenJson = """{"access_token":"new-access","refresh_token":"rotated-refresh","token_type":"Bearer","expires_in":3600}""";
        private const string TrackJson = """{"type":"track","uri":"spotify:track:one","name":"First track","duration_ms":185000,"artists":[{"name":"Test artist"}],"album":{"images":[{"url":"https://example.com/cover.png"}]}}""";

        [Fact]
        public async Task RestorePreparesPlaybackWithoutWaitingOrStartingMusic()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected Spotify request."));
            context.SeedToken(expired: false);
            context.Playback.Ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(await context.Player.RestoreAsync(ClientId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1, context.Playback.DeviceRequests);
            Assert.Equal("old-access", context.Playback.Token);
            Assert.False(context.Playback.Ready.Task.IsCompleted);
            context.Playback.Ready.SetResult("local-device");
            await context.Player.WaitForBackgroundWorkAsync();
        }

        [Fact]
        public async Task FailedPreparationDoesNotPreventRestoringLibraryOrRetryingPlay()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path is "/v1/me/player/play" or "/v1/me/player/shuffle" ?
                new HttpResponseMessage(HttpStatusCode.NoContent) :
                throw new InvalidOperationException("Unexpected Spotify request."));
            context.SeedToken(expired: false);
            context.Playback.Ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            context.Playback.Ready.SetException(new InvalidOperationException("Preparation failed."));
            Assert.True(await context.Player.RestoreAsync(ClientId, CancellationToken.None));
            await context.Player.WaitForBackgroundWorkAsync();
            context.Playback.Ready = null;
            await context.Player.PlayAsync("spotify:playlist:first", 0, CancellationToken.None);
            Assert.Equal(2, context.Playback.DeviceRequests);
        }

        [Theory]
        [InlineData(false, 0, 0)]
        [InlineData(false, 1, 1)]
        [InlineData(false, 2, 0)]
        [InlineData(false, 3, 0)]
        [InlineData(false, 4, 1)]
        [InlineData(false, 5, 0)]
        [InlineData(true, 0, 0)]
        [InlineData(true, 1, 1)]
        [InlineData(true, 2, 0)]
        [InlineData(true, 3, 0)]
        [InlineData(true, 4, 0)]
        [InlineData(true, 5, 1)]
        public async Task StartupPlayUsesCurrentScreenAndPersistedGlobalSort(bool openPlaylist, int sort, int expectedPosition)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request =>
            {
                if (request.Path == "/v1/me/playlists")
                {
                    return Json("""{"items":[{"id":"first","uri":"spotify:playlist:first","name":"First"},{"id":"second","uri":"spotify:playlist:second","name":"Second"}],"next":null}""");
                }

                if (request.Path.EndsWith("/items", StringComparison.Ordinal))
                {
                    var reversedDates = request.Path.Contains("second", StringComparison.Ordinal);
                    return Json(JsonSerializer.Serialize(new
                    {
                        items = Enumerable.Range(0, 2).Select(index => new
                        {
                            added_at = new DateTime(2026, 1, reversedDates ? 2 - index : index + 1, 0, 0, 0, DateTimeKind.Utc),
                            item = new
                            {
                                type = "track",
                                uri = $"spotify:track:{index}",
                                name = index == 0 ? "Zulu" : "Alpha",
                                duration_ms = 180000,
                                artists = new[] { new { name = "Artist" } },
                            },
                        }),
                        total = 2,
                        next = (string?)null,
                    }));
                }

                if (request.Path is "/v1/me/player/play" or "/v1/me/player/shuffle")
                {
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                throw new InvalidOperationException(request.Path);
            });
            context.SeedToken(false);
            context.Store.Save(context.Store.Load() with { PlaylistSorts = new() { ["first"] = sort, ["second"] = sort } });
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            if (openPlaylist)
            {
                await viewModel.OpenPlaylistCommand.ExecuteAsync(viewModel.Playlists[1]);
            }

            Assert.False(viewModel.HasPlayback);
            await viewModel.TogglePlaybackCommand.ExecuteAsync(null);

            var request = Assert.Single(context.Handler.Requests, item => item.Path == "/v1/me/player/play");
            using var body = JsonDocument.Parse(request.Body);
            if (sort == 0)
            {
                Assert.Equal(openPlaylist ? "spotify:playlist:second" : "spotify:playlist:first", body.RootElement.GetProperty("context_uri").GetString());
                Assert.Equal(expectedPosition, body.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
            }
            else
            {
                var uris = body.RootElement.GetProperty("uris").EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert.Equal(new[] { $"spotify:track:{expectedPosition}", $"spotify:track:{1 - expectedPosition}" }, uris);
                Assert.Equal(0, body.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
            }
            Assert.True(viewModel.HasPlayback);
            await viewModel.NextTrackCommand.ExecuteAsync(null);
            using var nextBody = JsonDocument.Parse(context.Handler.Requests.Last(item => item.Path == "/v1/me/player/play").Body);
            if (sort == 0)
            {
                Assert.Equal(1, nextBody.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
                Assert.Equal(openPlaylist ? "spotify:playlist:second" : "spotify:playlist:first", nextBody.RootElement.GetProperty("context_uri").GetString());
            }
            else
            {
                var offset = nextBody.RootElement.GetProperty("offset").GetProperty("position").GetInt32();
                Assert.Equal(1, offset);
                Assert.Equal($"spotify:track:{1 - expectedPosition}", nextBody.RootElement.GetProperty("uris")[offset].GetString());
            }

            Assert.Equal(0, context.Playback.NextCount);
            var requestsBeforeEnd = context.Handler.Requests.Count;
            await viewModel.NextTrackCommand.ExecuteAsync(null);
            Assert.Equal(requestsBeforeEnd, context.Handler.Requests.Count);
        }

        [Fact]
        public async Task SortedPagesHaveGlobalStartAndStableBoundariesAcrossDateSortChanges()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request =>
            {
                Assert.Equal("/v1/playlists/demo/items", request.Path);
                var offset = request.Query.Contains("offset=100", StringComparison.Ordinal) ? 100 :
                    request.Query.Contains("offset=50", StringComparison.Ordinal) ? 50 : 0;
                return Json(JsonSerializer.Serialize(new
                {
                    items = Enumerable.Range(offset, Math.Min(50, 120 - offset)).Select(index => new
                    {
                        is_local = index == 23,
                        added_at = index == 10 ? (DateTime?)null :
                            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index * 37 % 120),
                        item = index == 79 ? null : new
                        {
                            type = "track",
                            uri = $"spotify:track:{index}",
                            name = $"Track {120 - index:000}",
                            duration_ms = 180000,
                            artists = new[] { new { name = "Artist" } },
                        },
                    }),
                    total = 120,
                    next = offset < 100 ? "https://api.spotify.com/next" : null,
                }));
            });
            context.SeedToken(false);
            await context.Player.RestoreAsync(ClientId, CancellationToken.None);

            var newest = await context.Player.GetSortedTrackPageAsync("demo", 4, 0, CancellationToken.None);
            Assert.Equal(107, newest.Tracks[0].Position);
            Assert.Equal(50, newest.Tracks.Count);
            Assert.Equal(118, newest.Total);
            Assert.Equal(21240000, newest.TotalDurationMs);
            Assert.Equal(3, context.Handler.Requests.Count);
            Assert.All(context.Handler.Requests, request =>
            {
                Assert.Contains("limit=50", request.Query);
                Assert.Contains("fields=", request.Query);
                Assert.DoesNotContain("album", Uri.UnescapeDataString(request.Query));
            });

            var oldest = await context.Player.GetSortedTrackPageAsync("demo", 5, 0, CancellationToken.None);
            Assert.Equal(0, oldest.Tracks[0].Position);
            var second = await context.Player.GetSortedTrackPageAsync("demo", 4, newest.NextOffset, CancellationToken.None);
            var last = await context.Player.GetSortedTrackPageAsync("demo", 4, second.NextOffset, CancellationToken.None);
            var all = newest.Tracks.Concat(second.Tracks).Concat(last.Tracks).ToArray();
            Assert.Equal(118, all.Length);
            Assert.Equal(118, all.Select(track => track.Position).Distinct().Count());
            Assert.Equal(10, all[^1].Position);
            Assert.True(newest.Tracks[^1].AddedAt >= second.Tracks[0].AddedAt);
            Assert.False(last.HasMore);
            Assert.Equal(3, context.Handler.Requests.Count);

            var original = await context.Player.GetSortedTrackPageAsync("demo", 0, 0, CancellationToken.None);
            Assert.Equal(0, original.Tracks[0].Position);
            Assert.Equal(3, context.Handler.Requests.Count);
            context.Player.InvalidateTrackCache("demo");
            await context.Player.GetSortedTrackPageAsync("demo", 5, 0, CancellationToken.None);
            Assert.Equal(6, context.Handler.Requests.Count);
        }

        [Fact]
        public async Task TrackPageLoadsOnlyFiftyAndPreservesAddedDateAndPlaybackPosition()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var items = string.Join(",", Enumerable.Range(0, 50).Select(index =>
                index == 1 ? "{\"item\":null}" :
                "{\"added_at\":\"2026-11-01T12:30:00Z\",\"item\":" + TrackJson + "}"));
            using var context = new TestContext(request => request.Path switch
            {
                "/v1/playlists/demo/items" when request.Query.Contains("offset=0", StringComparison.Ordinal) =>
                    Json("{\"items\":[" + items + "],\"total\":51,\"next\":\"https://api.spotify.com/next\"}"),
                "/v1/playlists/demo/items" =>
                    Json("{\"items\":[{\"item\":" + TrackJson + "}],\"total\":51,\"next\":null}"),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken(false);
            await context.Player.RestoreAsync(ClientId, CancellationToken.None);

            var first = await context.Player.GetTrackPageAsync("demo", 0, CancellationToken.None);
            Assert.Single(context.Handler.Requests);
            Assert.Contains("limit=50", context.Handler.Requests[0].Query);
            Assert.True(first.HasMore);
            Assert.Equal(51, first.Total);
            Assert.Equal(50, first.NextOffset);
            Assert.Equal(49, first.Tracks.Count);
            Assert.Equal(49, first.Tracks[^1].Position);
            Assert.Equal(new DateTime(2026, 11, 1, 12, 30, 0, DateTimeKind.Utc), first.Tracks[0].AddedAt);

            var second = await context.Player.GetTrackPageAsync("demo", first.NextOffset, CancellationToken.None);
            Assert.Equal(2, context.Handler.Requests.Count);
            Assert.Contains("offset=50", context.Handler.Requests[1].Query);
            Assert.False(second.HasMore);
            Assert.Equal(50, Assert.Single(second.Tracks).Position);
            Assert.Null(second.Tracks[0].AddedAt);
        }

        [Fact]
        public async Task FailedNextTrackPageKeepsLoadedTracksAndRetriesSameOffset()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var nextAttempts = 0;
            var items = string.Join(",", Enumerable.Repeat(
                "{\"added_at\":\"2026-11-01T12:30:00Z\",\"item\":" + TrackJson + "}", 50));
            using var context = new TestContext(request => request.Path switch
            {
                "/v1/me/playlists" => Json("""{"items":[{"id":"demo","uri":"spotify:playlist:demo","name":"Demo"}],"next":null}"""),
                "/v1/playlists/demo/items" when request.Query.Contains("offset=0", StringComparison.Ordinal) =>
                    Json("{\"items\":[" + items + "],\"total\":51,\"next\":\"https://api.spotify.com/next\"}"),
                "/v1/playlists/demo/items" when ++nextAttempts == 1 =>
                    Json("""{"error":{"status":503,"message":"Unavailable"}}""", HttpStatusCode.ServiceUnavailable),
                "/v1/playlists/demo/items" =>
                    Json("{\"items\":[{\"added_at\":\"2026-11-10T12:30:00Z\",\"item\":" + TrackJson + "}],\"total\":51,\"next\":null}"),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken(false);
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            await viewModel.OpenPlaylistCommand.ExecuteAsync(viewModel.Playlists[0]);

            Assert.Equal(2, context.Handler.Requests.Count);
            Assert.Equal(50, viewModel.Tracks.Count);
            Assert.Equal("51 songs", viewModel.PlaylistSummary);
            await viewModel.LoadMoreTracksCommand.ExecuteAsync(null);

            Assert.Equal(50, viewModel.Tracks.Count);
            Assert.True(viewModel.HasRetry);
            Assert.False(viewModel.LoadMoreTracksCommand.CanExecute(null));
            await viewModel.RetryCommand.ExecuteAsync(null);

            Assert.Equal(51, viewModel.Tracks.Count);
            Assert.Equal(51, viewModel.SortedTracks.Count);
            Assert.Equal(0, viewModel.SortedTracks[0].Position);
            Assert.Equal(2, nextAttempts);
            Assert.All(context.Handler.Requests.TakeLast(2), request => Assert.Contains("offset=50", request.Query));
            Assert.False(viewModel.HasMoreTracks);
            Assert.Equal("51 songs · 2:37:15", viewModel.PlaylistSummary);
        }

        [Fact]
        public async Task LibraryLoadsTenAtATimeAndTrackPreviewsAreLoadedOnDemandAndCached()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path switch
            {
                "/v1/me/playlists" => Json(JsonSerializer.Serialize(new
                {
                    items = Enumerable.Range(request.Query.Contains("offset=10", StringComparison.Ordinal) ? 10 : 0, 10)
                        .Select(index => new { id = index.ToString(), uri = $"spotify:playlist:{index}", name = $"Playlist {index}" }),
                    next = request.Query.Contains("offset=10", StringComparison.Ordinal) ? null : "https://api.spotify.com/v1/me/playlists?offset=10",
                })),
                "/v1/playlists/0/items" => Json("{\"items\":[{\"item\":" + TrackJson + "}],\"total\":1,\"next\":null}"),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken(false);
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);

            await viewModel.InitializeAsync();

            Assert.Equal(10, viewModel.PlaylistCards.Count);
            Assert.True(viewModel.HasMorePlaylists);
            Assert.Single(context.Handler.Requests);
            Assert.Contains("limit=10", context.Handler.Requests[0].Query);
            Assert.All(viewModel.PlaylistCards, card => Assert.Empty(card.Tracks));

            await viewModel.PlaylistCards[0].EnsureLoadedAsync();
            await viewModel.PlaylistCards[0].EnsureLoadedAsync();
            Assert.Single(viewModel.PlaylistCards[0].Tracks);
            Assert.Single(context.Handler.Requests.FindAll(request => request.Path.EndsWith("/items", StringComparison.Ordinal)));

            await viewModel.LoadMorePlaylistsCommand.ExecuteAsync(null);

            Assert.Equal(20, viewModel.PlaylistCards.Count);
            Assert.False(viewModel.HasMorePlaylists);
            var metadata = context.Handler.Requests.FindAll(request => request.Path == "/v1/me/playlists");
            Assert.Equal(2, metadata.Count);
            Assert.Contains("offset=10", metadata[1].Query);
            Assert.Equal(3, context.Handler.Requests.Count);
        }

        [Theory]
        [InlineData(2, false, "6:10")]
        [InlineData(20, true, "")]
        public async Task TrackPreviewUsesOneLimitedRequestAndOnlyReportsCompleteDuration(int total, bool hasMore, string expectedDuration)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var count = Math.Min(total, PlaylistConstants.PreviewTrackLimit);
            var items = string.Join(",", Enumerable.Repeat("{\"item\":" + TrackJson + "}", count));
            using var context = new TestContext(request => request.Path == "/v1/playlists/demo/items" ?
                Json("{\"items\":[" + items + "],\"total\":" + total + ",\"next\":" + (hasMore ? "\"https://api.spotify.com/next\"" : "null") + "}") :
                throw new InvalidOperationException(request.Path));
            context.SeedToken(false);
            await context.Player.RestoreAsync(ClientId, CancellationToken.None);

            var preview = await context.Player.GetTrackPreviewAsync("demo", CancellationToken.None);

            Assert.Equal(count, preview.Tracks.Count);
            Assert.Equal(expectedDuration, preview.TotalDuration);
            var request = Assert.Single(context.Handler.Requests);
            Assert.Contains("limit=5", request.Query);
            Assert.Contains("offset=0", request.Query);
        }

        [Fact]
        public async Task FailedVisiblePreviewDoesNotRetryOnEveryLayoutUpdate()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var available = false;
            using var context = new TestContext(request => request.Path switch
            {
                "/v1/me/playlists" => Json("""{"items":[{"id":"demo","name":"Demo"}],"next":null}"""),
                "/v1/playlists/demo/items" when available => Json("{\"items\":[{\"item\":" + TrackJson + "}],\"total\":1,\"next\":null}"),
                "/v1/playlists/demo/items" => Json("""{"error":{"status":503,"message":"Unavailable"}}""", HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken(false);
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            var card = Assert.Single(viewModel.PlaylistCards);

            card.SetVisible(true);
            await card.LoadingTask;
            Assert.True(card.HasError);
            card.SetVisible(true);
            card.SetVisible(true);
            Assert.Single(context.Handler.Requests.FindAll(request => request.Path.EndsWith("/items", StringComparison.Ordinal)));

            available = true;
            await card.RetryCommand.ExecuteAsync(null);

            Assert.False(card.HasError);
            Assert.Single(card.Tracks);
            Assert.Equal(3, context.Handler.Requests.Count);
        }

        [Fact]
        public async Task FirstLaunchStaysEmptyAndDoesNotCallSpotify()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);

            await viewModel.InitializeAsync();

            Assert.False(viewModel.IsConnected);
            Assert.False(viewModel.ShowPlaylists);
            Assert.False(viewModel.HasClientId);
            Assert.False(viewModel.NeedsLogin);
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
            Assert.True(viewModel.NeedsLogin);
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
                Assert.False(firstSession.NeedsLogin);
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
            Assert.True(nextSession.NeedsLogin);
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
                "/v1/me/player/play" or "/v1/me/player/shuffle" => new HttpResponseMessage(HttpStatusCode.NoContent),
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
            var playRequest = context.Handler.Requests.FindLast(request => request.Path == "/v1/me/player/play")!;
            using var body = JsonDocument.Parse(playRequest.Body);
            Assert.Contains("device_id=local-device", playRequest.Query);
            Assert.Equal("new-access", context.Playback.Token);
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
        public async Task PlaylistSortPersistsIndependentlyAndCoverPlaysFirstSortedTrackAfterRestart()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var items = JsonSerializer.Serialize(new
            {
                items = new[] { "Zulu", "Alpha", "Beta" }.Select((name, index) => new
                {
                    added_at = $"2026-11-{index + 1:00}T12:00:00Z",
                    item = new
                    {
                        type = "track",
                        uri = $"spotify:track:{index}",
                        name,
                        duration_ms = 180000,
                        artists = new[] { new { name = "Artist" } },
                    },
                }),
                total = 3,
                next = (string?)null,
            });
            HttpResponseMessage Respond(CapturedRequest request) => request.Path switch
            {
                "/v1/me/playlists" => Json("""{"items":[{"id":"one","uri":"spotify:playlist:one","name":"One"},{"id":"two","uri":"spotify:playlist:two","name":"Two"}],"next":null}"""),
                "/v1/playlists/one/items" or "/v1/playlists/two/items" => Json(items),
                "/v1/me/player/play" or "/v1/me/player/shuffle" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException(request.Path),
            };

            using var context = new TestContext(Respond);
            context.SeedToken(false);
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            await viewModel.OpenPlaylistCommand.ExecuteAsync(viewModel.Playlists[0]);
            viewModel.SelectedSort = 1;
            await viewModel.SortLoadingTask;
            Assert.Equal(1, context.Store.Load().PlaylistSorts["one"]);
            Assert.Equal("Alpha", viewModel.PlaylistCards[0].Tracks[0].Name);
            await viewModel.PlayPlaylistCommand.ExecuteAsync(null);
            using (var body = JsonDocument.Parse(context.Handler.Requests.Last(request => request.Path == "/v1/me/player/play").Body))
            {
                Assert.Equal(0, body.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
                Assert.Equal("spotify:track:1", body.RootElement.GetProperty("uris")[0].GetString());
                Assert.Equal(new[] { "spotify:track:1", "spotify:track:2", "spotify:track:0" },
                    body.RootElement.GetProperty("uris").EnumerateArray().Select(item => item.GetString()));
            }

            await viewModel.PlayTrackCommand.ExecuteAsync(viewModel.SortedTracks[1]);
            using (var body = JsonDocument.Parse(context.Handler.Requests.Last(request => request.Path == "/v1/me/player/play").Body))
            {
                Assert.Equal(1, body.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
            }

            context.Playback.PublishProgress(new PlaybackProgress(0, 180000, true, "spotify:track:2"));
            Assert.Equal("spotify:playlist:one", viewModel.CurrentPlaylistUri);
            await viewModel.NextTrackCommand.ExecuteAsync(null);
            using (var body = JsonDocument.Parse(context.Handler.Requests.Last(request => request.Path == "/v1/me/player/play").Body))
            {
                Assert.Equal(2, body.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
            }

            Assert.Equal(0, context.Playback.NextCount);
            await viewModel.PreviousTrackCommand.ExecuteAsync(null);
            using (var body = JsonDocument.Parse(context.Handler.Requests.Last(request => request.Path == "/v1/me/player/play").Body))
            {
                Assert.Equal(1, body.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
            }

            viewModel.BackCommand.Execute(null);
            await viewModel.OpenPlaylistCommand.ExecuteAsync(viewModel.Playlists[1]);
            Assert.Equal(0, viewModel.SelectedSort);
            viewModel.SelectedSort = 4;
            await viewModel.SortLoadingTask;
            Assert.Equal(4, context.Store.Load().PlaylistSorts["two"]);
            Assert.Equal(1, context.Store.Load().PlaylistSorts["one"]);
            viewModel.BackCommand.Execute(null);

            using var restartedContext = new TestContext(Respond);
            restartedContext.SeedToken(false);
            using var restarted = new MainViewModel(restartedContext.Player, context.Store, restartedContext.Startup);
            await restarted.InitializeAsync();
            await restarted.PlaylistCards[0].EnsureLoadedAsync();
            Assert.Equal("Alpha", restarted.PlaylistCards[0].Tracks[0].Name);
            Assert.Contains("limit=50", restartedContext.Handler.Requests.Last().Query);
            await restarted.PlayCardCommand.ExecuteAsync(restarted.Playlists[0]);
            using (var body = JsonDocument.Parse(restartedContext.Handler.Requests.Last(request => request.Path == "/v1/me/player/play").Body))
            {
                Assert.Equal(0, body.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
                Assert.Equal("spotify:track:1", body.RootElement.GetProperty("uris")[0].GetString());
            }

            await restarted.OpenPlaylistCommand.ExecuteAsync(restarted.Playlists[0]);
            Assert.Equal(1, restarted.SelectedSort);
            Assert.Equal("Alpha", restarted.SortedTracks[0].Name);
            restarted.BackCommand.Execute(null);
            await restarted.OpenPlaylistCommand.ExecuteAsync(restarted.Playlists[1]);
            Assert.Equal(4, restarted.SelectedSort);
            Assert.Equal("Beta", restarted.SortedTracks[0].Name);
        }

        [Fact]
        public async Task ProgressUpdatesTimersAndSeekingDoesNotJumpWhileDraggingOrSeekAnotherTrack()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            context.Playback.PublishProgress(new PlaybackProgress(30000, 180000, true, "spotify:track:one", "spotify:playlist:demo", "First song", "Artist"));
            Assert.Equal("First song — Artist", viewModel.NowPlaying);
            Assert.True(viewModel.HasNowPlaying);
            Assert.Equal("0:30", viewModel.ElapsedTime);
            Assert.Equal("-2:30", viewModel.RemainingTime);
            viewModel.BeginSeek();
            viewModel.PositionMs = 90000;
            context.Playback.PublishProgress(new PlaybackProgress(31000, 180000, true, "spotify:track:one"));
            Assert.Equal(90000, viewModel.PositionMs);
            viewModel.CommitSeek();
            Assert.Equal(90000, Assert.Single(context.Playback.Seeks));
            context.Playback.PublishProgress(new PlaybackProgress(91000, 180000, true, "spotify:track:one"));
            Assert.Equal("1:31", viewModel.ElapsedTime);
            viewModel.BeginSeek();
            viewModel.PositionMs = 120000;
            context.Playback.PublishProgress(new PlaybackProgress(0, 240000, true, "spotify:track:two"));
            viewModel.CommitSeek();
            Assert.Single(context.Playback.Seeks);
            Assert.Equal(0, viewModel.PositionMs);
            Assert.Equal("-4:00", viewModel.RemainingTime);
            context.Playback.PublishProgress(new PlaybackProgress(1000, 240000, false, "spotify:track:two"));
            Assert.False(viewModel.CanSeek);
        }

        [Fact]
        public async Task ShuffleAndRepeatTargetEmbeddedDeviceAndFollowSdkState()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path switch
            {
                "/v1/me/playlists" => Json("""{"items":[{"id":"demo","uri":"spotify:playlist:demo","name":"Demo"}],"next":null}"""),
                "/v1/playlists/demo/items" => Json("{\"items\":[{\"item\":" + TrackJson + "}],\"total\":1,\"next\":null}"),
                "/v1/me/player/play" or "/v1/me/player/shuffle" or "/v1/me/player/repeat" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken(false);
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            Assert.False(viewModel.NextTrackCommand.CanExecute(null));
            await viewModel.PlayCardCommand.ExecuteAsync(viewModel.Playlists[0]);
            await viewModel.ToggleShuffleCommand.ExecuteAsync(null);
            Assert.True(viewModel.ShuffleEnabled);
            Assert.Contains("state=true", context.Handler.Requests.Last().Query);
            Assert.Contains("device_id=local-device", context.Handler.Requests.Last().Query);
            foreach (var state in new[] { "context", "track", "off" })
            {
                await viewModel.CycleRepeatCommand.ExecuteAsync(null);
                Assert.False(viewModel.ShuffleEnabled);
                Assert.Contains($"state={state}", context.Handler.Requests.Last().Query);
                Assert.Contains("device_id=local-device", context.Handler.Requests.Last().Query);
            }

            context.Playback.PublishModes(false, 0);
            context.Playback.PublishModes(false, 2);
            Assert.False(viewModel.ShuffleEnabled);
            Assert.True(viewModel.IsRepeatOne);
            await viewModel.ToggleShuffleCommand.ExecuteAsync(null);
            Assert.True(viewModel.ShuffleEnabled);
            Assert.Equal(0, viewModel.RepeatMode);
            Assert.Equal("/v1/me/player/repeat", context.Handler.Requests[^2].Path);
            Assert.Contains("state=off", context.Handler.Requests[^2].Query);
            viewModel.IsBusy = true;
            viewModel.NextTrackCommand.Execute(null);
            viewModel.PreviousTrackCommand.Execute(null);
            Assert.Equal(1, context.Playback.NextCount);
            Assert.Equal(1, context.Playback.PreviousCount);
            viewModel.IsBusy = false;
        }

        [Fact]
        public async Task PauseAndVolumeControlLocalPlayerEvenWhileLibraryIsBusy()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path switch
            {
                "/v1/me/playlists" => Json("""{"items":[{"id":"demo","uri":"spotify:playlist:demo","name":"Demo"}],"next":null}"""),
                "/v1/playlists/demo/items" => Json("{\"items\":[{\"item\":" + TrackJson + "}],\"total\":1,\"next\":null}"),
                "/v1/me/player/play" or "/v1/me/player/shuffle" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken(false);
            using var viewModel = new MainViewModel(context.Player, context.Store, context.Startup);
            await viewModel.InitializeAsync();
            await viewModel.PlayCardCommand.ExecuteAsync(viewModel.Playlists[0]);
            Assert.Equal(10, viewModel.Volume);
            Assert.Equal(0.1, context.Playback.Volume);
            viewModel.IsBusy = true;

            Assert.True(viewModel.TogglePlaybackCommand.CanExecute(null));
            viewModel.TogglePlaybackCommand.Execute(null);
            Assert.False(context.Playback.IsPlaying);
            Assert.False(viewModel.IsPlaying);
            viewModel.Volume = 23;
            Assert.Equal(0.23, context.Playback.Volume);
            viewModel.Volume = 0;
            Assert.Equal(0, context.Playback.Volume);
            viewModel.TogglePlaybackCommand.Execute(null);
            Assert.True(viewModel.IsPlaying);
            viewModel.IsBusy = false;
        }

        [Fact]
        public async Task PlaybackWaitsForLocalDeviceAndNeverUsesDefaultDevice()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
            context.SeedToken(false);
            context.Playback.Ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await context.Player.RestoreAsync(ClientId, CancellationToken.None);

            var play = context.Player.PlayAsync("spotify:playlist:demo", 7, CancellationToken.None);
            Assert.False(play.IsCompleted);
            Assert.Empty(context.Handler.Requests);
            context.Playback.Ready.SetResult("embedded-device");
            await play;

            var request = Assert.Single(context.Handler.Requests, item => item.Path == "/v1/me/player/play");
            Assert.Contains("device_id=embedded-device", request.Query);
            var shuffleRequest = Assert.Single(context.Handler.Requests, item => item.Path == "/v1/me/player/shuffle");
            Assert.Contains("state=false", shuffleRequest.Query);
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal(7, body.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
            context.Player.Disconnect();
            Assert.Equal(1, context.Playback.ResetCount);
        }

        [Fact]
        public async Task OldAuthorizationRequiresNewStreamingConsentBeforePlayback()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            context.SeedToken(false);
            var token = context.Store.LoadToken(ClientId)!;
            token.Scope = "playlist-read-private user-modify-playback-state";
            context.Store.SaveToken(ClientId, token);
            await context.Player.RestoreAsync(ClientId, CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Player.PlayAsync("spotify:playlist:demo", null, CancellationToken.None));

            Assert.Equal(StreamingConstants.Reauthorize, error.Message);
            Assert.Null(context.Playback.Token);
            Assert.Empty(context.Handler.Requests);
        }

        [Fact]
        public async Task LocalDeviceFailureDoesNotStartPlaybackOnAnotherSpotifyDevice()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected network request."));
            context.SeedToken(false);
            context.Playback.Ready = new TaskCompletionSource<string>();
            context.Playback.Ready.SetException(new InvalidOperationException(StreamingConstants.PremiumRequired));
            await context.Player.RestoreAsync(ClientId, CancellationToken.None);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Player.PlayAsync("spotify:playlist:demo", null, CancellationToken.None));

            Assert.Equal(StreamingConstants.PremiumRequired, error.Message);
            Assert.Empty(context.Handler.Requests);
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
            Assert.Equal(1, context.Playback.ResetCount);
            Assert.True(viewModel.IsConnected);
            Assert.False(viewModel.IsBusy);
        }

        private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };

        private sealed class TestContext : IDisposable
        {
            private readonly bool _ownsDirectory;

            public TestContext(Func<CapturedRequest, HttpResponseMessage> respond, string? directory = null)
            {
                _ownsDirectory = directory is null;
                Directory = directory ?? Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests", Guid.NewGuid().ToString("N"));
                Store = new SettingsStore(Directory);
                Handler = new FakeSpotifyHandler(respond);
                Player = new MPlayerService(Store, SpotifyClientConfig.CreateDefault()
                    .WithHTTPClient(new NetHttpClient(new HttpClient(Handler))), Playback);
            }

            public string Directory { get; }

            public SettingsStore Store { get; }

            public FakeStartupService Startup { get; } = new();

            public FakeSpotifyHandler Handler { get; }

            public MPlayerService Player { get; }

            public FakePlaybackEngine Playback { get; } = new();

            public void SeedToken(bool expired = true)
            {
                Store.Save(new AppSettings { ClientId = ClientId, AlwaysOnTop = true });
                Store.SaveToken(ClientId, new PKCETokenResponse
                {
                    AccessToken = "old-access",
                    RefreshToken = "old-refresh",
                    ExpiresIn = 3600,
                    TokenType = "Bearer",
                    Scope = "streaming user-read-email user-read-private user-modify-playback-state",
                    CreatedAt = expired ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow,
                });
            }

            public void Dispose()
            {
                Player.Dispose();
                Handler.Dispose();
                var testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KK.SFPlayer.Tests")) + Path.DirectorySeparatorChar;
                if (_ownsDirectory && Path.GetFullPath(Directory).StartsWith(testRoot, StringComparison.OrdinalIgnoreCase) &&
                    System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, true);
                }
            }
        }

        private sealed class FakePlaybackEngine : ILocalPlaybackEngine
        {
            public int DeviceRequests { get; private set; }
            public event EventHandler<PlaybackProgress>? ProgressChanged;

            public List<int> Seeks { get; } = [];

            public void Seek(int positionMs) => Seeks.Add(positionMs);

            public void PublishProgress(PlaybackProgress progress) => ProgressChanged?.Invoke(this, progress);

            public event EventHandler<PlaybackModes>? ModesChanged;

            public void PublishModes(bool shuffle, int repeat) => ModesChanged?.Invoke(this, new PlaybackModes(shuffle, repeat));

            public int NextCount { get; private set; }

            public int PreviousCount { get; private set; }

            public void NextTrack() => NextCount++;

            public void PreviousTrack() => PreviousCount++;

            public event EventHandler<bool>? PlayingChanged;

            public bool IsPlaying { get; private set; } = true;

            public double Volume { get; private set; } = StreamingConstants.DefaultVolume;

            public void TogglePause()
            {
                IsPlaying = !IsPlaying;
                PlayingChanged?.Invoke(this, IsPlaying);
            }

            public void SetVolume(double volume) => Volume = volume;

            public string? Token { get; private set; }

            public int ResetCount { get; private set; }

            public TaskCompletionSource<string>? Ready { get; set; }

            public async Task<string> GetDeviceAsync(Func<CancellationToken, Task<string>> getAccessToken, CancellationToken cancellationToken)
            {
                DeviceRequests++;
                Token = await getAccessToken(cancellationToken);
                return Ready is null ? "local-device" : await Ready.Task.WaitAsync(cancellationToken);
            }

            public void Reset() => ResetCount++;

            public void Dispose() { }
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
