using System;
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
    public partial class PlaybackViewModel : ObservableObject, IDisposable
    {
        private readonly IMPlayerService _player;

        private PlaybackProgress _progress = new(0, 0, false, null);

        private string? _seekTrackUri;
        private TaskCompletionSource? _playbackReady;
        private string? _requestedTrackUri;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLoading))]
        [NotifyPropertyChangedFor(nameof(PlaybackTooltip))]
        private bool _isPreparing;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLoading))]
        [NotifyPropertyChangedFor(nameof(PlaybackTooltip))]
        private bool _isStarting;

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

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PlaybackTooltip))]
        private bool _isPlaying;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(TogglePlaybackCommand))]
        [NotifyPropertyChangedFor(nameof(PlaybackTooltip))]
        [NotifyPropertyChangedFor(nameof(IsLoading))]
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

        private readonly Func<Task> _startPlayback;
        private readonly Func<bool> _canStartPlayback;

        internal PlaybackViewModel(
            IMPlayerService player,
            PlayerUiState state,
            Func<Task> startPlayback,
            Func<bool> canStartPlayback)
        {
            _player = player;
            State = state;
            _startPlayback = startPlayback;
            _canStartPlayback = canStartPlayback;
            _player.PreparationChanged += OnPreparationChanged;
            _player.PlayingChanged += OnPlayingChanged;
            _player.ModesChanged += OnModesChanged;
            _player.ProgressChanged += OnProgressChanged;
            State.PropertyChanged += OnStateChanged;
        }

        public bool HasNowPlaying => !State.HasStatus && !string.IsNullOrWhiteSpace(NowPlaying);

        public bool IsSeeking { get; private set; }

        public string? CurrentTrackUri => _progress.TrackUri;

        public string? CurrentPlaylistUri => _progress.ContextUri;

        public double SeekMaximum => Math.Max(1, DurationMs);

        public string ElapsedTime => FormatTrackTime(PositionMs);

        public string RemainingTime => string.Format(StreamingConstants.RemainingTimeFormat, FormatTrackTime(DurationMs - PositionMs));

        public string ShuffleTooltip => ShuffleEnabled ? StreamingConstants.ShuffleOn : StreamingConstants.ShuffleOff;

        public string RepeatTooltip => RepeatMode switch
        {
            1 => StreamingConstants.RepeatAll,
            2 => StreamingConstants.RepeatOne,
            _ => StreamingConstants.RepeatOff,
        };

        public bool IsRepeatEnabled => RepeatMode > 0;

        public bool IsRepeatOne => RepeatMode == 2;

        public bool IsLoading => IsPreparing || IsStarting || (State.IsConnected && State.IsBusy && !HasPlayback);

        public bool CanUseTransport => State.IsConnected && HasPlayback && !IsStarting;

        public bool CanChangeModes => CanUseTransport;

        public string PlaybackTooltip => IsLoading ? StreamingConstants.Loading :
            IsPlaying ? StreamingConstants.Pause :
            HasPlayback ? StreamingConstants.Resume : StreamingConstants.Play;

        public PlayerUiState State { get; }

        public bool CanTogglePlayback => State.IsConnected && !IsPreparing && !IsStarting &&
            (HasPlayback || (State.CanEdit && _canStartPlayback()));

        internal Task Completion => Task.WhenAll(new[]
        {
            ToggleShuffleCommand.ExecutionTask, CycleRepeatCommand.ExecutionTask,
            NextTrackCommand.ExecutionTask, PreviousTrackCommand.ExecutionTask, TogglePlaybackCommand.ExecutionTask,
        }.OfType<Task>());

        public bool IsCurrentTrack(string uri) => _progress.TrackUri == uri || _progress.OriginalTrackUri == uri;

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

        public async Task PlayAsync(string playlistUri, TrackInfo track, int sort, CancellationToken cancellationToken)
        {
            IsStarting = true;
            IsPlaying = false;
            _requestedTrackUri = track.Uri;
            OnProgressChanged(this, new PlaybackProgress(0, track.DurationMs, false, track.Uri, playlistUri, track.Name, track.Artists));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _playbackReady = ready;
            try
            {
                await _player.PlayAsync(playlistUri, track.Position, cancellationToken, track.Uri, sort);
                HasPlayback = true;
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(StreamingConstants.ConnectionTimeoutSeconds), cancellationToken);
                State.Status = string.Empty;
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(StreamingConstants.PlaybackFailed, exception);
            }
            finally
            {
                _playbackReady = null;
                _requestedTrackUri = null;
                IsStarting = false;
                if (!ready.Task.IsCompletedSuccessfully)
                {
                    HasPlayback = false;
                    IsPlaying = false;
                    OnProgressChanged(this, new PlaybackProgress(0, 0, false, null));
                }
            }
        }

        public void ReportError(string message)
        {
            State.Status = message;
            _playbackReady?.TrySetException(new InvalidOperationException(message));
        }

        public void RefreshCommands()
        {
            UpdateTransportCommands();
            TogglePlaybackCommand.NotifyCanExecuteChanged();
        }

        public void Dispose()
        {
            _playbackReady?.TrySetCanceled();
            _player.PreparationChanged -= OnPreparationChanged;
            _player.PlayingChanged -= OnPlayingChanged;
            _player.ModesChanged -= OnModesChanged;
            _player.ProgressChanged -= OnProgressChanged;
            State.PropertyChanged -= OnStateChanged;
            _ = DisposeGateAsync();
        }

        private static string FormatTrackTime(double milliseconds)
        {
            var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        }

        private void OnProgressChanged(object? sender, PlaybackProgress progress)
        {
            if (IsStarting && _requestedTrackUri is { } requested &&
                progress.TrackUri != requested && progress.OriginalTrackUri != requested)
            {
                return;
            }

            if (IsStarting && IsPlaying)
            {
                _playbackReady?.TrySetResult();
            }

            if (IsSeeking && _seekTrackUri != progress.TrackUri)
            {
                IsSeeking = false;
            }

            var trackChanged = _progress.TrackUri != progress.TrackUri || _progress.OriginalTrackUri != progress.OriginalTrackUri;
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

        partial void OnHasPlaybackChanged(bool value) => UpdateTransportCommands();

        partial void OnIsStartingChanged(bool value) => RefreshCommands();

        partial void OnIsPreparingChanged(bool value) => RefreshCommands();

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
                await action(State.CancellationToken);
            }
            catch (OperationCanceledException) when (State.CancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                State.Status = ErrorMessages.From(exception);
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
                State.Status = ErrorMessages.From(exception);
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
            var cancellationToken = State.CancellationToken;
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

                State.Status = ErrorMessages.From(exception);
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

        partial void OnVolumeChanged(double value)
        {
            try
            {
                _player.SetVolume(value / 100);
            }
            catch (Exception exception)
            {
                State.Status = ErrorMessages.From(exception);
            }
        }

        private void OnPreparationChanged(object? sender, bool preparing) => IsPreparing = preparing;

        private void OnPlayingChanged(object? sender, bool playing)
        {
            IsPlaying = playing;
            HasPlayback = true;
        }

        private async Task DisposeGateAsync()
        {
            await Completion.ConfigureAwait(false);
            _modeGate.Dispose();
        }

        private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
        {
            RefreshCommands();
            OnPropertyChanged(nameof(HasNowPlaying));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(PlaybackTooltip));
            if (e.PropertyName == nameof(PlayerUiState.IsConnected) && !State.IsConnected)
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

        [RelayCommand(CanExecute = nameof(CanTogglePlayback))]
        private async Task TogglePlaybackAsync()
        {
            if (!HasPlayback)
            {
                await _startPlayback();
                return;
            }

            SendTransport(_player.TogglePause);
        }
    }
}
