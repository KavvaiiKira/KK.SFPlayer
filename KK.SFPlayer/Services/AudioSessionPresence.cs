using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KK.SFPlayer.Constants;
using NAudio.Wave;

namespace KK.SFPlayer.Services
{
    internal sealed class AudioSessionPresence : IDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _worker;
        private bool _disposed;

        internal Task Completion => _worker ?? Task.CompletedTask;

        public Task<bool> StartAsync()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worker ??= Task.Run(RunAsync);
            return _ready.Task;
        }

        private async Task RunAsync()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                using var output = await new WasapiPlayerBuilder().WithDefaultDeviceStreamRouting().BuildAsync();
                _lifetime.Token.ThrowIfCancellationRequested();
                output.Init(new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
                output.Play();
                var processIds = new HashSet<int> { Environment.ProcessId };
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(AudioSessionConstants.RefreshIntervalSeconds));
                do
                {
                    AudioSessionBranding.Apply(processIds, ApplicationConstants.AppName, AudioSessionBranding.IconLocation, _lifetime.Token);
                    _ready.TrySetResult(true);
                }
                while (await timer.WaitForNextTickAsync(_lifetime.Token));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or NotSupportedException)
            {
                // An unavailable audio device must not prevent the application from opening.
            }
            finally
            {
                _ready.TrySetResult(false);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            _ = Completion.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
        }
    }
}
