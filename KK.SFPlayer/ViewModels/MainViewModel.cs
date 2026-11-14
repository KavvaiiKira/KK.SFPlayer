using KK.SFPlayer.Constants;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KK.SFPlayer.Models;
using KK.SFPlayer.Services;

namespace KK.SFPlayer.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly IMPlayerService _player;
        private readonly SettingsStore _store;
        private readonly IStartupService _startup;
        private readonly CancellationTokenSource _lifetime = new();
        private CancellationTokenSource? _operation;
        private Func<CancellationToken, Task>? _retryAction;
        private AppSettings _settings = new();
        private bool _disposed;
        private bool _disposePlayerScheduled;
        private int _nextPlaylistOffset;
        private int _nextTrackOffset;
        private int _totalTrackCount;
        private readonly HashSet<string> _playlistIds = [];
        private readonly List<Task> _retiredPreviewLoads = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlaybackTooltip))]
        private bool _isPlaying;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
        [NotifyPropertyChangedFor(nameof(PlaybackTooltip))]
        private bool _hasPlayback;

        [ObservableProperty]
        private double _volume = StreamingConstants.DefaultVolume * 100;

        public string PlaybackTooltip => IsPlaying ? StreamingConstants.Pause :
            HasPlayback ? StreamingConstants.Resume : StreamingConstants.Play;

        public bool CanTogglePlayback => IsConnected && (HasPlayback || (CanEdit && Playlists.Count > 0));

        partial void OnVolumeChanged(double value)
        {
            try
            {
                _player.SetVolume(value / 100);
            }
            catch (Exception exception)
            {
                Status = ErrorMessages.From(exception);
            }
        }

        private void OnPlayingChanged(object? sender, bool playing)
        {
            IsPlaying = playing;
            HasPlayback = true;
        }

        [RelayCommand(CanExecute = nameof(CanTogglePlayback))]
        private async Task TogglePlaybackAsync()
        {
            if (!HasPlayback)
            {
                await PlayCardAsync(Playlists.FirstOrDefault());
                return;
            }

            try
            {
                _player.TogglePause();
            }
            catch (Exception exception)
            {
                Status = ErrorMessages.From(exception);
            }
        }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayCardCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMoreTracksCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        [NotifyCanExecuteChangedFor(nameof(AccountCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(BackCommand))]
        [NotifyCanExecuteChangedFor(nameof(ConfirmLogoutCommand))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMorePlaylistsCommand))]
        [NotifyPropertyChangedFor(nameof(NeedsLogin))]
        [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
        private bool _isBusy;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayCardCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMoreTracksCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenPlaylistCommand))]
        [NotifyPropertyChangedFor(nameof(AccountTooltip))]
        [NotifyCanExecuteChangedFor(nameof(ConfirmLogoutCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMorePlaylistsCommand))]
        [NotifyPropertyChangedFor(nameof(NeedsLogin))]
        private bool _isConnected;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AccountCommand))]
        [NotifyCanExecuteChangedFor(nameof(ConfirmLogoutCommand))]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayCardCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMoreTracksCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        [NotifyCanExecuteChangedFor(nameof(BackCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMorePlaylistsCommand))]
        [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
        private bool _isLogoutConfirmationOpen;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AccountCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayCardCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMoreTracksCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        [NotifyCanExecuteChangedFor(nameof(BackCommand))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMorePlaylistsCommand))]
        [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
        private bool _isSettingsOpen;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LoadMorePlaylistsCommand))]
        private bool _hasMorePlaylists;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMoreTracksCommand))]
        private bool _hasRetry;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TopmostTooltip))]
        private bool _alwaysOnTop;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatus))]
        private string _status = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelectedPlaylist))]
        [NotifyPropertyChangedFor(nameof(ShowPlaylists))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayCardCommand))]
        [NotifyCanExecuteChangedFor(nameof(LoadMoreTracksCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        private PlaylistInfo? _selectedPlaylist;

        internal MainViewModel(IMPlayerService player, SettingsStore store, IStartupService? startup = null)
        {
            _player = player;
            _player.PlayingChanged += OnPlayingChanged;
            _store = store;
            _startup = startup ?? new StartupService();
            Settings = new SettingsViewModel(SaveSettings, CloseSettings);
        }

        public ObservableCollection<PlaylistInfo> Playlists { get; } = [];

        public ObservableCollection<PlaylistCardViewModel> PlaylistCards { get; } = [];

        public ObservableCollection<TrackInfo> Tracks { get; } = [];

        public ObservableCollection<TrackInfo> SortedTracks { get; } = [];

        public IReadOnlyList<string> SortOptions { get; } =
            [PlaylistConstants.OriginalOrder, PlaylistConstants.TitleOrder, PlaylistConstants.ArtistOrder,
                PlaylistConstants.DurationOrder, PlaylistConstants.NewestOrder, PlaylistConstants.OldestOrder];

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(LoadMoreTracksCommand))]
        private bool _hasMoreTracks;

        [ObservableProperty]
        private int _selectedSort;

        [ObservableProperty]
        private string _playlistSummary = string.Empty;

        partial void OnSelectedSortChanged(int value)
        {
            SortTracks();
            if (SelectedPlaylist is not { } playlist)
            {
                return;
            }

            PlaylistCards.FirstOrDefault(card => card.Playlist.Id == playlist.Id)?.SetSort(value);
            if (GetPlaylistSort(playlist.Id) == value)
            {
                return;
            }

            var sorts = new Dictionary<string, int>(_settings.PlaylistSorts ?? []) { [playlist.Id] = value };
            var next = _settings with { PlaylistSorts = sorts };
            try
            {
                _store.Save(next);
                _settings = next;
            }
            catch (Exception exception)
            {
                Status = ErrorMessages.From(exception);
            }
        }

        private int GetPlaylistSort(string playlistId) =>
            _settings.PlaylistSorts?.TryGetValue(playlistId, out var sort) == true && sort >= 0 && sort < SortOptions.Count ? sort : 0;

        private void SortTracks()
        {
            SortedTracks.Clear();
            foreach (var track in Tracks.Order(TrackSorting.For(SelectedSort)))
            {
                SortedTracks.Add(track);
            }
        }

        private int CompareTracks(TrackInfo left, TrackInfo right) => TrackSorting.Compare(SelectedSort, left, right);

        public SettingsViewModel Settings { get; }

        public bool HasStatus => !string.IsNullOrEmpty(Status);

        public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);

        public bool NeedsLogin => HasClientId && !IsConnected && !IsBusy;

        public bool HasSelectedPlaylist => SelectedPlaylist is not null;

        public bool ShowPlaylists => IsConnected && !HasSelectedPlaylist;

        public bool CanUsePlayer => IsConnected && CanEdit;

        public bool CanPlay => CanUsePlayer && HasSelectedPlaylist;

        public bool CanEdit => !IsBusy && !IsLogoutConfirmationOpen && !IsSettingsOpen;

        public bool CanConfirmLogout => IsConnected && !IsBusy && IsLogoutConfirmationOpen;

        public bool CanRetryRequest => HasRetry && CanEdit;

        public bool CanLoadMorePlaylists => HasMorePlaylists && CanUsePlayer;

        public bool CanLoadMoreTracks => HasMoreTracks && CanPlay && !HasRetry;

        public string TopmostTooltip => AlwaysOnTop ? WindowConstants.TopmostOn : WindowConstants.TopmostOff;

        public string AccountTooltip => IsConnected ? AuthenticationConstants.SignOut : AuthenticationConstants.SignIn;

        private string ClientId => _settings.ClientId?.Trim() ?? string.Empty;

        public async Task InitializeAsync()
        {
            try
            {
                _settings = _store.Load();
                AlwaysOnTop = _settings.AlwaysOnTop;
                OnPropertyChanged(nameof(HasClientId));
                OnPropertyChanged(nameof(NeedsLogin));
            }
            catch (Exception exception)
            {
                Status = ErrorMessages.From(exception);
                return;
            }

            if (string.IsNullOrWhiteSpace(ClientId))
            {
                return;
            }

            await RunAsync(async cancellationToken =>
            {
                IsConnected = await _player.RestoreAsync(ClientId, cancellationToken);
                if (IsConnected)
                {
                    await LoadPlaylistsAsync(cancellationToken);
                }
            }, true);
        }

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private void OpenSettings()
        {
            Settings.Open(ClientId, _settings.StartWithWindows, _startup.IsSupported);
            try
            {
                Settings.StartWithWindows = _startup.IsEnabled;
            }
            catch
            {
                Settings.Error = SettingsConstants.StartupError;
            }

            IsSettingsOpen = true;
        }

        private void CloseSettings() => IsSettingsOpen = false;

        private void SaveSettings(string clientId, bool startWithWindows)
        {
            var previousStartup = _startup.IsEnabled;
            if (previousStartup != startWithWindows)
            {
                try
                {
                    _startup.SetEnabled(startWithWindows);
                }
                catch
                {
                    throw new InvalidOperationException(SettingsConstants.StartupError);
                }
            }

            var next = _settings with { ClientId = clientId, StartWithWindows = startWithWindows };
            try
            {
                _store.Save(next);
            }
            catch
            {
                if (previousStartup != startWithWindows)
                {
                    _startup.SetEnabled(previousStartup);
                }

                throw;
            }

            var clientChanged = ClientId != clientId;
            _settings = next;
            OnPropertyChanged(nameof(HasClientId));
            OnPropertyChanged(nameof(NeedsLogin));
            if (clientChanged)
            {
                _player.Disconnect();
                IsConnected = false;
                Back();
                ClearLibrary();
            }

        }

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private async Task AccountAsync()
        {
            if (IsConnected)
            {
                IsLogoutConfirmationOpen = true;
                return;
            }

            if (!IsConnected && !SettingsViewModel.IsValidClientId(ClientId))
            {
                OpenSettings();
                Status = SettingsConstants.ClientIdRequired;
                Settings.Error = SettingsConstants.ClientIdRequired;
                return;
            }

            await RunAsync(async cancellationToken =>
            {
                Status = AuthenticationConstants.Connecting;
                await _player.ConnectAsync(ClientId, cancellationToken);
                IsConnected = true;
                Status = string.Empty;
                await LoadPlaylistsAsync(cancellationToken);
            });
        }

        [RelayCommand]
        private void CancelLogout() => IsLogoutConfirmationOpen = false;

        [RelayCommand(CanExecute = nameof(CanConfirmLogout))]
        private async Task ConfirmLogoutAsync()
        {
            if (!CanConfirmLogout)
            {
                return;
            }

            await RunAsync(_ =>
            {
                _player.Disconnect();
                IsConnected = false;
                IsLogoutConfirmationOpen = false;
                Back();
                ClearLibrary();
                return Task.CompletedTask;
            });
        }

        [RelayCommand]
        private void ToggleAlwaysOnTop()
        {
            var next = _settings with { AlwaysOnTop = !AlwaysOnTop };
            try
            {
                _store.Save(next);
                _settings = next;
                AlwaysOnTop = next.AlwaysOnTop;
            }
            catch (Exception exception)
            {
                Status = ErrorMessages.From(exception);
            }
        }

        [RelayCommand(CanExecute = nameof(CanUsePlayer))]
        private Task RefreshAsync() => SelectedPlaylist is { } playlist ?
            OpenPlaylistAsync(playlist) : RunAsync(LoadPlaylistsAsync, true);

        [RelayCommand(CanExecute = nameof(CanLoadMorePlaylists))]
        private Task LoadMorePlaylistsAsync() => RunAsync(async cancellationToken =>
        {
            var page = await _player.GetPlaylistPageAsync(_nextPlaylistOffset, cancellationToken);
            AppendPlaylistPage(page);
        }, true);

        [RelayCommand(CanExecute = nameof(CanRetryRequest))]
        private async Task RetryAsync()
        {
            var action = _retryAction;
            if (action is not null)
            {
                await RunAsync(action, true);
            }
        }

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
                Status = NetworkConstants.NetworkError;
            }
        }

        [RelayCommand(CanExecute = nameof(CanUsePlayer))]
        private async Task OpenPlaylistAsync(PlaylistInfo? playlist)
        {
            if (playlist is null)
            {
                return;
            }

            SelectedPlaylist = playlist;
            Tracks.Clear();
            SortedTracks.Clear();
            PlaylistSummary = string.Empty;
            SelectedSort = GetPlaylistSort(playlist.Id);
            HasMoreTracks = false;
            _nextTrackOffset = 0;
            _totalTrackCount = 0;
            await RunAsync(cancellationToken => LoadTrackPageAsync(playlist.Id, cancellationToken), true);
        }

        [RelayCommand(CanExecute = nameof(CanLoadMoreTracks))]
        private Task LoadMoreTracksAsync() => SelectedPlaylist is { } playlist ?
            RunAsync(cancellationToken => LoadTrackPageAsync(playlist.Id, cancellationToken), true) : Task.CompletedTask;

        private async Task LoadTrackPageAsync(string playlistId, CancellationToken cancellationToken)
        {
            var page = await _player.GetTrackPageAsync(playlistId, _nextTrackOffset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var track in page.Tracks)
            {
                Tracks.Add(track);
                var low = 0;
                var high = SortedTracks.Count;
                while (low < high)
                {
                    var middle = low + (high - low) / 2;
                    if (CompareTracks(SortedTracks[middle], track) <= 0)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }

                SortedTracks.Insert(low, track);
            }

            _nextTrackOffset = page.NextOffset;
            _totalTrackCount = page.Total;
            HasMoreTracks = page.HasMore;
            PlaylistCards.FirstOrDefault(card => card.Playlist.Id == playlistId)?.UseTracks(Tracks, replace: true);
            PlaylistSummary = string.Format(PlaylistConstants.CountFormat, _totalTrackCount);
            if (!HasMoreTracks && Tracks.Count == _totalTrackCount)
            {
                var duration = TimeSpan.FromMilliseconds(Tracks.Sum(track => (long)track.DurationMs));
                var time = duration.TotalHours >= 1 ?
                    $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" :
                    $"{duration.Minutes}:{duration.Seconds:00}";
                PlaylistSummary = string.Format(PlaylistConstants.SummaryFormat, _totalTrackCount, time);
                PlaylistCards.FirstOrDefault(card => card.Playlist.Id == playlistId)?.UseTracks(Tracks, time);
            }

            if (Tracks.Count == 0 && !HasMoreTracks)
            {
                Status = PlaybackConstants.NoTracks;
            }
        }

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private void Back()
        {
            _retryAction = null;
            HasRetry = false;
            SelectedPlaylist = null;
            Tracks.Clear();
            SortedTracks.Clear();
            PlaylistSummary = string.Empty;
            HasMoreTracks = false;
            _nextTrackOffset = 0;
            _totalTrackCount = 0;
            Status = string.Empty;
        }

        [RelayCommand(CanExecute = nameof(CanPlay))]
        private Task PlayPlaylistAsync() => SortedTracks.FirstOrDefault() is { } first ? PlayAsync(first.Position) : Task.CompletedTask;

        [RelayCommand(CanExecute = nameof(CanPlay))]
        private Task PlayTrackAsync(TrackInfo? track) => PlayAsync(track?.Position);

        [RelayCommand(CanExecute = nameof(CanUsePlayer))]
        private Task PlayCardAsync(PlaylistInfo? playlist) => playlist is null ? Task.CompletedTask :
            RunAsync(async cancellationToken =>
            {
                var card = PlaylistCards.FirstOrDefault(item => item.Playlist.Id == playlist.Id);
                if (card is null)
                {
                    return;
                }

                await card.EnsureLoadedAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (card.HasError)
                {
                    throw new InvalidOperationException(card.Error);
                }

                if (card.FirstTrack is not { } first)
                {
                    throw new InvalidOperationException(PlaybackConstants.NoTracks);
                }

                await _player.PlayAsync(playlist.Uri, first.Position, cancellationToken);
                HasPlayback = true;
                IsPlaying = true;
                Status = PlaybackConstants.Playing;
            });

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _player.PlayingChanged -= OnPlayingChanged;
            _lifetime.Cancel();
            _operation?.Cancel();
            foreach (var card in PlaylistCards)
            {
                card.Dispose();
            }

            if (_operation is null)
            {
                BeginDisposePlayer();
            }

            _lifetime.Dispose();
        }

        partial void OnIsConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(ShowPlaylists));
            TogglePlaybackCommand.NotifyCanExecuteChanged();
            if (!value)
            {
                HasPlayback = false;
                IsPlaying = false;
            }
        }

        private async Task PlayAsync(int? position)
        {
            var playlist = SelectedPlaylist;
            if (playlist is null)
            {
                return;
            }

            await RunAsync(async cancellationToken =>
            {
                await _player.PlayAsync(playlist.Uri, position, cancellationToken);
                HasPlayback = true;
                IsPlaying = true;
                Status = PlaybackConstants.Playing;
            });
        }

        private async Task LoadPlaylistsAsync(CancellationToken cancellationToken)
        {
            var page = await _player.GetPlaylistPageAsync(0, cancellationToken);
            Back();
            ClearLibrary();
            AppendPlaylistPage(page);

            Status = Playlists.Count == 0 ? PlaybackConstants.NoPlaylists : string.Empty;
        }

        private void AppendPlaylistPage(PlaylistPage page)
        {
            foreach (var playlist in page.Playlists)
            {
                if (_playlistIds.Add(playlist.Id))
                {
                    Playlists.Add(playlist);
                    var card = new PlaylistCardViewModel(playlist, _player, OpenSpotifyCommand, _lifetime.Token)
                    {
                        OpenCommand = OpenPlaylistCommand,
                        PlayCommand = PlayCardCommand,
                    };
                    card.SetSort(GetPlaylistSort(playlist.Id));
                    PlaylistCards.Add(card);
                }
            }

            _nextPlaylistOffset = page.NextOffset;
            HasMorePlaylists = page.HasMore;
            TogglePlaybackCommand.NotifyCanExecuteChanged();
        }

        private void ClearLibrary()
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
            TogglePlaybackCommand.NotifyCanExecuteChanged();
        }

        private void BeginDisposePlayer()
        {
            if (_disposePlayerScheduled)
            {
                return;
            }

            _disposePlayerScheduled = true;
            var pending = _retiredPreviewLoads.Concat(PlaylistCards.Select(card => card.LoadingTask)).ToArray();
            _ = DisposePlayerAsync(pending);
        }

        private async Task DisposePlayerAsync(Task[] pending)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
            _player.Dispose();
        }

        private async Task RunAsync(Func<CancellationToken, Task> action, bool retryable = false)
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _operation = operation;
            _retryAction = null;
            HasRetry = false;
            IsBusy = true;
            Status = string.Empty;
            try
            {
                await action(operation.Token);
            }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            {
                Status = string.Empty;
            }
            catch (Exception exception)
            {
                if (exception is SpotifyAPI.Web.APIException api &&
                    api.Response?.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
                {
                    try
                    {
                        _player.Disconnect();
                    }
                    catch
                    {
                        // Keep the original authorization error visible if token cleanup fails.
                    }

                    IsConnected = false;
                    Back();
                    ClearLibrary();
                }

                Status = ErrorMessages.From(exception);
                if (retryable && ErrorMessages.CanRetry(exception))
                {
                    _retryAction = action;
                    HasRetry = true;
                }
            }
            finally
            {
                IsBusy = false;
                _operation = null;
                if (_disposed)
                {
                    BeginDisposePlayer();
                }
            }
        }
    }
}
