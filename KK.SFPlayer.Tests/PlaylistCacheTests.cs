using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Constants;
using KK.SFPlayer.Models.Records;
using KK.SFPlayer.ViewModels;
using SpotifyAPI.Web;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed partial class SpotifyIntegrationTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task VersionCheckUpdatesPlaylistCoverIndependentlyOfTrackSnapshot(bool changedSnapshot)
        {
            using var context = new TestContext(request => request.Path switch
            {
                "/v1/me/playlists" => Json("""{"items":[{"id":"demo","uri":"spotify:playlist:demo","name":"Demo","snapshot_id":"v1","images":[{"url":"https://example.com/old"}]}],"next":null}"""),
                "/v1/playlists/demo/items" => CacheTracks(["one"]),
                "/v1/playlists/demo" => Json(JsonSerializer.Serialize(new
                {
                    id = "demo",
                    uri = "spotify:playlist:demo",
                    name = "Demo",
                    snapshot_id = changedSnapshot ? "v2" : "v1",
                    images = new[] { new { url = "https://example.com/new" } },
                })),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken(false);
            using var model = new MainViewModel(context.Player, context.Store, context.Startup);
            await model.InitializeAsync();
            await model.Library.OpenPlaylistCommand.ExecuteAsync(model.Library.Playlists[0]);
            await context.Player.WaitForBackgroundWorkAsync();
            var trackRequests = context.Handler.Requests.Count(request => request.Path.EndsWith("/items", StringComparison.Ordinal));

            await model.Playlist.RefreshAsync();

            Assert.Equal("https://example.com/new", model.Playlist.SelectedPlaylist?.ImageUrl);
            Assert.Equal("https://example.com/new", model.Library.PlaylistCards[0].Playlist.ImageUrl);
            Assert.Equal("https://example.com/new", model.Library.Playlists[0].ImageUrl);
            Assert.Equal("https://example.com/new", context.Player.GetCachedPlaylists()?.Playlists[0].ImageUrl);
            var afterRefresh = context.Handler.Requests.Count(request => request.Path.EndsWith("/items", StringComparison.Ordinal));
            Assert.Equal(changedSnapshot ? trackRequests + 1 : trackRequests, afterRefresh);
            Assert.Contains("images", Uri.UnescapeDataString(context.Handler.Requests.Single(request => request.Path == "/v1/playlists/demo").Query));
        }

        [Fact]
        public async Task BackgroundWarmupReusesFirstPageAndKeepsOriginalCursorWithUnavailableTracks()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request =>
            {
                if (request.Path == "/v1/me/playlists")
                {
                    return Json("""{"items":[{"id":"demo","uri":"spotify:playlist:demo","name":"Demo","snapshot_id":"v1"}],"next":null}""");
                }

                var offset = request.Query.Contains("offset=100", StringComparison.Ordinal) ? 100 :
                    request.Query.Contains("offset=50", StringComparison.Ordinal) ? 50 : 0;
                return Json(JsonSerializer.Serialize(new
                {
                    items = Enumerable.Range(offset, Math.Min(50, 120 - offset)).Select(index => new
                    {
                        is_local = index == 7,
                        item = index == 13 ? null : new
                        {
                            type = "track",
                            uri = $"spotify:track:{index}",
                            name = $"Track {index}",
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
            await context.Player.GetPlaylistPageAsync(0, CancellationToken.None);
            var first = await context.Player.GetSortedTrackPageAsync("demo", 0, 0, CancellationToken.None);
            Assert.Equal(48, first.Tracks.Count);
            await context.Player.WaitForBackgroundWorkAsync();
            var second = await context.Player.GetSortedTrackPageAsync("demo", 0, first.NextOffset, CancellationToken.None);
            var third = await context.Player.GetSortedTrackPageAsync("demo", 0, second.NextOffset, CancellationToken.None);
            Assert.Equal(Enumerable.Range(0, 120).Except([7, 13]), first.Tracks.Concat(second.Tracks).Concat(third.Tracks).Select(track => track.Position));
            Assert.Equal(3, context.Handler.Requests.Count(request => request.Path.EndsWith("/items", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task CorruptCacheIsIgnoredAndCachedPlaylistsBeyondFirstPageAreVersionChecked()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            using var context = new TestContext(request => request.Path switch
            {
                "/v1/me/playlists" => Json("""{"items":[{"id":"first","uri":"spotify:playlist:first","name":"First","snapshot_id":"s1"}],"next":null}"""),
                "/v1/playlists/other" => Json("""{"snapshot_id":"v2"}"""),
                "/v1/playlists/gone" => Json("""{"error":{"status":404,"message":"Not found"}}""", HttpStatusCode.NotFound),
                "/v1/playlists/other/items" => CacheTracks(["new"]),
                "/v1/playlists/first/items" => CacheTracks(["first"]),
                _ => throw new InvalidOperationException(request.Path),
            });
            context.SeedToken(false);
            var cache = new KK.SFPlayer.Services.PlaylistCacheStore(context.Store.DataDirectory);
            cache.SaveTracks(ClientId, new CachedPlaylistData("other", "v1", [new TrackInfo("spotify:track:old", "old", "Artist", "3:00", null, 0, 180000)]));
            cache.SaveTracks(ClientId, new CachedPlaylistData("gone", "v1", []));
            cache.SaveLibrary(ClientId, new PlaylistPage([], 0, false));
            var cacheDirectory = Path.Combine(context.Directory, "PlaylistCache");
            File.WriteAllText(Directory.EnumerateFiles(cacheDirectory, "library.json", SearchOption.AllDirectories).Single(), "{broken");
            Assert.Null(cache.LoadLibrary(ClientId));
            await context.Player.RestoreAsync(ClientId, CancellationToken.None);
            await context.Player.GetPlaylistPageAsync(0, CancellationToken.None);
            await context.Player.WaitForBackgroundWorkAsync();
            var other = await context.Player.GetSortedTrackPageAsync("other", 0, 0, CancellationToken.None);
            Assert.Equal("new", other.Tracks[0].Name);
            Assert.Contains(context.Handler.Requests, request => request.Path == "/v1/playlists/other");
            Assert.Single(context.Handler.Requests, request => request.Path == "/v1/playlists/other/items");
            Assert.DoesNotContain(cache.LoadTracks(ClientId), playlist => playlist.PlaylistId == "gone");
        }

        [Fact]
        public async Task DiskCacheSurvivesRestartAndOnlyChangedVersionDownloadsTracks()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var version = "v1";
            var names = new[] { "one", "two", "three" };
            HttpResponseMessage Respond(CapturedRequest request) => request.Path switch
            {
                "/v1/me/playlists" => Json(JsonSerializer.Serialize(new
                {
                    items = new[] { new { id = "demo", uri = "spotify:playlist:demo", name = "Demo", snapshot_id = version } },
                    next = (string?)null,
                })),
                "/v1/playlists/demo" => Json(JsonSerializer.Serialize(new { snapshot_id = version })),
                "/v1/playlists/demo/items" => CacheTracks(names),
                "/v1/me/player/play" or "/v1/me/player/shuffle" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException(request.Path),
            };
            using var initial = new TestContext(Respond);
            initial.SeedToken(false);
            using (var model = new MainViewModel(initial.Player, initial.Store, initial.Startup))
            {
                await model.InitializeAsync();
                await model.Library.OpenPlaylistCommand.ExecuteAsync(model.Library.Playlists[0]);
                await initial.Player.WaitForBackgroundWorkAsync();
                Assert.Equal(3, model.Playlist.Tracks.Count);
            }

            using var restarted = new TestContext(Respond, initial.Directory);
            using var viewModel = new MainViewModel(restarted.Player, restarted.Store, restarted.Startup);
            await viewModel.InitializeAsync();
            await restarted.Player.WaitForBackgroundWorkAsync();
            await viewModel.Library.OpenPlaylistCommand.ExecuteAsync(viewModel.Library.Playlists[0]);
            Assert.Equal(names, viewModel.Playlist.Tracks.Select(track => track.Name));
            Assert.DoesNotContain(restarted.Handler.Requests, request => request.Path.EndsWith("/items", StringComparison.Ordinal));
            await viewModel.RefreshCommand.ExecuteAsync(null);
            Assert.Equal("/v1/playlists/demo", restarted.Handler.Requests.Last().Path);
            Assert.DoesNotContain(restarted.Handler.Requests, request => request.Path.EndsWith("/items", StringComparison.Ordinal));

            version = "v2";
            names = ["three", "new", "one"];
            await viewModel.RefreshCommand.ExecuteAsync(null);
            await restarted.Player.WaitForBackgroundWorkAsync();
            await viewModel.Playlist.SortLoadingTask;
            Assert.Equal(names, viewModel.Playlist.Tracks.Select(track => track.Name));
            Assert.Single(restarted.Handler.Requests, request => request.Path.EndsWith("/items", StringComparison.Ordinal));
            await restarted.Player.PlayAsync("spotify:playlist:demo", 0, CancellationToken.None, "spotify:track:one");
            using (var play = JsonDocument.Parse(restarted.Handler.Requests.Last(request => request.Path == "/v1/me/player/play").Body))
            {
                Assert.Equal(2, play.RootElement.GetProperty("offset").GetProperty("position").GetInt32());
            }

            var removed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                restarted.Player.PlayAsync("spotify:playlist:demo", 1, CancellationToken.None, "spotify:track:two"));
            Assert.Equal(PlaylistConstants.TrackRemoved, removed.Message);
            viewModel.Playlist.BackCommand.Execute(null);
            await viewModel.RefreshCommand.ExecuteAsync(null);
            await restarted.Player.WaitForBackgroundWorkAsync();
            Assert.Single(restarted.Handler.Requests, request => request.Path.EndsWith("/items", StringComparison.Ordinal));
        }

        [Fact]
        public async Task StartupRefreshesChangedCachedPlaylistAutomatically()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var version = "v1";
            HttpResponseMessage Respond(CapturedRequest request) => request.Path switch
            {
                "/v1/me/playlists" => Json(JsonSerializer.Serialize(new
                {
                    items = new[] { new { id = "demo", uri = "spotify:playlist:demo", name = "Demo", snapshot_id = version } },
                    next = (string?)null,
                })),
                "/v1/playlists/demo/items" => CacheTracks(version == "v1" ? ["old", "removed"] : ["added", "old"]),
                _ => throw new InvalidOperationException(request.Path),
            };
            using var initial = new TestContext(Respond);
            initial.SeedToken(false);
            await initial.Player.RestoreAsync(ClientId, CancellationToken.None);
            await initial.Player.GetPlaylistPageAsync(0, CancellationToken.None);
            await initial.Player.GetSortedTrackPageAsync("demo", 0, 0, CancellationToken.None);
            await initial.Player.WaitForBackgroundWorkAsync();
            version = "v2";
            using var restarted = new TestContext(Respond, initial.Directory);
            using var model = new MainViewModel(restarted.Player, restarted.Store, restarted.Startup);
            await model.InitializeAsync();
            await restarted.Player.WaitForBackgroundWorkAsync();
            await model.Library.OpenPlaylistCommand.ExecuteAsync(model.Library.Playlists[0]);
            Assert.Equal(new[] { "added", "old" }, model.Playlist.Tracks.Select(track => track.Name));
            Assert.Single(restarted.Handler.Requests, request => request.Path.EndsWith("/items", StringComparison.Ordinal));
        }

        [Fact]
        public async Task FailedBackgroundRefreshKeepsOldCacheAndF5RetriesVersionCheck()
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var version = "v1";
            var fail = false;
            HttpResponseMessage Respond(CapturedRequest request) => request.Path switch
            {
                "/v1/me/playlists" => Json(JsonSerializer.Serialize(new
                {
                    items = new[] { new { id = "demo", uri = "spotify:playlist:demo", name = "Demo", snapshot_id = version } },
                    next = (string?)null,
                })),
                "/v1/playlists/demo" => Json(JsonSerializer.Serialize(new { snapshot_id = version })),
                "/v1/playlists/demo/items" when fail => Json("""{"error":{"status":503,"message":"Unavailable"}}""", HttpStatusCode.ServiceUnavailable),
                "/v1/playlists/demo/items" => CacheTracks([version]),
                _ => throw new InvalidOperationException(request.Path),
            };
            using var context = new TestContext(Respond);
            context.SeedToken(false);
            await context.Player.RestoreAsync(ClientId, CancellationToken.None);
            await context.Player.GetPlaylistPageAsync(0, CancellationToken.None);
            await context.Player.GetSortedTrackPageAsync("demo", 0, 0, CancellationToken.None);
            await context.Player.WaitForBackgroundWorkAsync();
            version = "v2";
            fail = true;
            await context.Player.CheckPlaylistVersionAsync("demo", CancellationToken.None);
            await context.Player.WaitForBackgroundWorkAsync();
            var old = await context.Player.GetSortedTrackPageAsync("demo", 0, 0, CancellationToken.None);
            Assert.Equal("v1", old.Tracks[0].Name);
            await Assert.ThrowsAsync<APIException>(() => context.Player.EnsurePlaylistFreshAsync("demo", CancellationToken.None));
            fail = false;
            await context.Player.CheckPlaylistVersionAsync("demo", CancellationToken.None);
            await context.Player.EnsurePlaylistFreshAsync("demo", CancellationToken.None);
            var updated = await context.Player.GetSortedTrackPageAsync("demo", 0, 0, CancellationToken.None);
            Assert.Equal("v2", updated.Tracks[0].Name);
        }

        private static HttpResponseMessage CacheTracks(string[] names) => Json(JsonSerializer.Serialize(new
        {
            items = names.Select(name => new
            {
                added_at = "2026-01-01T00:00:00Z",
                item = new
                {
                    type = "track",
                    uri = $"spotify:track:{name}",
                    name,
                    duration_ms = 180000,
                    artists = new[] { new { name = "Artist" } },
                },
            }),
            total = names.Length,
            next = (string?)null,
        }));
    }
}
