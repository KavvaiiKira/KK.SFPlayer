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
using KK.SFPlayer.Models.Records;
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
        private bool _restoringSort;
        internal Task SortLoadingTask { get; private set; } = Task.CompletedTask;
        private readonly HashSet<string> _playlistIds = [];
        private readonly HashSet<string> _pendingPlaylistUpdates = [];
        private readonly List<Task> _retiredPreviewLoads = [];
        private PlaybackProgress _progress = new(0, 0, false, null);
        private string? _seekTrackUri;
        private readonly SemaphoreSlim _modeGate = new(1, 1);
        private PlaybackModes _appliedModes = new(false, 0);
        private PlaybackModes? _pendingModes;
        private DateTime _modeConfirmationDeadline;
        private int _modeRevision;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ElapsedTime))]
        [NotifyPropertyChangedFor(nameof(RemainingTime))]
        private double _positionMs;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SeekMaximum))]
        [NotifyPropertyChangedFor(nameof(RemainingTime))]
        private int _durationMs;

        [ObservableProperty]
        private bool _canSeek;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNowPlaying))]
        private string _nowPlaying = string.Empty;

        public bool HasNowPlaying => !HasStatus && !string.IsNullOrWhiteSpace(NowPlaying);

        public bool IsSeeking { get; private set; }

        public string? CurrentTrackUri => _progress.TrackUri;

        public string? CurrentPlaylistUri => _progress.ContextUri;

        public double SeekMaximum => Math.Max(1, DurationMs);

        public string ElapsedTime => FormatTrackTime(PositionMs);

        public string RemainingTime => string.Format(StreamingConstants.RemainingTimeFormat, FormatTrackTime(DurationMs - PositionMs));

        private static string FormatTrackTime(double milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        }

        private void OnProgressChanged(object? sender, PlaybackProgress progress)
        {
            if (IsSeeking && _seekTrackUri != progress.TrackUri)
            {
                IsSeeking = false;
            }

            var trackChanged = _progress.TrackUri != progress.TrackUri;
            var playlistChanged = _progress.ContextUri != progress.ContextUri;
            _progress = progress;
            NowPlaying = string.IsNullOrWhiteSpace(progress.TrackName) ? string.Empty :
                string.IsNullOrWhiteSpace(progress.Artists) ? progress.TrackName : $"{progress.TrackName} — {progress.Artists}";
            if (trackChanged)
            {
                OnPropertyChanged(nameof(CurrentTrackUri));
            }

            if (playlistChanged)
            {
                OnPropertyChanged(nameof(CurrentPlaylistUri));
            }
            DurationMs = Math.Max(0, progress.DurationMs);
            CanSeek = progress.CanSeek && DurationMs > 0;
            if (!IsSeeking)
            {
                PositionMs = Math.Clamp(progress.PositionMs, 0, DurationMs);
            }
        }

        public void BeginSeek()
        {
            if (CanSeek)
            {
                IsSeeking = true;
                _seekTrackUri = _progress.TrackUri;
            }
        }

        public void CommitSeek()
        {
            if (!IsSeeking)
            {
                return;
            }

            IsSeeking = false;
            if (CanSeek && _seekTrackUri == _progress.TrackUri)
            {
                var position = (int)Math.Clamp(PositionMs, 0, DurationMs);
                SendTransport(() => _player.Seek(position));
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlaybackTooltip))]
        private bool _isPlaying;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
        [NotifyPropertyChangedFor(nameof(PlaybackTooltip))]
        private bool _hasPlayback;

        [ObservableProperty]
        private double _volume = StreamingConstants.DefaultVolume * 100;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShuffleTooltip))]
        private bool _shuffleEnabled;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RepeatTooltip))]
        [NotifyPropertyChangedFor(nameof(IsRepeatEnabled))]
        [NotifyPropertyChangedFor(nameof(IsRepeatOne))]
        private int _repeatMode;

        [ObservableProperty]
        private bool _isChangingMode;

        public string ShuffleTooltip => ShuffleEnabled ? StreamingConstants.ShuffleOn : StreamingConstants.ShuffleOff;

        public string RepeatTooltip => RepeatMode switch
        {
            1 => StreamingConstants.RepeatAll,
            2 => StreamingConstants.RepeatOne,
            _ => StreamingConstants.RepeatOff,
        };

        public bool IsRepeatEnabled => RepeatMode > 0;

        public bool IsRepeatOne => RepeatMode == 2;

        public bool CanUseTransport => IsConnected && HasPlayback;

        public bool CanChangeModes => CanUseTransport;

        partial void OnHasPlaybackChanged(bool value) => UpdateTransportCommands();

        private void UpdateTransportCommands()
        {
            PreviousTrackCommand.NotifyCanExecuteChanged();
            NextTrackCommand.NotifyCanExecuteChanged();
            ToggleShuffleCommand.NotifyCanExecuteChanged();
            CycleRepeatCommand.NotifyCanExecuteChanged();
        }

        private void OnModesChanged(object? sender, PlaybackModes modes)
        {
            if (_pendingModes is { } pending && (IsChangingMode || DateTime.UtcNow < _modeConfirmationDeadline))
            {
                if (modes == pending && !IsChangingMode)
                {
                    _appliedModes = modes;
                    _pendingModes = null;
                }

                return;
            }

            _pendingModes = null;
            _appliedModes = modes;
            ShuffleEnabled = modes.Shuffle;
            RepeatMode = modes.RepeatMode;
        }

        [RelayCommand(CanExecute = nameof(CanUseTransport))]
        private Task PreviousTrackAsync() => SendTransportAsync(_player.PreviousTrackAsync);

        [RelayCommand(CanExecute = nameof(CanUseTransport))]
        private Task NextTrackAsync() => SendTransportAsync(_player.NextTrackAsync);

        private async Task SendTransportAsync(Func<CancellationToken, Task> action)
        {
            try
            {
                await action(_lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Status = ErrorMessages.From(exception);
            }
        }

        private void SendTransport(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                Status = ErrorMessages.From(exception);
            }
        }

        [RelayCommand(CanExecute = nameof(CanChangeModes), AllowConcurrentExecutions = true)]
        private Task ToggleShuffleAsync()
        {
            var enabled = !ShuffleEnabled;
            return ChangeModeAsync(new PlaybackModes(enabled, enabled ? 0 : RepeatMode));
        }

        [RelayCommand(CanExecute = nameof(CanChangeModes), AllowConcurrentExecutions = true)]
        private Task CycleRepeatAsync()
        {
            var mode = (RepeatMode + 1) % 3;
            return ChangeModeAsync(new PlaybackModes(mode == 0 && ShuffleEnabled, mode));
        }

        private async Task ChangeModeAsync(PlaybackModes requested)
        {
            var revision = ++_modeRevision;
            var cancellationToken = _lifetime.Token;
            _pendingModes = requested;
            _modeConfirmationDeadline = DateTime.MaxValue;
            ShuffleEnabled = requested.Shuffle;
            RepeatMode = requested.RepeatMode;
            var entered = false;
            try
            {
                await _modeGate.WaitAsync(cancellationToken);
                entered = true;
                IsChangingMode = true;
                if (requested.Shuffle)
                {
                    if (_appliedModes.RepeatMode != 0)
                    {
                        await _player.SetRepeatAsync(0, cancellationToken);
                        _appliedModes = _appliedModes with { RepeatMode = 0 };
                    }

                    await _player.SetShuffleAsync(true, cancellationToken);
                }
                else
                {
                    if (_appliedModes.Shuffle)
                    {
                        await _player.SetShuffleAsync(false, cancellationToken);
                        _appliedModes = _appliedModes with { Shuffle = false };
                    }

                    await _player.SetRepeatAsync(requested.RepeatMode, cancellationToken);
                }

                _appliedModes = requested;
                if (revision == _modeRevision)
                {
                    _modeConfirmationDeadline = DateTime.UtcNow.AddSeconds(StreamingConstants.ModeConfirmationTimeoutSeconds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (revision == _modeRevision)
                {
                    _pendingModes = null;
                    ShuffleEnabled = _appliedModes.Shuffle;
                    RepeatMode = _appliedModes.RepeatMode;
                }

                Status = ErrorMessages.From(exception);
            }
            finally
            {
                if (entered)
                {
                    IsChangingMode = false;
                    _modeGate.Release();
                }
            }
        }

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
                if (SelectedPlaylist is not null)
                {
                    await PlayPlaylistAsync();
                }
                else
                {
                    await PlayCardAsync(Playlists.FirstOrDefault());
                }

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
        [NotifyPropertyChangedFor(nameof(HasNowPlaying))]
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
            _player.ModesChanged += OnModesChanged;
            _player.ProgressChanged += OnProgressChanged;
            _player.PlaylistChanged += OnPlaylistChanged;
            _player.CacheWarning += OnCacheWarning;
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
            if (_restoringSort || SelectedPlaylist is not { } playlist)
            {
                return;
            }

            PlaylistCards.FirstOrDefault(card => card.Playlist.Id == playlist.Id)?.SetSort(value);
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

            ResetTrackPages();
            SortLoadingTask = RunAsync(cancellationToken => LoadTrackPageAsync(playlist.Id, cancellationToken), true);
        }

        private int GetPlaylistSort(string playlistId) =>
            _settings.PlaylistSorts?.TryGetValue(playlistId, out var sort) == true && sort >= 0 && sort < SortOptions.Count ? sort : 0;

        private void ResetTrackPages()
        {
            Tracks.Clear();
            SortedTracks.Clear();
            PlaylistSummary = string.Empty;
            HasMoreTracks = false;
            _nextTrackOffset = 0;
            _totalTrackCount = 0;
        }

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
                    if (_player.GetCachedPlaylists() is { } cached)
                    {
                        AppendPlaylistPage(cached);
                    }

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
        private Task RefreshAsync()
        {
            return SelectedPlaylist is { } playlist ? RunAsync(async cancellationToken =>
            {
                await _player.CheckPlaylistVersionAsync(playlist.Id, cancellationToken);
                await _player.EnsurePlaylistFreshAsync(playlist.Id, cancellationToken);
                _pendingPlaylistUpdates.Remove(playlist.Id);
                ResetTrackPages();
                await LoadTrackPageAsync(playlist.Id, cancellationToken);
            }, true) : RunAsync(LoadPlaylistsAsync, true);
        }

        private void OnCacheWarning(object? sender, string message) => Status = message;

        private void OnPlaylistChanged(object? sender, string playlistId)
        {
            if (_disposed)
            {
                return;
            }

            PlaylistCards.FirstOrDefault(card => card.Playlist.Id == playlistId)?.InvalidateTracks();
            if (SelectedPlaylist?.Id == playlistId)
            {
                _pendingPlaylistUpdates.Add(playlistId);
                ApplyPendingPlaylistUpdates();
            }
        }

        private void ApplyPendingPlaylistUpdates()
        {
            if (!CanUsePlayer || SelectedPlaylist is not { } playlist || !_pendingPlaylistUpdates.Remove(playlist.Id))
            {
                return;
            }

            ResetTrackPages();
            SortLoadingTask = RunAsync(cancellationToken => LoadTrackPageAsync(playlist.Id, cancellationToken), true);
        }

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
            ResetTrackPages();
            _restoringSort = true;
            try
            {
                SelectedSort = GetPlaylistSort(playlist.Id);
            }
            finally
            {
                _restoringSort = false;
            }
            await RunAsync(cancellationToken => LoadTrackPageAsync(playlist.Id, cancellationToken), true);
        }

        [RelayCommand(CanExecute = nameof(CanLoadMoreTracks))]
        private Task LoadMoreTracksAsync() => SelectedPlaylist is { } playlist ?
            RunAsync(cancellationToken => LoadTrackPageAsync(playlist.Id, cancellationToken), true) : Task.CompletedTask;

        private async Task LoadTrackPageAsync(string playlistId, CancellationToken cancellationToken)
        {
            if (SelectedSort != 0 && _nextTrackOffset == 0)
            {
                Status = PlaylistConstants.Sorting;
            }

            var page = await _player.GetSortedTrackPageAsync(playlistId, SelectedSort, _nextTrackOffset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var track in page.Tracks)
            {
                Tracks.Add(track);
                SortedTracks.Add(track);
            }

            _nextTrackOffset = page.NextOffset;
            _totalTrackCount = page.Total;
            HasMoreTracks = page.HasMore;
            PlaylistCards.FirstOrDefault(card => card.Playlist.Id == playlistId)?.UseTracks(Tracks, replace: true);
            PlaylistSummary = string.Format(PlaylistConstants.CountFormat, _totalTrackCount);
            if (page.TotalDurationMs.HasValue || (!HasMoreTracks && Tracks.Count == _totalTrackCount))
            {
                var duration = TimeSpan.FromMilliseconds(page.TotalDurationMs ?? Tracks.Sum(track => (long)track.DurationMs));
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
            else
            {
                Status = string.Empty;
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
        private Task PlayPlaylistAsync() => PlayAsync(null);

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

                await _player.EnsurePlaylistFreshAsync(playlist.Id, cancellationToken);
                var page = await _player.GetSortedTrackPageAsync(playlist.Id, GetPlaylistSort(playlist.Id), 0, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                card.UseTracks(page.Tracks, replace: true);
                if (page.Tracks.FirstOrDefault() is not { } first)
                {
                    throw new InvalidOperationException(PlaybackConstants.NoTracks);
                }

                await _player.PlayAsync(playlist.Uri, first.Position, cancellationToken, first.Uri, GetPlaylistSort(playlist.Id));
                HasPlayback = true;
                IsPlaying = true;
                Status = string.Empty;
            });

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _player.PlayingChanged -= OnPlayingChanged;
            _player.ModesChanged -= OnModesChanged;
            _player.ProgressChanged -= OnProgressChanged;
            _player.PlaylistChanged -= OnPlaylistChanged;
            _player.CacheWarning -= OnCacheWarning;
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
            UpdateTransportCommands();
            if (!value)
            {
                _pendingModes = null;
                _appliedModes = new PlaybackModes(false, 0);
                _modeRevision++;
                HasPlayback = false;
                IsPlaying = false;
                ShuffleEnabled = false;
                RepeatMode = 0;
                IsSeeking = false;
                OnProgressChanged(this, new PlaybackProgress(0, 0, false, null));
            }
        }

        private async Task PlayAsync(int? position)
        {
            var playlist = SelectedPlaylist;
            if (playlist is null)
            {
                return;
            }

            var selectedTrack = position.HasValue ? Tracks.FirstOrDefault(track => track.Position == position) : null;

            await RunAsync(async cancellationToken =>
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

                await _player.PlayAsync(playlist.Uri, track.Position, cancellationToken, track.Uri, SelectedSort);
                HasPlayback = true;
                IsPlaying = true;
                Status = string.Empty;
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
            var pending = _retiredPreviewLoads.Concat(PlaylistCards.Select(card => card.LoadingTask))
                .Concat(new[]
                {
                    ToggleShuffleCommand.ExecutionTask,
                    CycleRepeatCommand.ExecutionTask,
                    NextTrackCommand.ExecutionTask,
                    PreviousTrackCommand.ExecutionTask,
                }.OfType<Task>()).ToArray();
            _ = DisposePlayerAsync(pending);
        }

        private async Task DisposePlayerAsync(Task[] pending)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
            _modeGate.Dispose();
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
                if (!_disposed)
                {
                    ApplyPendingPlaylistUpdates();
                }
                if (_disposed)
                {
                    BeginDisposePlayer();
                }
            }
        }
    }
}
