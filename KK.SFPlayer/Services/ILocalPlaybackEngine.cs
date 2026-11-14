using System;
using System.Threading;
using System.Threading.Tasks;

namespace KK.SFPlayer.Services
{
    internal interface ILocalPlaybackEngine : IDisposable
    {
        event EventHandler<bool>? PlayingChanged;

        Task<string> GetDeviceAsync(Func<CancellationToken, Task<string>> getAccessToken, CancellationToken cancellationToken);

        void TogglePause();

        void SetVolume(double volume);

        void Reset();
    }
}
