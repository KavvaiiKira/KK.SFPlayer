using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    public partial class LibraryViewModel : ObservableObject, IDisposable
    {
        private readonly IMPlayerService _player;

        private int _nextPlaylistOffset;

        private readonly HashSet<string> _playlistIds = [];

        private readonly List<Task> _retiredPreviewLoads = [];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LoadMorePlaylistsCommand))]
        private bool _hasMorePlaylists;

        private readonly Func<PlaylistInfo, Task> _openPlaylist;
        private readonly Action _closePlaylist;
        private readonly Func<string, int> _getSort;
        private readonly Action<string, int> _saveSort;
        private readonly PlaybackViewModel _playback;
        private bool _disposed;

        internal LibraryViewModel(
            IMPlayerService player,
            PlayerUiState state,
            PlaybackViewModel playback,
            Func<PlaylistInfo, Task> openPlaylist,
            Action closePlaylist,
            Func<string, int> getSort,
            Action<string, int> saveSort)
        {
            _player = player;
            State = state;
            _playback = playback;
            _openPlaylist = openPlaylist;
            _closePlaylist = closePlaylist;
            _getSort = getSort;
            _saveSort = saveSort;
            _player.PlaylistChanged += OnPlaylistChanged;
            State.PropertyChanged += OnStateChanged;
            Playlists.CollectionChanged += (_, _) => _playback.RefreshCommands();
        }

        public ObservableCollection<PlaylistInfo> Playlists { get; } = [];

        public ObservableCollection<PlaylistCardViewModel> PlaylistCards { get; } = [];

        public PlayerUiState State { get; }

        public bool CanUsePlayer => State.CanUsePlayer;

        public bool CanLoadMorePlaylists => HasMorePlaylists && CanUsePlayer;

        internal Task Completion => Task.WhenAll(_retiredPreviewLoads.Concat(PlaylistCards.Select(card => card.LoadingTask))
            .Concat(new[] { PlayCardCommand.ExecutionTask, LoadMorePlaylistsCommand.ExecutionTask, OpenPlaylistCommand.ExecutionTask }.OfType<Task>()));

        public int GetSort(string playlistId) => _getSort(playlistId);

        public void SetSort(string playlistId, int sort)
        {
            PlaylistCards.FirstOrDefault(card => card.Playlist.Id == playlistId)?.SetSort(sort);
            _saveSort(playlistId, sort);
        }

        public void UpdateTracks(string playlistId, IEnumerable<TrackInfo> tracks, string? duration = null, bool replace = false) =>
            PlaylistCards.FirstOrDefault(card => card.Playlist.Id == playlistId)?.UseTracks(tracks, duration, replace);

        public async Task LoadAsync(CancellationToken cancellationToken)
        {
            var page = await _player.GetPlaylistPageAsync(0, cancellationToken);
            _closePlaylist();
            Clear();
            AppendPage(page);
            State.Status = Playlists.Count == 0 ? PlaybackConstants.NoPlaylists : string.Empty;
        }

        public void AppendPage(PlaylistPage page)
        {
            foreach (var playlist in page.Playlists)
            {
                if (_playlistIds.Add(playlist.Id))
                {
                    Playlists.Add(playlist);
                    var card = new PlaylistCardViewModel(playlist, _player, OpenSpotifyCommand, State.CancellationToken)
                    {
                        OpenCommand = OpenPlaylistCommand,
                        PlayCommand = PlayCardCommand,
                    };
                    card.SetSort(GetSort(playlist.Id));
                    PlaylistCards.Add(card);
                }
            }

            _nextPlaylistOffset = page.NextOffset;
            HasMorePlaylists = page.HasMore;
        }

        public void Clear()
        {
            _retiredPreviewLoads.RemoveAll(task => task.IsCompleted);
            foreach (var card in PlaylistCards)
            {
                card.Dispose();
                _retiredPreviewLoads.Add(card.LoadingTask);
            }

            PlaylistCards.Clear();
            Playlists.Clear();
            _playlistIds.Clear();
            HasMorePlaylists = false;
            _nextPlaylistOffset = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _player.PlaylistChanged -= OnPlaylistChanged;
            State.PropertyChanged -= OnStateChanged;
            foreach (var card in PlaylistCards)
            {
                card.Dispose();
            }
        }

        [RelayCommand(CanExecute = nameof(CanLoadMorePlaylists))]
        private Task LoadMorePlaylistsAsync() => State.RunAsync(async cancellationToken =>
        {
            var page = await _player.GetPlaylistPageAsync(_nextPlaylistOffset, cancellationToken);
            AppendPage(page);
        }, true);

        [RelayCommand(CanExecute = nameof(CanUsePlayer))]
        private Task PlayCardAsync(PlaylistInfo? playlist) => playlist is null ? Task.CompletedTask :
            State.RunAsync(async cancellationToken =>
            {
                var card = PlaylistCards.FirstOrDefault(item => item.Playlist.Id == playlist.Id);
                if (card is null)
                {
                    return;
                }

                await _player.EnsurePlaylistFreshAsync(playlist.Id, cancellationToken);
                if (card.Tracks.Count == 0)
                {
                    var page = await _player.GetSortedTrackPageAsync(playlist.Id, GetSort(playlist.Id), 0, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    card.UseTracks(page.Tracks, replace: true);
                }

                if (card.Tracks.FirstOrDefault() is not { } first)
                {
                    throw new InvalidOperationException(PlaybackConstants.NoTracks);
                }

                await _playback.PlayAsync(playlist.Uri, first, GetSort(playlist.Id), cancellationToken);
                State.Status = string.Empty;
            });

        [RelayCommand]
        private void OpenSpotify(string? uri)
        {
            if (uri is null || !uri.StartsWith("spotify:", StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                var parts = uri.Split(':');
                if (parts.Length == 3 && (parts[1] is "playlist" or "track"))
                {
                    Process.Start(new ProcessStartInfo($"https://open.spotify.com/{parts[1]}/{Uri.EscapeDataString(parts[2])}")
                    {
                        UseShellExecute = true,
                    });
                }
            }
            catch
            {
                State.Status = NetworkConstants.NetworkError;
            }
        }

        private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            LoadMorePlaylistsCommand.NotifyCanExecuteChanged();
            PlayCardCommand.NotifyCanExecuteChanged();
            OpenPlaylistCommand.NotifyCanExecuteChanged();
        }

        private void OnPlaylistChanged(object? sender, string playlistId) =>
            PlaylistCards.FirstOrDefault(card => card.Playlist.Id == playlistId)?.InvalidateTracks();

        [RelayCommand(CanExecute = nameof(CanUsePlayer))]
        private Task OpenPlaylistAsync(PlaylistInfo? playlist) => playlist is null ? Task.CompletedTask : _openPlaylist(playlist);
    }
}
