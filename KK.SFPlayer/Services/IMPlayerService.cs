using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Models;

namespace KK.SFPlayer.Services
{
    internal interface IMPlayerService : IDisposable
    {
        bool IsConnected { get; }

        Task<bool> RestoreAsync(string clientId, CancellationToken cancellationToken);

        Task ConnectAsync(string clientId, CancellationToken cancellationToken);

        void Disconnect();

        Task<IReadOnlyList<PlaylistInfo>> GetPlaylistsAsync(CancellationToken cancellationToken);

        Task<PlaylistPage> GetPlaylistPageAsync(int offset, CancellationToken cancellationToken);

        Task<IReadOnlyList<TrackInfo>> GetTracksAsync(string playlistId, CancellationToken cancellationToken);

        Task<PlaylistPreview> GetTrackPreviewAsync(string playlistId, CancellationToken cancellationToken);

        Task PlayAsync(string playlistUri, int? position, CancellationToken cancellationToken);
    }
}
