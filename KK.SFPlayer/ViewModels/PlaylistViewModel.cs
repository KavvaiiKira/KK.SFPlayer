using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KK.SFPlayer.Constants;
using KK.SFPlayer.Models.Records;
using KK.SFPlayer.Services;

namespace KK.SFPlayer.ViewModels
{
    public partial class PlaylistViewModel : ObservableObject, IDisposable
    {
        private readonly IMPlayerService _player;

        private int _nextTrackOffset;

        private int _totalTrackCount;

        private bool _restoringSort;

        private readonly HashSet<string> _pendingPlaylistUpdates = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectedPlaylist))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMoreTracksCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        private PlaylistInfo? _selectedPlaylist;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LoadMoreTracksCommand))]
        private bool _hasMoreTracks;

        [ObservableProperty]
        private int _selectedSort;

        [ObservableProperty]
        private string _playlistSummary = string.Empty;

        private readonly LibraryViewModel _library;
        private bool _disposed;

        internal PlaylistViewModel(
            IMPlayerService player,
            PlayerUiState state,
            LibraryViewModel library,
            PlaybackViewModel playback)
        {
            _player = player;
            State = state;
            _library = library;
            Playback = playback;
            _player.PlaylistChanged += OnPlaylistChanged;
            State.PropertyChanged += OnStateChanged;
        }

        public ObservableCollection<TrackInfo> Tracks { get; } = [];

        public IReadOnlyList<string> SortOptions { get; } =
            [PlaylistConstants.OriginalOrder, PlaylistConstants.TitleOrder, PlaylistConstants.ArtistOrder,
                PlaylistConstants.DurationOrder, PlaylistConstants.NewestOrder, PlaylistConstants.OldestOrder];

        public bool HasSelectedPlaylist => SelectedPlaylist is not null;

        public PlayerUiState State { get; }

        public PlaybackViewModel Playback { get; }

        public bool CanPlay => State.CanUsePlayer && HasSelectedPlaylist;

        public bool CanEdit => State.CanEdit;

        public bool CanLoadMoreTracks => HasMoreTracks && CanPlay && !State.HasRetry;

        internal Task SortLoadingTask { get; private set; } = Task.CompletedTask;

        public async Task OpenAsync(PlaylistInfo playlist)
        {
            SelectedPlaylist = playlist;
            ResetTrackPages();
            _restoringSort = true;
            try
            {
                SelectedSort = _library.GetSort(playlist.Id);
            }
            finally
            {
                _restoringSort = false;
            }
            await State.RunAsync(token => LoadTrackPageAsync(playlist.Id, token), true);
        }

        public Task RefreshAsync() => SelectedPlaylist is { } playlist ? State.RunAsync(async token =>
        {
            await _player.CheckPlaylistVersionAsync(playlist.Id, token);
            await _player.EnsurePlaylistFreshAsync(playlist.Id, token);
            _pendingPlaylistUpdates.Remove(playlist.Id);
            ResetTrackPages();
            await LoadTrackPageAsync(playlist.Id, token);
        }, true) : Task.CompletedTask;

        public void Close()
        {
            State.ClearRetry();
            SelectedPlaylist = null;
            ResetTrackPages();
            State.Status = string.Empty;
        }

        public void Dispose()
        {
            _disposed = true;
            _player.PlaylistChanged -= OnPlaylistChanged;
            State.PropertyChanged -= OnStateChanged;
        }

        private void ResetTrackPages()
        {
            Tracks.Clear();
            PlaylistSummary = string.Empty;
            HasMoreTracks = false;
            _nextTrackOffset = 0;
            _totalTrackCount = 0;
        }

        [RelayCommand(CanExecute = nameof(CanLoadMoreTracks))]
        private Task LoadMoreTracksAsync() => SelectedPlaylist is { } playlist ?
            State.RunAsync(cancellationToken => LoadTrackPageAsync(playlist.Id, cancellationToken), true) : Task.CompletedTask;

        private async Task LoadTrackPageAsync(string playlistId, CancellationToken cancellationToken)
        {
            if (SelectedSort != 0 && _nextTrackOffset == 0)
            {
                State.Status = PlaylistConstants.Sorting;
            }

            var page = await _player.GetSortedTrackPageAsync(playlistId, SelectedSort, _nextTrackOffset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var track in page.Tracks)
            {
                Tracks.Add(track);
            }

            _nextTrackOffset = page.NextOffset;
            _totalTrackCount = page.Total;
            HasMoreTracks = page.HasMore;
            _library.UpdateTracks(playlistId, Tracks, replace: true);
            PlaylistSummary = string.Format(PlaylistConstants.CountFormat, _totalTrackCount);
            if (page.TotalDurationMs.HasValue || (!HasMoreTracks && Tracks.Count == _totalTrackCount))
            {
                var duration = TimeSpan.FromMilliseconds(page.TotalDurationMs ?? Tracks.Sum(track => (long)track.DurationMs));
                var time = duration.TotalHours >= 1 ?
                    $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" :
                    $"{duration.Minutes}:{duration.Seconds:00}";
                PlaylistSummary = string.Format(PlaylistConstants.SummaryFormat, _totalTrackCount, time);
                _library.UpdateTracks(playlistId, Tracks, time);
            }

            if (Tracks.Count == 0 && !HasMoreTracks)
            {
                State.Status = PlaybackConstants.NoTracks;
            }
            else
            {
                State.Status = string.Empty;
            }
        }

        [RelayCommand(CanExecute = nameof(CanPlay))]
        private Task PlayPlaylistAsync() => PlayAsync(null);

        [RelayCommand(CanExecute = nameof(CanPlay))]
        private Task PlayTrackAsync(TrackInfo? track) => PlayAsync(track?.Position);

        private async Task PlayAsync(int? position)
        {
            var playlist = SelectedPlaylist;
            if (playlist is null)
            {
                return;
            }

            var selectedTrack = position.HasValue ? Tracks.FirstOrDefault(track => track.Position == position) : null;

            await State.RunAsync(async cancellationToken =>
            {
                await _player.EnsurePlaylistFreshAsync(playlist.Id, cancellationToken);
                var track = selectedTrack;
                if (track is null)
                {
                    var page = await _player.GetSortedTrackPageAsync(playlist.Id, SelectedSort, 0, cancellationToken);
                    track = page.Tracks.FirstOrDefault();
                }

                if (track is null)
                {
                    throw new InvalidOperationException(PlaybackConstants.NoTracks);
                }

                await Playback.PlayAsync(playlist.Uri, track, SelectedSort, cancellationToken);
                State.Status = string.Empty;
            });
        }

        partial void OnSelectedSortChanged(int value)
        {
            if (_restoringSort || SelectedPlaylist is not { } playlist)
            {
                return;
            }
            _library.SetSort(playlist.Id, value);
            ResetTrackPages();
            SortLoadingTask = State.RunAsync(token => LoadTrackPageAsync(playlist.Id, token), true);
        }

        private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            PlayPlaylistCommand.NotifyCanExecuteChanged();
            PlayTrackCommand.NotifyCanExecuteChanged();
            LoadMoreTracksCommand.NotifyCanExecuteChanged();
            BackCommand.NotifyCanExecuteChanged();
            ApplyPendingPlaylistUpdates();
        }

        private void OnPlaylistChanged(object? sender, string playlistId)
        {
            if (!_disposed && SelectedPlaylist?.Id == playlistId)
            {
                _pendingPlaylistUpdates.Add(playlistId);
                ApplyPendingPlaylistUpdates();
            }
        }

        private void ApplyPendingPlaylistUpdates()
        {
            if (!State.CanUsePlayer || SelectedPlaylist is not { } playlist || !_pendingPlaylistUpdates.Remove(playlist.Id))
            {
                return;
            }
            ResetTrackPages();
            SortLoadingTask = State.RunAsync(token => LoadTrackPageAsync(playlist.Id, token), true);
        }

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private void Back() => Close();
    }
}
