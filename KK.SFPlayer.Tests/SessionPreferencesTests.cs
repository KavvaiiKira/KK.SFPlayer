using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using KK.SFPlayer.Models.Records;
using KK.SFPlayer.ViewModels;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed partial class SpotifyIntegrationTests
    {
        [Fact]
        public async Task VolumeAndWindowSizeSurviveRestartWithoutLosingOtherSettings()
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected request."));
            context.Store.Save(new AppSettings
            {
                Volume = 0,
                WindowWidth = 900,
                WindowHeight = 600,
                AlwaysOnTop = true,
                PlaylistSorts = new Dictionary<string, int> { ["demo"] = 4 },
            });
            using (var first = new MainViewModel(context.Player, context.Store, context.Startup))
            {
                await first.InitializeAsync();
                Assert.Equal(0, first.Playback.Volume);
                Assert.Equal(0, context.Playback.Volume);
                Assert.Equal(900, first.WindowWidth);
                Assert.Equal(600, first.WindowHeight);
                first.Playback.Volume = 37;
                first.WindowWidth = 1024;
                first.WindowHeight = 700;
            }

            using var restartedContext = new TestContext(_ => throw new InvalidOperationException("Unexpected request."), context.Directory);
            using var restarted = new MainViewModel(restartedContext.Player, restartedContext.Store, restartedContext.Startup);
            await restarted.InitializeAsync();
            Assert.Equal(37, restarted.Playback.Volume);
            Assert.Equal(0.37, restartedContext.Playback.Volume);
            Assert.Equal(1024, restarted.WindowWidth);
            Assert.Equal(700, restarted.WindowHeight);
            Assert.True(context.Store.Load().AlwaysOnTop);
            Assert.Equal(4, context.Store.Load().PlaylistSorts["demo"]);
        }

        [Theory]
        [InlineData(true, 0, "off")]
        [InlineData(false, 1, "context")]
        [InlineData(false, 2, "track")]
        [InlineData(false, 0, "off")]
        public async Task PlaybackModesSurviveRestartAndAreRestoredOnFirstPlay(bool shuffle, int repeat, string repeatState)
        {
            HttpResponseMessage Respond(CapturedRequest request) => request.Path switch
            {
                "/v1/me/playlists" => Json("""{"items":[{"id":"demo","uri":"spotify:playlist:demo","name":"Demo"}],"next":null}"""),
                "/v1/playlists/demo/items" => Json("{\"items\":[{\"item\":" + TrackJson + "}],\"total\":1,\"next\":null}"),
                "/v1/me/player/play" or "/v1/me/player/shuffle" or "/v1/me/player/repeat" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException(request.Path),
            };
            using var context = new TestContext(Respond);
            context.SeedToken(false);
            using (var first = new MainViewModel(context.Player, context.Store, context.Startup))
            {
                await first.InitializeAsync();
                await first.Playback.TogglePlaybackCommand.ExecuteAsync(null);
                if (shuffle)
                {
                    await first.Playback.ToggleShuffleCommand.ExecuteAsync(null);
                }
                for (var index = 0; index < repeat; index++)
                {
                    await first.Playback.CycleRepeatCommand.ExecuteAsync(null);
                }
            }
            Assert.Equal(shuffle, context.Store.Load().ShuffleEnabled);
            Assert.Equal(repeat, context.Store.Load().RepeatMode);
            using var restartedContext = new TestContext(Respond, context.Directory);
            using var restarted = new MainViewModel(restartedContext.Player, restartedContext.Store, restartedContext.Startup);
            await restarted.InitializeAsync();
            restartedContext.Playback.PublishModes(!shuffle, repeat == 2 ? 0 : 2);
            Assert.Equal(shuffle, restarted.Playback.ShuffleEnabled);
            Assert.Equal(repeat, restarted.Playback.RepeatMode);

            await restarted.Playback.TogglePlaybackCommand.ExecuteAsync(null);

            var shuffleRequest = Assert.Single(restartedContext.Handler.Requests, request => request.Path == "/v1/me/player/shuffle");
            var repeatRequest = Assert.Single(restartedContext.Handler.Requests, request => request.Path == "/v1/me/player/repeat");
            Assert.Contains("state=" + shuffle.ToString().ToLowerInvariant(), shuffleRequest.Query);
            Assert.Contains("state=" + repeatState, repeatRequest.Query);
            Assert.Equal(shuffle, restarted.Playback.ShuffleEnabled);
            Assert.Equal(repeat, restarted.Playback.RepeatMode);
        }

        [Theory]
        [InlineData(35.4, 35, 0.35)]
        [InlineData(35.5, 36, 0.36)]
        [InlineData(-20, 0, 0)]
        [InlineData(120, 100, 1)]
        public void VolumeUsesWholePercentAndPassesNormalizedValueToPlayback(double requested, double percent, double localVolume)
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected request."));
            using var model = new MainViewModel(context.Player, context.Store, context.Startup);
            model.Playback.Volume = requested;
            Assert.Equal(percent, model.Playback.Volume);
            Assert.Equal(localVolume, context.Playback.Volume);
            Assert.Equal($"Volume: {percent:0}%", model.Playback.VolumeDescription);
        }

        [Theory]
        [InlineData(200, 300, 470, 450)]
        [InlineData(1900, 1000, 1280, 720)]
        public async Task RestoredWindowSizeStaysWithinAllowedBounds(double width, double height, double expectedWidth, double expectedHeight)
        {
            using var context = new TestContext(_ => throw new InvalidOperationException("Unexpected request."));
            context.Store.Save(new AppSettings { WindowWidth = width, WindowHeight = height });
            using var model = new MainViewModel(context.Player, context.Store, context.Startup);
            await model.InitializeAsync();
            Assert.Equal(expectedWidth, model.WindowWidth);
            Assert.Equal(expectedHeight, model.WindowHeight);
        }
    }
}
