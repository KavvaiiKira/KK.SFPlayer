using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KK.SFPlayer.Models;
using KK.SFPlayer.Services;
using KK.SFPlayer.ViewModels;
using KK.SFPlayer.Views;
using Xunit;

namespace KK.SFPlayer.Tests
{
    public sealed class PlaylistLayoutTests
    {
        [Fact]
        public async Task OnlyVisiblePlaylistsLoadTracksAndScrollingLoadsNextPage()
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    RunLayoutScenario();
                    completed.SetResult();
                }
                catch (Exception exception)
                {
                    completed.SetException(exception);
                }
            }) { IsBackground = true };
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.STA);
            }

            thread.Start();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private static void RunLayoutScenario()
        {
            AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            var directory = Path.Combine(Path.GetTempPath(), "KK.SFPlayer.LayoutTests", Guid.NewGuid().ToString("N"));
            var store = new SettingsStore(directory);
            store.Save(new AppSettings { ClientId = "0123456789abcdef0123456789abcdef" });
            var player = new PreviewPlayer();
            var viewModel = new MainViewModel(player, store);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                Layout(window);
                var list = window.FindControl<ListBox>("PlaylistsList")!;
                var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();

                Assert.InRange(list.Bounds.Height, 200, 400);
                Assert.Equal(10, viewModel.PlaylistCards.Count);
                Assert.InRange(player.Previews.Count, 1, 3);
                Assert.Single(player.Pages);

                scroll.Offset = new Vector(0, scroll.Extent.Height);
                Layout(window);

                Assert.Equal(20, viewModel.PlaylistCards.Count);
                Assert.Equal(new[] { 0, 10 }, player.Pages);
                Assert.True(player.Previews.Count < 10);

                window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                Layout(window);

                Assert.Equal(10, viewModel.PlaylistCards.Count);
                Assert.Equal(new[] { 0, 10, 0 }, player.Pages);
            }
            finally
            {
                window.Close();
                var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KK.SFPlayer.LayoutTests")) + Path.DirectorySeparatorChar;
                if (Path.GetFullPath(directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        private static void Layout(Window window)
        {
            window.Measure(new Size(450, 450));
            window.Arrange(new Rect(0, 0, 450, 450));
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(450, 450));
            window.Arrange(new Rect(0, 0, 450, 450));
            Dispatcher.UIThread.RunJobs();
        }

        private sealed class PreviewPlayer : IMPlayerService
        {
            public bool IsConnected { get; private set; } = true;
            public List<int> Pages { get; } = [];
            public HashSet<string> Previews { get; } = [];

            public Task<bool> RestoreAsync(string clientId, CancellationToken cancellationToken) => Task.FromResult(true);

            public Task ConnectAsync(string clientId, CancellationToken cancellationToken) => throw new InvalidOperationException();

            public void Disconnect() => IsConnected = false;

            public Task<IReadOnlyList<PlaylistInfo>> GetPlaylistsAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

            public Task<IReadOnlyList<TrackInfo>> GetTracksAsync(string playlistId, CancellationToken cancellationToken) => throw new InvalidOperationException();

            public Task PlayAsync(string playlistUri, int? position, CancellationToken cancellationToken) => throw new InvalidOperationException();

            public Task<PlaylistPage> GetPlaylistPageAsync(int offset, CancellationToken cancellationToken)
            {
                Pages.Add(offset);
                var playlists = Enumerable.Range(offset, 10)
                    .Select(index => new PlaylistInfo(index.ToString(), $"spotify:playlist:{index}", $"Playlist {index}", string.Empty, null))
                    .ToArray();
                return Task.FromResult(new PlaylistPage(playlists, offset + 10, offset == 0));
            }

            public Task<PlaylistPreview> GetTrackPreviewAsync(string playlistId, CancellationToken cancellationToken)
            {
                Previews.Add(playlistId);
                return Task.FromResult(new PlaylistPreview(
                    Enumerable.Range(0, 5).Select(index => new TrackInfo($"spotify:track:{index}", $"Track {index}", "Artist", "3:00", null, index)).ToArray(),
                    string.Empty));
            }

            public void Dispose() { }
        }
    }
}
