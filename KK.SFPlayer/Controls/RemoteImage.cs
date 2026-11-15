using KK.SFPlayer.Constants;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using KK.SFPlayer.Services;

namespace KK.SFPlayer.Controls
{
    public sealed partial class RemoteImage : UserControl
    {
        public static readonly StyledProperty<bool> AutoRetryProperty = AvaloniaProperty.Register<RemoteImage, bool>(nameof(AutoRetry));
        public static readonly StyledProperty<bool> IsRetryVisibleProperty = AvaloniaProperty.Register<RemoteImage, bool>(nameof(IsRetryVisible));
        public static readonly StyledProperty<string?> CacheVersionProperty = AvaloniaProperty.Register<RemoteImage, string?>(nameof(CacheVersion));
        public static readonly StyledProperty<bool> CacheOnDiskProperty = AvaloniaProperty.Register<RemoteImage, bool>(nameof(CacheOnDisk), true);
        public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<RemoteImage, string?>(nameof(Url));
        public static readonly StyledProperty<Stretch> StretchProperty = AvaloniaProperty.Register<RemoteImage, Stretch>(nameof(Stretch), Stretch.Uniform);
        public static readonly StyledProperty<Bitmap?> ArtworkProperty = AvaloniaProperty.Register<RemoteImage, Bitmap?>(nameof(Artwork));
        public static readonly StyledProperty<bool> HasFailedProperty = AvaloniaProperty.Register<RemoteImage, bool>(nameof(HasFailed));
        public static readonly StyledProperty<bool> IsPlaceholderVisibleProperty = AvaloniaProperty.Register<RemoteImage, bool>(nameof(IsPlaceholderVisible), true);
        public static readonly StyledProperty<string> ErrorMessageProperty = AvaloniaProperty.Register<RemoteImage, string>(nameof(ErrorMessage), string.Empty);

        private static readonly HttpClient Client = new(new TransientRetryHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly SemaphoreSlim Downloads = new(4, 4);
        private static readonly ArtworkCacheStore Cache = new();

        private CancellationTokenSource? _download;
        private readonly ArtworkCacheStore _currentArtwork = new(persist: false);

        public RemoteImage() => InitializeComponent();

        public bool AutoRetry
        {
            get => GetValue(AutoRetryProperty);
            set => SetValue(AutoRetryProperty, value);
        }

        public bool IsRetryVisible
        {
            get => GetValue(IsRetryVisibleProperty);
            private set => SetValue(IsRetryVisibleProperty, value);
        }

        public string? CacheVersion
        {
            get => GetValue(CacheVersionProperty);
            set => SetValue(CacheVersionProperty, value);
        }

        public bool CacheOnDisk
        {
            get => GetValue(CacheOnDiskProperty);
            set => SetValue(CacheOnDiskProperty, value);
        }

        public string? Url
        {
            get => GetValue(UrlProperty);
            set => SetValue(UrlProperty, value);
        }

        public Stretch Stretch
        {
            get => GetValue(StretchProperty);
            set => SetValue(StretchProperty, value);
        }

        public Bitmap? Artwork
        {
            get => GetValue(ArtworkProperty);
            private set => SetValue(ArtworkProperty, value);
        }

        public bool HasFailed
        {
            get => GetValue(HasFailedProperty);
            private set => SetValue(HasFailedProperty, value);
        }

        public bool IsPlaceholderVisible
        {
            get => GetValue(IsPlaceholderVisibleProperty);
            private set => SetValue(IsPlaceholderVisibleProperty, value);
        }

        public string ErrorMessage
        {
            get => GetValue(ErrorMessageProperty);
            private set => SetValue(ErrorMessageProperty, value);
        }

        private ArtworkCacheStore ImageCache => CacheOnDisk ? Cache : _currentArtwork;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Reload();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _download?.Cancel();
            ClearArtwork();
            base.OnDetachedFromVisualTree(e);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == UrlProperty || change.Property == CacheOnDiskProperty || change.Property == CacheVersionProperty || change.Property == AutoRetryProperty)
            {
                _currentArtwork.ClearMemory();
                if (this.IsAttachedToVisualTree())
                {
                    Reload();
                }
            }
        }

        private void OnRetryClick(object? sender, RoutedEventArgs e)
        {
            e.Handled = true;
            Reload();
        }

        private void ClearArtwork()
        {
            var previous = Artwork;

            Artwork = null;

            previous?.Dispose();

            HasFailed = false;
            IsRetryVisible = false;
            ErrorMessage = string.Empty;
            IsPlaceholderVisible = true;
        }

        private void Reload()
        {
            _download?.Cancel();
            _download = null;
            ClearArtwork();
            if (Url is { } url && ImageCache.TryGet(url, out var cached, CacheVersion))
            {
                try
                {
                    ApplyArtwork(cached);
                    return;
                }
                catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
                {
                    InvalidateCachedArtwork(url);
                }
            }

            _download = new CancellationTokenSource();
            _ = LoadAsync(Url, _download);
        }

        private void InvalidateCachedArtwork(string url) => ImageCache.Invalidate(url, CacheVersion);

        private void ApplyArtwork(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            Artwork = Bitmap.DecodeToWidth(stream, 96);
            IsPlaceholderVisible = false;
            HasFailed = false;
            IsRetryVisible = false;
            ErrorMessage = string.Empty;
        }

        private void ShowFailure(string message)
        {
            ErrorMessage = message;
            IsPlaceholderVisible = true;
            HasFailed = true;
            IsRetryVisible = !AutoRetry;
        }

        private async Task LoadAsync(string? url, CancellationTokenSource download)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return;
                }
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    ShowFailure(ArtworkConstants.ArtworkUnavailable);
                    return;
                }

                while (!download.IsCancellationRequested)
                {
                    await LoadOnceAsync(uri, download);
                    if (!AutoRetry || !HasFailed || download.IsCancellationRequested)
                    {
                        return;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(ArtworkConstants.RetryDelaySeconds), download.Token);
                }
            }
            catch (OperationCanceledException) when (download.IsCancellationRequested)
            {
            }
            finally
            {
                if (ReferenceEquals(_download, download))
                {
                    _download = null;
                }

                download.Dispose();
            }
        }

        private async Task LoadOnceAsync(Uri uri, CancellationTokenSource download)
        {
            try
            {
                await Downloads.WaitAsync(download.Token);
                try
                {
                    var bytes = await ImageCache.LoadAsync(uri, Client, download.Token, CacheVersion);
                    download.Token.ThrowIfCancellationRequested();
                    ApplyArtwork(bytes);
                }
                finally
                {
                    Downloads.Release();
                }
            }
            catch (Exception) when (download.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException exception)
            {
                ShowFailure(exception.StatusCode is null ? ArtworkConstants.ArtworkNetworkError : ArtworkConstants.ArtworkUnavailable);
            }
            catch (OperationCanceledException)
            {
                ShowFailure(ArtworkConstants.ArtworkTimeout);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
            {
                InvalidateCachedArtwork(uri.AbsoluteUri);
                ShowFailure(ArtworkConstants.ArtworkInvalid);
            }
        }
    }
}
