using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KK.SFPlayer.Constants;
using KK.SFPlayer.Models;
using KK.SFPlayer.Models.Records;
using KK.SFPlayer.Services;

namespace KK.SFPlayer.ViewModels
{
    public partial class PlaylistCardViewModel : ObservableObject, IDisposable
    {
        private static readonly SemaphoreSlim PreviewSlots = new(2, 2);
        private readonly IMPlayerService _player;
        private readonly CancellationToken _lifetime;
        private CancellationTokenSource? _request;
        private bool _isVisible;
        private bool _isLoaded;
        private bool _disposed;
        private int _sort;
        private readonly Dictionary<int, TrackInfo> _knownTracks = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ImageUrl))]
        private PlaylistInfo _playlist;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
        private bool _isLoading;

        [ObservableProperty]
        private IReadOnlyList<TrackInfo> _tracks = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string _error = string.Empty;

        [ObservableProperty]
        private string _totalDuration = string.Empty;

        [ObservableProperty]
        private bool _isEmpty;

        internal PlaylistCardViewModel(PlaylistInfo playlist, IMPlayerService player, ICommand openSpotifyCommand, CancellationToken lifetime)
        {
            _playlist = playlist;
            _player = player;
            _lifetime = lifetime;
            OpenSpotifyCommand = openSpotifyCommand;
        }

        public string? ImageUrl => _isVisible ? Playlist.ImageUrl : null;

        public ICommand OpenSpotifyCommand { get; }

        public ICommand? OpenCommand { get; init; }

        public ICommand? PlayCommand { get; init; }

        public bool HasError => !string.IsNullOrEmpty(Error);

        public bool CanRetry => !IsLoading && !_disposed;

        internal Task LoadingTask { get; private set; } = Task.CompletedTask;

        public void Dispose()
        {
            _disposed = true;
            _request?.Cancel();
        }

        internal void SetSort(int sort)
        {
            if (_sort == sort)
            {
                return;
            }

            _sort = sort;
            InvalidateTracks();
        }

        internal void InvalidateTracks()
        {
            _knownTracks.Clear();
            _isLoaded = false;
            Tracks = [];
            TotalDuration = string.Empty;
            IsEmpty = false;
            _request?.Cancel();
            if (_isVisible && !IsLoading)
            {
                _ = EnsureLoadedAsync();
            }
        }

        internal void UseTracks(IEnumerable<TrackInfo> tracks, string? totalDuration = null, bool replace = false)
        {
            if (replace)
            {
                _knownTracks.Clear();
            }

            foreach (var track in tracks)
            {
                _knownTracks[track.Position] = track;
            }

            if (totalDuration is not null)
            {
                TotalDuration = totalDuration;
            }

            Error = string.Empty;
            _isLoaded = true;
            UpdatePreview();
        }

        internal void SetVisible(bool visible)
        {
            if (_isVisible == visible)
            {
                return;
            }

            _isVisible = visible;
            OnPropertyChanged(nameof(ImageUrl));
            if (visible)
            {
                _ = EnsureLoadedAsync();
            }
            else
            {
                _request?.Cancel();
            }
        }

        internal Task EnsureLoadedAsync()
        {
            if (_disposed || _isLoaded || IsLoading)
            {
                return LoadingTask;
            }

            return LoadingTask = LoadAsync();
        }

        private void UpdatePreview()
        {
            Tracks = _knownTracks.Values.Order(TrackSorting.For(_sort)).Take(PlaylistConstants.PreviewTrackLimit).ToArray();
            IsEmpty = _isLoaded && Tracks.Count == 0;
        }

        [RelayCommand(CanExecute = nameof(CanRetry))]
        private Task RetryAsync() => EnsureLoadedAsync();

        private async Task LoadAsync()
        {
            IsLoading = true;
            Error = string.Empty;
            IsEmpty = false;
            using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
            _request = request;
            var cancelled = false;
            try
            {
                await PreviewSlots.WaitAsync(request.Token);
                try
                {
                    if (_sort == 0)
                    {
                        var preview = await _player.GetTrackPreviewAsync(Playlist.Id, request.Token);
                        request.Token.ThrowIfCancellationRequested();
                        UseTracks(preview.Tracks, preview.TotalDuration);
                    }
                    else
                    {
                        var page = await _player.GetSortedTrackPageAsync(Playlist.Id, _sort, 0, request.Token);
                        request.Token.ThrowIfCancellationRequested();
                        UseTracks(page.Tracks);
                    }
                }
                finally
                {
                    PreviewSlots.Release();
                }
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
                cancelled = true;
            }
            catch (Exception exception)
            {
                if (!_disposed)
                {
                    Error = ErrorMessages.From(exception);
                }
            }
            finally
            {
                _request = null;
                IsLoading = false;
                if (cancelled && _isVisible && !_disposed && !_lifetime.IsCancellationRequested)
                {
                    _ = EnsureLoadedAsync();
                }
            }
        }
    }
}
