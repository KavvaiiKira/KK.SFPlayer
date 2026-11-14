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
using KK.SFPlayer.Controls;
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

                var initialPlaybackButton = window.FindControl<Button>("PlaybackButton")!;
                Assert.True(initialPlaybackButton.IsEnabled);
                Click(window, initialPlaybackButton.TranslatePoint(new Point(15, 15), window)!.Value);
                Assert.Equal(viewModel.Playlists[0].Uri, Assert.Single(player.Played).Uri);
                Assert.Null(viewModel.SelectedPlaylist);
                player.Played.Clear();

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

                scroll.Offset = default;
                Layout(window);
                var card = list.GetVisualDescendants().OfType<PlaylistCardView>().First();
                var cover = card.FindControl<Grid>("Cover")!;
                var play = cover.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("cover-play"));
                window.MouseMove(new Point(440, 430));
                Assert.Equal(0, play.Opacity);
                var coverPoint = cover.TranslatePoint(new Point(48, 48), window)!.Value;
                window.MouseMove(coverPoint);
                Assert.Equal(1, play.Opacity);
                Click(window, coverPoint);
                Assert.Single(player.Played);
                Assert.Null(viewModel.SelectedPlaylist);
                var playbackButton = window.FindControl<Button>("PlaybackButton")!;
                var volumeSlider = window.FindControl<Slider>("VolumeSlider")!;
                Assert.Equal(10, volumeSlider.Value);
                window.MouseMove(new Point(440, 430));
                Layout(window);
                var knob = volumeSlider.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "VolumeKnob");
                Assert.Equal(8, knob.Bounds.Width);
                var knobCenter = knob.TranslatePoint(new Point(4, 4), window)!.Value;
                var playbackCenter = playbackButton.TranslatePoint(new Point(15, 15), window)!.Value;
                Assert.InRange(Math.Abs(knobCenter.Y - playbackCenter.Y), 0, 0.5);
                window.MouseMove(volumeSlider.TranslatePoint(new Point(50, 15), window)!.Value);
                Layout(window);
                Assert.Equal(10, knob.Bounds.Width);
                Click(window, volumeSlider.TranslatePoint(new Point(75, 15), window)!.Value);
                Assert.True(volumeSlider.Value > 50, $"Value={volumeSlider.Value}; " + string.Join("; ", volumeSlider.GetVisualDescendants().OfType<Button>().Select(button => $"{button.Name}={button.Bounds}")));
                volumeSlider.Value = 10;
                Click(window, playbackButton.TranslatePoint(new Point(15, 15), window)!.Value);
                Assert.False(viewModel.IsPlaying);
                viewModel.IsBusy = true;
                Assert.True(playbackButton.IsEnabled);
                Click(window, playbackButton.TranslatePoint(new Point(15, 15), window)!.Value);
                Assert.True(viewModel.IsPlaying);
                volumeSlider.Value = 0;
                Assert.Equal(0, player.Volume);
                volumeSlider.Value = 35;
                Assert.Equal(0.35, player.Volume);
                volumeSlider.Value = 10;
                viewModel.IsBusy = false;

                var cardPoint = card.TranslatePoint(new Point(155, 65), window)!.Value;
                Click(window, cardPoint);
                Layout(window);
                Assert.NotNull(viewModel.SelectedPlaylist);
                Assert.False(list.IsVisible);
                Assert.Equal(50, viewModel.SortedTracks.Count);
                Assert.Equal("60 songs", viewModel.PlaylistSummary);
                Assert.Equal(new[] { 0 }, player.TrackOffsets);
                var detail = window.GetVisualDescendants().OfType<PlaylistDetailView>().Single();
                var tracksScroll = detail.FindControl<ListBox>("TracksList")!
                    .GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.True(tracksScroll.Extent.Height > tracksScroll.Viewport.Height);
                tracksScroll.Offset = new Vector(0, 600);
                Layout(window);
                Assert.True(tracksScroll.Offset.Y > 0);
                Assert.Equal(50, viewModel.SortedTracks.Count);
                tracksScroll.Offset = new Vector(0, tracksScroll.Extent.Height);
                Layout(window);
                Assert.Equal(60, viewModel.SortedTracks.Count);
                Assert.Equal("60 songs · 3:00:00", viewModel.PlaylistSummary);
                Assert.Equal(new[] { 0, 50 }, player.TrackOffsets);
                Assert.True(tracksScroll.Offset.Y > 0);

                viewModel.SelectedSort = 1;
                Layout(window);
                Assert.Equal("Track 01", viewModel.SortedTracks[0].Name);
                Assert.Equal(59, viewModel.SortedTracks[0].Position);
                Assert.Equal(0, tracksScroll.Offset.Y);
                viewModel.PlayTrackCommand.Execute(viewModel.SortedTracks[0]);
                Assert.Equal(59, player.Played.Last().Position);
                viewModel.SelectedSort = 4;
                Layout(window);
                Assert.Equal(59, viewModel.SortedTracks[0].Position);
                viewModel.SelectedSort = 5;
                Layout(window);
                Assert.Equal(0, viewModel.SortedTracks[0].Position);
                viewModel.SelectedSort = 0;
                Layout(window);
                Assert.Equal(0, viewModel.SortedTracks[0].Position);

                var labels = detail.GetVisualDescendants().OfType<TrimmedTextBlock>().ToArray();
                Assert.True(labels[0].TextLayout.TextLines.Any(line => line.HasCollapsed), $"Width={labels[0].Bounds.Width}; text width={labels[0].TextLayout.Width}");
                window.MouseMove(labels[0].TranslatePoint(new Point(5, 10), window)!.Value);
                Assert.Equal(labels[0].Text, ToolTip.GetTip(labels[0]));
                window.MouseMove(labels[1].TranslatePoint(new Point(5, 10), window)!.Value);
                Assert.Null(ToolTip.GetTip(labels[1]));

                var back = detail.GetVisualDescendants().OfType<Button>()
                    .Single(button => ReferenceEquals(button.Command, viewModel.BackCommand));
                Click(window, back.TranslatePoint(new Point(15, 15), window)!.Value);
                Layout(window);
                Assert.Null(viewModel.SelectedPlaylist);
                Assert.True(list.IsVisible);
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

        private static void Click(Window window, Point point)
        {
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        private sealed class PreviewPlayer : IMPlayerService
        {
            public event EventHandler<bool>? PlayingChanged;

            private bool _playing = true;

            public double Volume { get; private set; } = 0.1;

            public void TogglePause()
            {
                _playing = !_playing;
                PlayingChanged?.Invoke(this, _playing);
            }

            public void SetVolume(double volume) => Volume = volume;

            public bool IsConnected { get; private set; } = true;
            public List<int> Pages { get; } = [];
            public HashSet<string> Previews { get; } = [];
            public List<(string Uri, int? Position)> Played { get; } = [];
            public List<int> TrackOffsets { get; } = [];

            public Task<bool> RestoreAsync(string clientId, CancellationToken cancellationToken) => Task.FromResult(true);

            public Task ConnectAsync(string clientId, CancellationToken cancellationToken) => throw new InvalidOperationException();

            public void Disconnect() => IsConnected = false;

            public Task<IReadOnlyList<PlaylistInfo>> GetPlaylistsAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();

            public Task<IReadOnlyList<TrackInfo>> GetTracksAsync(string playlistId, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Full playlist loading is not expected.");

            public Task<TrackPage> GetTrackPageAsync(string playlistId, int offset, CancellationToken cancellationToken)
            {
                TrackOffsets.Add(offset);
                var tracks = Enumerable.Range(offset, Math.Min(50, 60 - offset))
                    .Select(index => new TrackInfo($"spotify:track:{index}",
                        index == 0 ? new string('W', 1000) : $"Track {60 - index:00}",
                        "Artist", "3:00", null, index, 180000,
                        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index))).ToArray();
                return Task.FromResult(new TrackPage(tracks, offset + tracks.Length, offset == 0, 60));
            }

            public Task PlayAsync(string playlistUri, int? position, CancellationToken cancellationToken)
            {
                Played.Add((playlistUri, position));
                return Task.CompletedTask;
            }

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
