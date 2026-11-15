using System;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Models.Records;

namespace KK.SFPlayer.Services
{
    internal interface IMPlayerService : IDisposable
    {
        bool IsConnected { get; }

        event EventHandler<bool>? PlayingChanged;

        event EventHandler<PlaybackModes>? ModesChanged;

        event EventHandler<PlaybackProgress>? ProgressChanged;

        event EventHandler<string>? PlaylistChanged;

        event EventHandler<string>? CacheWarning;

        PlaylistPage? GetCachedPlaylists();

        Task EnsurePlaylistFreshAsync(string playlistId, CancellationToken cancellationToken);

        Task CheckPlaylistVersionAsync(string playlistId, CancellationToken cancellationToken);

        void Seek(int positionMs);

        Task NextTrackAsync(CancellationToken cancellationToken);

        Task PreviousTrackAsync(CancellationToken cancellationToken);

        Task SetShuffleAsync(bool enabled, CancellationToken cancellationToken);

        Task SetRepeatAsync(int mode, CancellationToken cancellationToken);

        void TogglePause();

        void SetVolume(double volume);

        Task<bool> RestoreAsync(string clientId, CancellationToken cancellationToken);

        Task ConnectAsync(string clientId, CancellationToken cancellationToken);

        void Disconnect();

        Task<PlaylistPage> GetPlaylistPageAsync(int offset, CancellationToken cancellationToken);

        Task<TrackPage> GetSortedTrackPageAsync(string playlistId, int sort, int offset, CancellationToken cancellationToken);

        Task<PlaylistPreview> GetTrackPreviewAsync(string playlistId, CancellationToken cancellationToken);

        Task PlayAsync(string playlistUri, int? position, CancellationToken cancellationToken, string? trackUri = null, int sort = 0);
    }
}
