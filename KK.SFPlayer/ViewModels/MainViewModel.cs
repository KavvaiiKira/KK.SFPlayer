using KK.SFPlayer.Constants;
using System;
using System.Collections.ObjectModel;
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

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        [NotifyCanExecuteChangedFor(nameof(AccountCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(BackCommand))]
        [NotifyCanExecuteChangedFor(nameof(ConfirmLogoutCommand))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private bool _isBusy;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenPlaylistCommand))]
        [NotifyPropertyChangedFor(nameof(AccountTooltip))]
        [NotifyCanExecuteChangedFor(nameof(ConfirmLogoutCommand))]
        private bool _isConnected;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AccountCommand))]
        [NotifyCanExecuteChangedFor(nameof(ConfirmLogoutCommand))]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        [NotifyCanExecuteChangedFor(nameof(BackCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private bool _isLogoutConfirmationOpen;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AccountCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand))]
        [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
        [NotifyCanExecuteChangedFor(nameof(OpenPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayPlaylistCommand))]
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        [NotifyCanExecuteChangedFor(nameof(BackCommand))]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private bool _isSettingsOpen;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
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
        [NotifyCanExecuteChangedFor(nameof(PlayTrackCommand))]
        private PlaylistInfo? _selectedPlaylist;

        internal MainViewModel(IMPlayerService player, SettingsStore store, IStartupService? startup = null)
        {
            _player = player;
            _store = store;
            _startup = startup ?? new StartupService();
            Settings = new SettingsViewModel(SaveSettings, CloseSettings);
        }

        public ObservableCollection<PlaylistInfo> Playlists { get; } = [];

        public ObservableCollection<TrackInfo> Tracks { get; } = [];

        public SettingsViewModel Settings { get; }

        public bool HasStatus => !string.IsNullOrEmpty(Status);

        public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);

        public bool HasSelectedPlaylist => SelectedPlaylist is not null;

        public bool ShowPlaylists => IsConnected && !HasSelectedPlaylist;

        public bool CanUsePlayer => IsConnected && CanEdit;

        public bool CanPlay => CanUsePlayer && HasSelectedPlaylist;

        public bool CanEdit => !IsBusy && !IsLogoutConfirmationOpen && !IsSettingsOpen;

        public bool CanConfirmLogout => IsConnected && !IsBusy && IsLogoutConfirmationOpen;

        public bool CanRetryRequest => HasRetry && CanEdit;

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
            if (clientChanged)
            {
                _player.Disconnect();
                IsConnected = false;
                Back();
                Playlists.Clear();
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
                Playlists.Clear();
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
        private Task RefreshAsync() => RunAsync(LoadPlaylistsAsync, true);

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
            await RunAsync(async cancellationToken =>
            {
                foreach (var track in await _player.GetTracksAsync(playlist.Id, cancellationToken))
                {
                    Tracks.Add(track);
                }

                if (Tracks.Count == 0)
                {
                    Status = PlaybackConstants.NoTracks;
                }
            }, true);
        }

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private void Back()
        {
            _retryAction = null;
            HasRetry = false;
            SelectedPlaylist = null;
            Tracks.Clear();
            Status = string.Empty;
        }

        [RelayCommand(CanExecute = nameof(CanPlay))]
        private Task PlayPlaylistAsync() => PlayAsync(null);

        [RelayCommand(CanExecute = nameof(CanPlay))]
        private Task PlayTrackAsync(TrackInfo? track) => PlayAsync(track?.Position);

        public void Dispose()
        {
            _disposed = true;
            _lifetime.Cancel();
            _operation?.Cancel();
            if (_operation is null)
            {
                _player.Dispose();
            }

            _lifetime.Dispose();
        }

        partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(ShowPlaylists));

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
                Status = PlaybackConstants.Playing;
            });
        }

        private async Task LoadPlaylistsAsync(CancellationToken cancellationToken)
        {
            var playlists = await _player.GetPlaylistsAsync(cancellationToken);
            Back();
            Playlists.Clear();
            foreach (var playlist in playlists)
            {
                Playlists.Add(playlist);
            }

            Status = Playlists.Count == 0 ? PlaybackConstants.NoPlaylists : string.Empty;
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
                    Playlists.Clear();
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
                    _player.Dispose();
                }
            }
        }
    }
}
