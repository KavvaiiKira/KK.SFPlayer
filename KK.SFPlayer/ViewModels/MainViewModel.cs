using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KK.SFPlayer.Constants;
using KK.SFPlayer.Models.Records;
using KK.SFPlayer.Services;

namespace KK.SFPlayer.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly IMPlayerService _player;

        private readonly SettingsStore _store;

        private readonly IStartupService _startup;

        private AppSettings _settings = new();

        private bool _disposed;
        private bool _settingsLoaded;

        [ObservableProperty]
        private double _windowWidth = WindowConstants.MinimumWidth;

        [ObservableProperty]
        private double _windowHeight = WindowConstants.MinimumHeight;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TopmostTooltip))]
        private bool _alwaysOnTop;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConfirmLogoutCommand))]
        private bool _isLogoutConfirmationOpen;

        [ObservableProperty]
        private bool _isSettingsOpen;

        internal MainViewModel(IMPlayerService player, SettingsStore store, IStartupService? startup = null)
        {
            _player = player;
            _store = store;
            _startup = startup ?? new StartupService();
            State = new PlayerUiState();
            Playback = new PlaybackViewModel(player, State, StartPlaybackAsync, CanStartPlayback);
            Library = new LibraryViewModel(
                player,
                State,
                Playback,
                OpenPlaylistAsync,
                ClosePlaylist,
                GetPlaylistSort,
                SavePlaylistSort);
            Playlist = new PlaylistViewModel(player, State, Library, Playback);
            Settings = new SettingsViewModel(SaveSettings, CloseSettings);
            State.PropertyChanged += OnStateChanged;
            State.AuthorizationLost += OnAuthorizationLost;
            Playlist.PropertyChanged += OnPlaylistChanged;
            _player.CacheWarning += OnCacheWarning;
        }

        public SettingsViewModel Settings { get; }

        public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);

        public bool NeedsLogin => HasClientId && !State.IsConnected && !State.IsBusy;

        public bool CanConfirmLogout => State.IsConnected && !State.IsBusy && IsLogoutConfirmationOpen;

        public string TopmostTooltip => AlwaysOnTop ? WindowConstants.TopmostOn : WindowConstants.TopmostOff;

        public string AccountTooltip => State.IsConnected ? AuthenticationConstants.SignOut : AuthenticationConstants.SignIn;

        public PlayerUiState State { get; }

        public PlaybackViewModel Playback { get; }

        public LibraryViewModel Library { get; }

        public PlaylistViewModel Playlist { get; }

        public bool ShowPlaylists => State.IsConnected && !Playlist.HasSelectedPlaylist;

        private string ClientId => _settings.ClientId?.Trim() ?? string.Empty;

        private bool CanEdit => State.CanEdit;

        private bool CanUsePlayer => State.CanUsePlayer;

        public async Task InitializeAsync()
        {
            try
            {
                _settings = _store.Load();
                AlwaysOnTop = _settings.AlwaysOnTop;
                WindowWidth = double.IsFinite(_settings.WindowWidth) ?
                    Math.Clamp(_settings.WindowWidth, WindowConstants.MinimumWidth, WindowConstants.MaximumWidth) : WindowConstants.MinimumWidth;
                WindowHeight = double.IsFinite(_settings.WindowHeight) ?
                    Math.Clamp(_settings.WindowHeight, WindowConstants.MinimumHeight, WindowConstants.MaximumHeight) : WindowConstants.MinimumHeight;
                Playback.Volume = double.IsFinite(_settings.Volume) ? Math.Clamp(_settings.Volume, 0, 100) : StreamingConstants.DefaultVolume * 100;
                _settingsLoaded = true;
                OnPropertyChanged(nameof(HasClientId));
                OnPropertyChanged(nameof(NeedsLogin));
            }
            catch (Exception exception)
            {
                State.Status = ErrorMessages.From(exception);
                return;
            }

            if (string.IsNullOrWhiteSpace(ClientId))
            {
                return;
            }
            await State.RunAsync(async token =>
            {
                State.IsConnected = await _player.RestoreAsync(ClientId, token);
                if (State.IsConnected)
                {
                    if (_player.GetCachedPlaylists() is { } cached)
                    {
                        Library.AppendPage(cached);
                    }
                    await Library.LoadAsync(token);
                }
            }, true);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            SaveSessionPreferences();
            _disposed = true;
            State.PropertyChanged -= OnStateChanged;
            State.AuthorizationLost -= OnAuthorizationLost;
            Playlist.PropertyChanged -= OnPlaylistChanged;
            _player.CacheWarning -= OnCacheWarning;
            Playlist.Dispose();
            Library.Dispose();
            Playback.Dispose();
            State.Dispose();
            _ = DisposePlayerAsync();
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
                State.IsConnected = false;
                Playlist.Close();
                Library.Clear();
            }

        }

        [RelayCommand(CanExecute = nameof(CanEdit))]
        private async Task AccountAsync()
        {
            if (State.IsConnected)
            {
                IsLogoutConfirmationOpen = true;
                return;
            }

            if (!State.IsConnected && !SettingsViewModel.IsValidClientId(ClientId))
            {
                OpenSettings();
                State.Status = SettingsConstants.ClientIdRequired;
                Settings.Error = SettingsConstants.ClientIdRequired;
                return;
            }

            await State.RunAsync(async cancellationToken =>
            {
                State.Status = AuthenticationConstants.Connecting;
                await _player.ConnectAsync(ClientId, cancellationToken);
                State.IsConnected = true;
                State.Status = string.Empty;
                await Library.LoadAsync(cancellationToken);
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

            await State.RunAsync(_ =>
            {
                _player.Disconnect();
                State.IsConnected = false;
                IsLogoutConfirmationOpen = false;
                Playlist.Close();
                Library.Clear();
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
                State.Status = ErrorMessages.From(exception);
            }
        }

        private async Task DisposePlayerAsync()
        {
            await Task.WhenAll(State.Completion, Library.Completion, Playback.Completion).ConfigureAwait(false);
            _player.Dispose();
        }

        private void SaveSessionPreferences()
        {
            if (!_settingsLoaded)
            {
                return;
            }

            var next = _settings with
            {
                Volume = Playback.Volume,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
            };
            try
            {
                _store.Save(next);
                _settings = next;
            }
            catch (Exception exception)
            {
                State.Status = ErrorMessages.From(exception);
            }
        }

        private Task OpenPlaylistAsync(PlaylistInfo playlist) => Playlist.OpenAsync(playlist);

        private void ClosePlaylist() => Playlist.Close();

        private Task StartPlaybackAsync() => Playlist.HasSelectedPlaylist ?
            Playlist.PlayPlaylistCommand.ExecuteAsync(null) :
            Library.PlayCardCommand.ExecuteAsync(Library.Playlists.Count > 0 ? Library.Playlists[0] : null);

        private bool CanStartPlayback() => Library.Playlists.Count > 0;

        private int GetPlaylistSort(string playlistId) =>
            _settings.PlaylistSorts?.TryGetValue(playlistId, out var sort) == true && sort >= 0 && sort < Playlist.SortOptions.Count ? sort : 0;

        private void SavePlaylistSort(string playlistId, int sort)
        {
            var sorts = new Dictionary<string, int>(_settings.PlaylistSorts ?? []) { [playlistId] = sort };
            var next = _settings with { PlaylistSorts = sorts };
            try
            {
                _store.Save(next);
                _settings = next;
            }
            catch (Exception exception)
            {
                State.Status = ErrorMessages.From(exception);
            }
        }

        partial void OnIsSettingsOpenChanged(bool value) => State.IsBlocked = value || IsLogoutConfirmationOpen;

        partial void OnIsLogoutConfirmationOpenChanged(bool value) => State.IsBlocked = value || IsSettingsOpen;

        private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(nameof(NeedsLogin));
            OnPropertyChanged(nameof(AccountTooltip));
            OnPropertyChanged(nameof(ShowPlaylists));
            AccountCommand.NotifyCanExecuteChanged();
            OpenSettingsCommand.NotifyCanExecuteChanged();
            ConfirmLogoutCommand.NotifyCanExecuteChanged();
            RefreshCommand.NotifyCanExecuteChanged();
        }

        private void OnPlaylistChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlaylistViewModel.SelectedPlaylist))
            {
                OnPropertyChanged(nameof(ShowPlaylists));
            }
        }

        private void OnCacheWarning(object? sender, string message) => State.Status = message;

        private void OnAuthorizationLost()
        {
            try
            {
                _player.Disconnect();
            }
            catch
            {
                // Keep the original authorization error visible if token cleanup fails.
            }
            State.IsConnected = false;
            Playlist.Close();
            Library.Clear();
        }

        [RelayCommand(CanExecute = nameof(CanUsePlayer))]
        private Task RefreshAsync() => Playlist.HasSelectedPlaylist ?
            Playlist.RefreshAsync() : State.RunAsync(Library.LoadAsync, true);
    }
}
