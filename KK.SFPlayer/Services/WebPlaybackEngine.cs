using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using KK.SFPlayer.Constants;
using Microsoft.Web.WebView2.Core;

namespace KK.SFPlayer.Services
{
    internal sealed class WebPlaybackEngine : ILocalPlaybackEngine
    {
        private readonly Func<IntPtr> _getWindowHandle;
        private readonly string _profileDirectory;
        private CoreWebView2Controller? _controller;
        private CancellationTokenSource? _session;
        private Task<string>? _connection;
        private TaskCompletionSource<string>? _ready;
        private Func<CancellationToken, Task<string>>? _getAccessToken;
        private bool _disposed;
        private double _volume = StreamingConstants.DefaultVolume;

        public WebPlaybackEngine(Func<IntPtr> getWindowHandle, string? profileDirectory = null)
        {
            _getWindowHandle = getWindowHandle;
            _profileDirectory = profileDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationConstants.AppName,
                StreamingConstants.ProfileDirectory);
        }

        public event EventHandler<string>? PlaybackError;

        public event EventHandler<bool>? PlayingChanged;

        public void TogglePause()
        {
            if (_controller is null || _connection?.IsCompletedSuccessfully != true)
            {
                throw new InvalidOperationException(StreamingConstants.DeviceUnavailable);
            }

            _controller.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "toggle" }));
        }

        public void SetVolume(double volume)
        {
            _volume = Math.Clamp(volume, 0, 1);
            _controller?.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "volume", volume = _volume }));
        }

        public async Task<string> GetDeviceAsync(
            Func<CancellationToken, Task<string>> getAccessToken,
            CancellationToken cancellationToken)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return await Dispatcher.UIThread.InvokeAsync(() => GetDeviceAsync(getAccessToken, cancellationToken));
            }

            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(StreamingConstants.Unsupported);
            }

            if (_connection is null || _connection.IsFaulted || _connection.IsCanceled)
            {
                ResetCore();
                _session = new CancellationTokenSource();
                _getAccessToken = getAccessToken;
                _ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _connection = ConnectAsync(_ready, _session.Token);
            }

            return await _connection.WaitAsync(cancellationToken);
        }

        private async Task<string> ConnectAsync(TaskCompletionSource<string> ready, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(StreamingConstants.ConnectionTimeoutSeconds));
            CoreWebView2Controller? controller = null;
            var attached = false;
            try
            {
                var handle = _getWindowHandle();
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(StreamingConstants.InitializationFailed);
                }

                var environment = await CoreWebView2Environment.CreateAsync(
                    null,
                    _profileDirectory,
                    new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                timeout.Token.ThrowIfCancellationRequested();
                controller = await environment.CreateCoreWebView2ControllerAsync(handle);
                timeout.Token.ThrowIfCancellationRequested();
                _controller = controller;
                attached = true;
                controller.Bounds = new Rectangle(0, 0, 1, 1);
                controller.IsVisible = true;
                var browser = controller.CoreWebView2;
                browser.Settings.AreDevToolsEnabled = false;
                browser.Settings.AreDefaultContextMenusEnabled = false;
                browser.Settings.AreHostObjectsAllowed = false;
                browser.Settings.IsStatusBarEnabled = false;
                browser.Settings.IsWebMessageEnabled = true;
                browser.NavigationStarting += (_, args) => args.Cancel = args.Uri != StreamingConstants.Page;
                browser.NewWindowRequested += (_, args) => args.Handled = true;
                browser.WebMessageReceived += OnWebMessageReceived;
                browser.ProcessFailed += (sender, _) =>
                {
                    if (ReferenceEquals(sender, _controller?.CoreWebView2))
                    {
                        Fail(StreamingConstants.InitializationFailed);
                    }
                };
                browser.NavigationCompleted += (sender, args) =>
                {
                    if (ReferenceEquals(sender, _controller?.CoreWebView2) && !args.IsSuccess)
                    {
                        Fail(StreamingConstants.InitializationFailed);
                    }
                };
                browser.SetVirtualHostNameToFolderMapping(
                    StreamingConstants.Host,
                    Path.Combine(AppContext.BaseDirectory, "Assets", "Playback"),
                    CoreWebView2HostResourceAccessKind.DenyCors);
                browser.Navigate(StreamingConstants.Page);
                return await ready.Task.WaitAsync(timeout.Token);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                throw new InvalidOperationException(StreamingConstants.RuntimeMissing);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(StreamingConstants.InitializationFailed);
            }
            finally
            {
                if (!ready.Task.IsCompletedSuccessfully || cancellationToken.IsCancellationRequested)
                {
                    if (controller is not null && (!attached || ReferenceEquals(_controller, controller)))
                    {
                        controller.Close();
                    }

                    if (ReferenceEquals(_controller, controller))
                    {
                        _controller = null;
                    }
                }
            }
        }

        private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            var browser = _controller?.CoreWebView2;
            var session = _session;
            if (!ReferenceEquals(sender, browser) || session is null ||
                session.IsCancellationRequested || args.Source != StreamingConstants.Page)
            {
                return;
            }

            try
            {
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                var root = message.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "loaded":
                        browser!.PostWebMessageAsJson(JsonSerializer.Serialize(new
                        {
                            type = "connect",
                            name = ApplicationConstants.AppName,
                            volume = _volume,
                        }));
                        break;
                    case "token":
                        var requestId = root.GetProperty("id").GetInt32();
                        var token = await _getAccessToken!(session.Token);
                        if (ReferenceEquals(session, _session) && !session.IsCancellationRequested)
                        {
                            browser!.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "token", id = requestId, token }));
                        }

                        break;
                    case "ready":
                        var deviceId = root.GetProperty("deviceId").GetString();
                        if (!string.IsNullOrWhiteSpace(deviceId))
                        {
                            _ready?.TrySetResult(deviceId);
                        }

                        break;
                    case "state":
                        PlayingChanged?.Invoke(this, root.GetProperty("playing").GetBoolean());
                        break;
                    case "error":
                        Fail(root.GetProperty("code").GetString() switch
                        {
                            "authentication_error" => StreamingConstants.Reauthorize,
                            "account_error" => StreamingConstants.PremiumRequired,
                            "playback_error" => StreamingConstants.PlaybackFailed,
                            "not_ready" => StreamingConstants.DeviceUnavailable,
                            _ => StreamingConstants.InitializationFailed,
                        });
                        break;
                }
            }
            catch (OperationCanceledException) when (session.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(session, _session) && !session.IsCancellationRequested)
                {
                    Fail(ErrorMessages.From(exception));
                }
            }
        }

        private void Fail(string message)
        {
            if (_ready?.TrySetException(new InvalidOperationException(message)) != true)
            {
                _connection = null;
                PlaybackError?.Invoke(this, message);
            }
        }

        public void Reset()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(ResetCore);
                return;
            }

            ResetCore();
        }

        private void ResetCore()
        {
            _session?.Cancel();
            _session?.Dispose();
            _session = null;
            _ready?.TrySetCanceled();
            _ready = null;
            _connection = null;
            _getAccessToken = null;
            if (_controller is not null)
            {
                _controller.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                _controller.Close();
                _controller = null;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            Reset();
        }
    }
}
