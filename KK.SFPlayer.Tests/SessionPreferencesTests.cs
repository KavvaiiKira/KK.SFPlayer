using System;
using System.Collections.Generic;
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
