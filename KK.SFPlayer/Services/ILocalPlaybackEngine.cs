using System;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Models.Records;

namespace KK.SFPlayer.Services
{
    internal interface ILocalPlaybackEngine : IDisposable
    {
        event EventHandler<bool>? PlayingChanged;

        event EventHandler<PlaybackModes>? ModesChanged;

        event EventHandler<PlaybackProgress>? ProgressChanged;

        void Seek(int positionMs);

        void NextTrack();

        void PreviousTrack();

        Task<string> GetDeviceAsync(Func<CancellationToken, Task<string>> getAccessToken, CancellationToken cancellationToken);

        void TogglePause();

        void SetVolume(double volume);

        void Reset();
    }
}
