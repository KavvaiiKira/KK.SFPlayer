using Avalonia;
using Avalonia.Controls;
using System.ComponentModel;
using System.Linq;
using Avalonia.VisualTree;
using KK.SFPlayer.ViewModels;
using KK.SFPlayer.Models.Records;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace KK.SFPlayer.Views
{
    public partial class PlaylistDetailView : UserControl
    {
        private PlaylistViewModel? _model;

        public PlaylistDetailView()
        {
            InitializeComponent();
            TracksList.AddHandler(ScrollViewer.ScrollChangedEvent, OnTracksScrolled);
            TracksList.AddHandler(KeyDownEvent, OnTracksKeyDown, RoutingStrategies.Tunnel);
            DataContextChanged += (_, _) => UpdateModel();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            LayoutUpdated += OnLayoutUpdated;
            UpdateModel();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            LayoutUpdated -= OnLayoutUpdated;
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelPropertyChanged;
                _model.Playback.PropertyChanged -= OnModelPropertyChanged;
                _model = null;
            }

            base.OnDetachedFromVisualTree(e);
        }

        private void OnTracksScrolled(object? sender, ScrollChangedEventArgs e)
        {
            if (e.OffsetDelta.Y > 0 && e.ExtentDelta.Y == 0 && e.Source is ScrollViewer scroll &&
                scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 100 &&
                _model?.LoadMoreTracksCommand.CanExecute(null) == true)
            {
                _model.LoadMoreTracksCommand.Execute(null);
            }
        }

        private void OnTrackTapped(object? sender, TappedEventArgs e)
        {
            if (sender is Control { DataContext: TrackInfo track } && _model?.PlayTrackCommand.CanExecute(track) == true)
            {
                _model.PlayTrackCommand.Execute(track);
                e.Handled = true;
            }
        }

        private void OnTracksKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && TracksList.SelectedItem is TrackInfo track &&
                _model?.PlayTrackCommand.CanExecute(track) == true)
            {
                _model.PlayTrackCommand.Execute(track);
                e.Handled = true;
            }
        }

        private void UpdateModel()
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelPropertyChanged;
                _model.Playback.PropertyChanged -= OnModelPropertyChanged;
            }

            _model = DataContext as PlaylistViewModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelPropertyChanged;
                _model.Playback.PropertyChanged += OnModelPropertyChanged;
            }

            UpdateCurrentTrack();
        }

        private void OnLayoutUpdated(object? sender, System.EventArgs e) => UpdateCurrentTrack();

        private void UpdateCurrentTrack()
        {
            var playlistMatches = _model?.SelectedPlaylist?.Uri is { } playlistUri && playlistUri == _model.Playback.CurrentPlaylistUri;
            foreach (var row in TracksList.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("track-row")))
            {
                var current = playlistMatches && row.DataContext is TrackInfo track && track.Uri == _model?.Playback.CurrentTrackUri;
                row.Classes.Set("current", current);
                row.Classes.Set("playing", current && _model?.Playback.IsPlaying == true);
            }
        }

        private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PlaybackViewModel.CurrentTrackUri) or nameof(PlaybackViewModel.CurrentPlaylistUri) or
                nameof(PlaylistViewModel.SelectedPlaylist) or nameof(PlaybackViewModel.IsPlaying))
            {
                UpdateCurrentTrack();
            }

            if (e.PropertyName is nameof(PlaylistViewModel.SelectedPlaylist) or nameof(PlaylistViewModel.SelectedSort))
            {
                var scroll = TracksList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (scroll is not null)
                {
                    scroll.Offset = default;
                }
            }
        }
    }
}
