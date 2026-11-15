using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KK.SFPlayer.Models;
using KK.SFPlayer.Models.Records;
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
            })
            { IsBackground = true };
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
                Assert.Equal(10, viewModel.Library.PlaylistCards.Count);
                Assert.InRange(player.Previews.Count, 1, 3);
                Assert.Single(player.Pages);

                var initialPlaybackButton = window.FindControl<Button>("PlaybackButton")!;
                Assert.True(initialPlaybackButton.IsEnabled);
                var loader = window.FindControl<PathIcon>("PlaybackLoader")!;
                player.PublishPreparation(true);
                Layout(window);
                Assert.True(loader.IsVisible);
                Assert.False(initialPlaybackButton.IsEffectivelyEnabled);
                player.PublishPreparation(false);
                player.AutoConfirmPlayback = false;
                Click(window, initialPlaybackButton.TranslatePoint(new Point(15, 15), window)!.Value);
                Assert.Equal(viewModel.Library.Playlists[0].Uri, Assert.Single(player.Played).Uri);
                Assert.Null(viewModel.Playlist.SelectedPlaylist);
                Layout(window);
                Assert.True(loader.IsVisible);
                Assert.False(initialPlaybackButton.IsEffectivelyEnabled);
                player.TogglePause();
                player.TogglePause();
                player.PublishProgress(new PlaybackProgress(0, 180000, true, "spotify:track:0", viewModel.Library.Playlists[0].Uri));
                var startup = viewModel.Playback.TogglePlaybackCommand.ExecutionTask!;
                using (var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                {
                    _ = startup.ContinueWith(_ => startupTimeout.Cancel(), TaskScheduler.Default);
                    Dispatcher.UIThread.MainLoop(startupTimeout.Token);
                }
                Assert.True(startup.IsCompletedSuccessfully);
                Layout(window);
                Assert.False(loader.IsVisible);
                player.AutoConfirmPlayback = true;
                player.Played.Clear();

                scroll.Offset = new Vector(0, scroll.Extent.Height);
                Layout(window);

                Assert.Equal(20, viewModel.Library.PlaylistCards.Count);
                Assert.Equal(new[] { 0, 10 }, player.Pages);
                Assert.True(player.Previews.Count < 10);

                window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                Layout(window);

                Assert.Equal(10, viewModel.Library.PlaylistCards.Count);
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
                Assert.Null(viewModel.Playlist.SelectedPlaylist);
                var playbackButton = window.FindControl<Button>("PlaybackButton")!;
                var volumeSlider = window.FindControl<Slider>("VolumeSlider")!;
                player.PublishProgress(new PlaybackProgress(30000, 180000, true, "spotify:track:one"));
                Layout(window);
                var artwork = window.FindControl<Border>("NowPlayingArtwork")!;
                var footer = window.FindControl<Grid>("PlaybackFooter")!;
                Assert.True(artwork.IsVisible);
                Assert.False(artwork.GetVisualDescendants().OfType<RemoteImage>().Single().CacheOnDisk);
                Assert.True(artwork.GetVisualDescendants().OfType<RemoteImage>().Single().AutoRetry);
                var volumeControl = window.FindControl<Button>("VolumeButton")!;
                var topmostControl = window.FindControl<Button>("TopmostButton")!;
                Assert.True(volumeControl.TranslatePoint(new Point(volumeControl.Bounds.Width, 0), window)!.Value.X + 12 <=
                    topmostControl.TranslatePoint(default, window)!.Value.X);
                Assert.Equal(footer.Bounds.Height, artwork.Bounds.Height);
                Assert.Equal(artwork.Bounds.Width, artwork.Bounds.Height);
                Assert.True(artwork.TranslatePoint(new Point(artwork.Bounds.Width, 0), window)!.Value.X <
                    window.FindControl<Button>("ShuffleButton")!.TranslatePoint(default, window)!.Value.X);
                Assert.Equal("0:30", viewModel.Playback.ElapsedTime);
                Assert.Equal("-2:30", viewModel.Playback.RemainingTime);
                var progressSlider = window.FindControl<Slider>("ProgressSlider")!;
                Click(window, progressSlider.TranslatePoint(new Point(progressSlider.Bounds.Width / 2, 11), window)!.Value);
                Assert.InRange(Assert.Single(player.Seeks), 89990, 90010);
                Assert.False(viewModel.Playback.IsSeeking);
                Assert.Equal(10, volumeSlider.Value);
                var volumeButton = window.FindControl<Button>("VolumeButton")!;
                var volumePopup = window.FindControl<Popup>("VolumePopup")!;
                Assert.False(volumePopup.IsOpen);
                window.MouseMove(volumeButton.TranslatePoint(new Point(12, 12), window)!.Value);
                Layout(window);
                Assert.True(volumePopup.IsOpen);
                Assert.False(volumePopup.IsLightDismissEnabled);
                Assert.True(volumePopup.ShouldUseOverlayLayer);
                using (var hoverTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(450)))
                {
                    Dispatcher.UIThread.MainLoop(hoverTimeout.Token);
                }

                Assert.True(volumePopup.IsOpen);
                Assert.True(volumeButton.IsPointerOver);
                Assert.Equal(18, Assert.IsType<Viewbox>(volumeButton.Content).Width);
                Assert.Equal(Avalonia.Layout.Orientation.Vertical, volumeSlider.Orientation);
                Assert.Equal(1, volumeSlider.SmallChange);
                Assert.Equal(5, volumeSlider.LargeChange);
                Assert.True(volumeSlider.IsSnapToTickEnabled);
                Assert.Equal(playbackButton.Bounds.Width, volumeButton.Bounds.Width);
                var volumeRoot = (WindowBase)TopLevel.GetTopLevel(volumeSlider)!;
                window.MouseMove(new Point(440, 430));
                Layout(window);
                var knob = volumeSlider.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "VolumeKnob");
                Assert.Equal(8, knob.Bounds.Width);
                volumeRoot.MouseMove(volumeSlider.TranslatePoint(new Point(12, 60), volumeRoot)!.Value);
                Layout(window);
                Assert.Equal(10, knob.Bounds.Width);
                volumeRoot.MouseDown(volumeSlider.TranslatePoint(new Point(12, 1), volumeRoot)!.Value, MouseButton.Left);
                volumeRoot.MouseUp(volumeSlider.TranslatePoint(new Point(12, 1), volumeRoot)!.Value, MouseButton.Left);
                Assert.Equal(100, volumeSlider.Value);
                Layout(window);
                Assert.InRange(knob.TranslatePoint(new Point(knob.Bounds.Width / 2, knob.Bounds.Height / 2), volumeSlider)!.Value.Y, 4.9, 5.1);
                volumeRoot.MouseDown(volumeSlider.TranslatePoint(new Point(12, 119), volumeRoot)!.Value, MouseButton.Left);
                volumeRoot.MouseUp(volumeSlider.TranslatePoint(new Point(12, 119), volumeRoot)!.Value, MouseButton.Left);
                Assert.Equal(0, volumeSlider.Value);
                volumeSlider.Focus();
                volumeRoot.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                volumeRoot.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                Assert.Equal(1, volumeSlider.Value);
                Assert.Equal(0.01, player.Volume);
                volumeRoot.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                volumeRoot.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                Assert.Equal(0, volumeSlider.Value);
                Assert.Equal(0, player.Volume);
                Layout(window);
                Assert.InRange(knob.TranslatePoint(new Point(knob.Bounds.Width / 2, knob.Bounds.Height / 2), volumeSlider)!.Value.Y, 114.9, 115.1);
                volumeRoot.MouseMove(new Point(-20, -20));
                window.MouseMove(new Point(440, 430));
                using (var popupTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350)))
                {
                    Dispatcher.UIThread.MainLoop(popupTimeout.Token);
                }
                Layout(window);
                Assert.False(volumePopup.IsOpen);
                volumeSlider.Value = 10;
                Click(window, playbackButton.TranslatePoint(new Point(15, 15), window)!.Value);
                Assert.False(viewModel.Playback.IsPlaying,
                    $"HasPlayback={viewModel.Playback.HasPlayback}; Starting={viewModel.Playback.IsStarting}; Preparing={viewModel.Playback.IsPreparing}; CanToggle={viewModel.Playback.CanTogglePlayback}");
                viewModel.State.IsBusy = true;
                Assert.True(playbackButton.IsEnabled);
                Click(window, playbackButton.TranslatePoint(new Point(15, 15), window)!.Value);
                Assert.True(viewModel.Playback.IsPlaying);
                volumeSlider.Value = 0;
                Assert.Equal(0, player.Volume);
                volumeSlider.Value = 35;
                Assert.Equal(0.35, player.Volume);
                volumeSlider.Value = 10;
                viewModel.State.IsBusy = false;
                var previous = window.FindControl<Button>("PreviousTrackButton")!;
                var next = window.FindControl<Button>("NextTrackButton")!;
                Click(window, previous.TranslatePoint(new Point(12, 12), window)!.Value);
                Click(window, next.TranslatePoint(new Point(12, 12), window)!.Value);
                Assert.Equal(1, player.PreviousCount);
                Assert.Equal(1, player.NextCount);
                next.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Assert.Equal(1, player.NextCount);
                var shuffle = window.FindControl<Button>("ShuffleButton")!;
                Click(window, shuffle.TranslatePoint(new Point(12, 12), window)!.Value);
                Assert.True(viewModel.Playback.ShuffleEnabled);
                Assert.Contains("active", shuffle.Classes);
                var repeat = window.FindControl<Button>("RepeatButton")!;
                Click(window, repeat.TranslatePoint(new Point(12, 12), window)!.Value);
                Assert.Equal(1, viewModel.Playback.RepeatMode);
                Assert.False(viewModel.Playback.ShuffleEnabled);
                Click(window, repeat.TranslatePoint(new Point(12, 12), window)!.Value);
                Assert.True(viewModel.Playback.IsRepeatOne);
                Click(window, repeat.TranslatePoint(new Point(12, 12), window)!.Value);
                Assert.Equal(0, viewModel.Playback.RepeatMode);
                Assert.DoesNotContain("active", repeat.Classes);

                player.ModeResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var shuffleChange = viewModel.Playback.ToggleShuffleCommand.ExecuteAsync(null);
                Layout(window);
                Assert.Contains("active", shuffle.Classes);
                Assert.True(shuffle.IsEnabled);
                Assert.True(repeat.IsEnabled);
                player.PublishModes(false, 0);
                Assert.True(viewModel.Playback.ShuffleEnabled);
                var repeatChange = viewModel.Playback.CycleRepeatCommand.ExecuteAsync(null);
                Layout(window);
                Assert.DoesNotContain("active", shuffle.Classes);
                Assert.Contains("active", repeat.Classes);
                Assert.Equal(1, viewModel.Playback.RepeatMode);
                player.ModeResponse.SetResult();
                using (var modeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                {
                    Dispatcher.UIThread.MainLoop(modeTimeout.Token);
                }

                Assert.True(shuffleChange.IsCompletedSuccessfully);
                Assert.True(repeatChange.IsCompletedSuccessfully);
                player.PublishModes(true, 0);
                Assert.False(viewModel.Playback.ShuffleEnabled);
                Assert.Equal(1, viewModel.Playback.RepeatMode);
                player.PublishModes(false, 1);
                player.ModeResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var failedChange = viewModel.Playback.ToggleShuffleCommand.ExecuteAsync(null);
                Assert.True(viewModel.Playback.ShuffleEnabled);
                player.ModeResponse.SetException(new InvalidOperationException("Mode change failed."));
                using (var modeTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
                {
                    Dispatcher.UIThread.MainLoop(modeTimeout.Token);
                }

                Assert.True(failedChange.IsCompletedSuccessfully);
                Assert.False(viewModel.Playback.ShuffleEnabled);
                Assert.Equal(1, viewModel.Playback.RepeatMode);
                Assert.False(string.IsNullOrEmpty(viewModel.State.Status));
                player.ModeResponse = null;
                viewModel.Playback.CycleRepeatCommand.Execute(null);
                viewModel.Playback.CycleRepeatCommand.Execute(null);

                player.TrackOffsets.Clear();

                var playlistUri = viewModel.Library.Playlists[0].Uri;
                player.PublishProgress(new PlaybackProgress(1000, 180000, true, "spotify:track:1", playlistUri));
                var cardPoint = card.TranslatePoint(new Point(155, 65), window)!.Value;
                Click(window, cardPoint);
                Layout(window);
                Assert.NotNull(viewModel.Playlist.SelectedPlaylist);
                Assert.False(list.IsVisible);
                Assert.Equal(50, viewModel.Playlist.Tracks.Count);
                Assert.Equal("60 songs", viewModel.Playlist.PlaylistSummary);
                Assert.Equal(new[] { 0 }, player.TrackOffsets);
                var detail = window.GetVisualDescendants().OfType<PlaylistDetailView>().Single();
                var tracksScroll = detail.FindControl<ListBox>("TracksList")!
                    .GetVisualDescendants().OfType<ScrollViewer>().First();
                Assert.Equal(playlistUri, viewModel.Playlist.SelectedPlaylist!.Uri);
                Layout(window);
                var currentRow = detail.GetVisualDescendants().OfType<Border>()
                    .First(border => border.Classes.Contains("track-row") && border.DataContext is TrackInfo { Position: 1 });
                Assert.Contains("current", currentRow.Classes);
                Assert.Contains("playing", currentRow.Classes);
                var durationLabel = currentRow.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "3:00");
                var tracksList = detail.FindControl<ListBox>("TracksList")!;
                var durationRight = durationLabel.TranslatePoint(new Point(durationLabel.Bounds.Width, 0), tracksList)!.Value.X;
                Assert.True(durationRight <= tracksList.Bounds.Width - 22);
                Assert.Equal(1, currentRow.GetVisualDescendants().OfType<Grid>().Single(icon => icon.Classes.Contains("playing-indicator")).Opacity);
                player.TogglePause();
                Assert.Contains("current", currentRow.Classes);
                Assert.DoesNotContain("playing", currentRow.Classes);
                Assert.Equal(1, currentRow.GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Classes.Contains("paused-indicator")).Opacity);
                player.TogglePause();
                viewModel.Playback.NextTrackCommand.Execute(null);
                player.PublishProgress(new PlaybackProgress(0, 180000, true, "spotify:track:2", playlistUri));
                Assert.DoesNotContain("current", currentRow.Classes);
                Assert.Single(detail.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("current"));
                viewModel.Playback.PreviousTrackCommand.Execute(null);
                player.PublishProgress(new PlaybackProgress(0, 180000, true, "spotify:track:1", playlistUri));
                Assert.Contains("current", currentRow.Classes);
                player.PublishProgress(new PlaybackProgress(0, 180000, true, "spotify:track:1", "spotify:playlist:another"));
                Assert.DoesNotContain("current", currentRow.Classes);
                viewModel.Playlist.SelectedSort = 4;
                Layout(window);
                Assert.Equal(59, viewModel.Playlist.Tracks[0].Position);
                Assert.Equal(50, viewModel.Playlist.Tracks.Count);
                Assert.True(viewModel.Playlist.HasMoreTracks);
                viewModel.Playlist.SelectedSort = 5;
                Layout(window);
                Assert.Equal(0, viewModel.Playlist.Tracks[0].Position);
                Assert.Equal(50, viewModel.Playlist.Tracks.Count);
                viewModel.Playlist.SelectedSort = 0;
                Layout(window);
                Assert.True(tracksScroll.Extent.Height > tracksScroll.Viewport.Height);
                tracksScroll.Offset = new Vector(0, 600);
                Layout(window);
                Assert.True(tracksScroll.Offset.Y > 0);
                Assert.Equal(50, viewModel.Playlist.Tracks.Count);
                player.PublishProgress(new PlaybackProgress(0, 180000, true, "spotify:track:59", playlistUri));
                tracksScroll.Offset = new Vector(0, tracksScroll.Extent.Height);
                Layout(window);
                Assert.Equal(60, viewModel.Playlist.Tracks.Count);
                Assert.Equal("60 songs · 3:00:00", viewModel.Playlist.PlaylistSummary);
                Assert.Equal(new[] { 0, 0, 50 }, player.TrackOffsets);
                Assert.True(tracksScroll.Offset.Y > 0);
                tracksScroll.Offset = new Vector(0, tracksScroll.Extent.Height);
                Layout(window);
                var lazyCurrent = detail.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("current"));
                Assert.Equal(59, Assert.IsType<TrackInfo>(lazyCurrent.DataContext).Position);

                viewModel.Playlist.SelectedSort = 1;
                Layout(window);
                Assert.Equal("Track 01", viewModel.Playlist.Tracks[0].Name);
                Assert.Equal(59, viewModel.Playlist.Tracks[0].Position);
                Assert.Equal(0, tracksScroll.Offset.Y);
                var firstTrackRow = detail.GetVisualDescendants().OfType<Border>()
                    .First(border => border.Classes.Contains("track-row") && border.DataContext is TrackInfo { Position: 59 });
                Click(window, firstTrackRow.TranslatePoint(new Point(50, 16), window)!.Value);
                var playCount = player.Played.Count;
                Assert.Equal(59, player.Played.Last().Position);
                Assert.IsType<TrackInfo>(detail.FindControl<ListBox>("TracksList")!.SelectedItem);
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Assert.Equal(playCount, player.Played.Count);
                Assert.Equal(59, player.Played.Last().Position);
                viewModel.Playlist.SelectedSort = 4;
                Layout(window);
                Assert.Equal(59, viewModel.Playlist.Tracks[0].Position);
                viewModel.Playlist.SelectedSort = 5;
                Layout(window);
                Assert.Equal(0, viewModel.Playlist.Tracks[0].Position);
                viewModel.Playlist.SelectedSort = 0;
                Layout(window);
                Assert.Equal(0, viewModel.Playlist.Tracks[0].Position);

                var labels = detail.GetVisualDescendants().OfType<TrimmedTextBlock>().ToArray();
                Assert.True(labels[0].TextLayout.TextLines.Any(line => line.HasCollapsed), $"Width={labels[0].Bounds.Width}; text width={labels[0].TextLayout.Width}");
                window.MouseMove(labels[0].TranslatePoint(new Point(5, 10), window)!.Value);
                Assert.Equal(labels[0].Text, ToolTip.GetTip(labels[0]));
                window.MouseMove(labels[1].TranslatePoint(new Point(5, 10), window)!.Value);
                Assert.Null(ToolTip.GetTip(labels[1]));

                var metadataRequests = player.Pages.Count;
                window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                Layout(window);
                Assert.Equal(new[] { viewModel.Playlist.SelectedPlaylist!.Id }, player.VersionChecks);
                Assert.Equal(metadataRequests, player.Pages.Count);
                viewModel.OpenSettingsCommand.Execute(null);
                window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                Layout(window);
                Assert.Single(player.VersionChecks);
                Assert.Equal(metadataRequests, player.Pages.Count);
                viewModel.Settings.CancelCommand.Execute(null);
                viewModel.AccountCommand.Execute(null);
                Assert.True(viewModel.IsLogoutConfirmationOpen);
                window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                window.KeyRelease(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
                Assert.Single(player.VersionChecks);
                Assert.Equal(metadataRequests, player.Pages.Count);
                viewModel.CancelLogoutCommand.Execute(null);

                var back = detail.GetVisualDescendants().OfType<Button>()
                    .Single(button => ReferenceEquals(button.Command, viewModel.Playlist.BackCommand));
                Click(window, back.TranslatePoint(new Point(15, 15), window)!.Value);
                Layout(window);
                Assert.Null(viewModel.Playlist.SelectedPlaylist);
                Assert.True(list.IsVisible);
                Assert.Equal(new[] { 0, 10, 0 }, player.Pages);
                Assert.True(window.CanResize);
                Assert.Equal(470, window.MinWidth);
                Assert.Equal(450, window.MinHeight);
                Assert.Equal(1280, window.MaxWidth);
                Assert.Equal(720, window.MaxHeight);
                Assert.Equal(8, window.GetVisualDescendants().OfType<Border>().Count(border => border.Classes.Contains("resize-handle")));
                window.Width = 1280;
                window.Height = 720;
                Layout(window, 1280, 720);
                Assert.Equal(1280, window.Bounds.Width);
                Assert.Equal(720, window.Bounds.Height);
                Assert.Equal(1280, viewModel.WindowWidth);
                Assert.Equal(720, viewModel.WindowHeight);
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

        private static void Layout(Window window, double width = 470, double height = 450)
        {
            window.Measure(new Size(width, height));
            window.Arrange(new Rect(0, 0, width, height));
            Dispatcher.UIThread.RunJobs();
            window.Measure(new Size(width, height));
            window.Arrange(new Rect(0, 0, width, height));
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
            public event EventHandler<bool>? PreparationChanged;

            public void PublishPreparation(bool preparing) => PreparationChanged?.Invoke(this, preparing);

            public bool AutoConfirmPlayback { get; set; } = true;

            public event EventHandler<string>? PlaylistChanged { add { } remove { } }
            public event EventHandler<string>? CacheWarning { add { } remove { } }
            public PlaylistPage? GetCachedPlaylists() => null;
            public Task EnsurePlaylistFreshAsync(string playlistId, CancellationToken cancellationToken) => Task.CompletedTask;
            public List<string> VersionChecks { get; } = [];

            public Task<PlaylistInfo?> CheckPlaylistVersionAsync(string playlistId, CancellationToken cancellationToken)
            {
                VersionChecks.Add(playlistId);
                return Task.FromResult<PlaylistInfo?>(null);
            }
            public event EventHandler<PlaybackProgress>? ProgressChanged;

            public List<int> Seeks { get; } = [];

            public void Seek(int positionMs) => Seeks.Add(positionMs);

            public void PublishProgress(PlaybackProgress progress) => ProgressChanged?.Invoke(this, progress);

            public event EventHandler<PlaybackModes>? ModesChanged;

            private bool _shuffle;
            private int _repeat;
            public TaskCompletionSource? ModeResponse { get; set; }

            public void PublishModes(bool shuffle, int repeat) => ModesChanged?.Invoke(this, new PlaybackModes(shuffle, repeat));

            public int NextCount { get; private set; }
            public int PreviousCount { get; private set; }

            public Task NextTrackAsync(CancellationToken cancellationToken)
            {
                NextCount++;
                return Task.CompletedTask;
            }

            public Task PreviousTrackAsync(CancellationToken cancellationToken)
            {
                PreviousCount++;
                return Task.CompletedTask;
            }

            public async Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken)
            {
                if (ModeResponse is { } response)
                {
                    await response.Task.WaitAsync(cancellationToken);
                }

                _shuffle = enabled;
                ModesChanged?.Invoke(this, new PlaybackModes(_shuffle, _repeat));
            }

            public async Task SetRepeatAsync(int mode, CancellationToken cancellationToken)
            {
                if (ModeResponse is { } response)
                {
                    await response.Task.WaitAsync(cancellationToken);
                }

                _repeat = mode;
                ModesChanged?.Invoke(this, new PlaybackModes(_shuffle, _repeat));
            }

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

            private Task<TrackPage> GetTrackPageAsync(string playlistId, int offset, CancellationToken cancellationToken)
            {
                TrackOffsets.Add(offset);
                var tracks = Enumerable.Range(offset, Math.Min(50, 60 - offset))
                    .Select(index => new TrackInfo($"spotify:track:{index}",
                        index == 0 ? new string('W', 1000) : $"Track {60 - index:00}",
                        "Artist", "3:00", null, index, 180000,
                        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index))).ToArray();
                return Task.FromResult(new TrackPage(tracks, offset + tracks.Length, offset == 0, 60));
            }

            public Task<TrackPage> GetSortedTrackPageAsync(string playlistId, int sort, int offset, CancellationToken cancellationToken)
            {
                if (sort == 0)
                {
                    return GetTrackPageAsync(playlistId, offset, cancellationToken);
                }

                var tracks = Enumerable.Range(0, 60)
                    .Select(index => new TrackInfo($"spotify:track:{index}",
                        index == 0 ? new string('W', 1000) : $"Track {60 - index:00}",
                        "Artist", "3:00", null, index, 180000,
                        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index)))
                    .Order(TrackSorting.For(sort)).Skip(offset).Take(50).ToArray();
                return Task.FromResult(new TrackPage(tracks, offset + tracks.Length, offset + tracks.Length < 60, 60, 10800000));
            }

            public Task PlayAsync(string playlistUri, int? position, CancellationToken cancellationToken, string? trackUri = null, int sort = 0)
            {
                Played.Add((playlistUri, position));
                if (AutoConfirmPlayback)
                {
                    _playing = true;
                    PlayingChanged?.Invoke(this, true);
                    PublishProgress(new PlaybackProgress(0, 180000, true, trackUri, playlistUri));
                }
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
