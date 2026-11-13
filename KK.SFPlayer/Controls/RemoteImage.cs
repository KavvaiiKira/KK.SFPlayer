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
        private static readonly HttpClient Client = new(new TransientRetryHandler()) { Timeout = TimeSpan.FromSeconds(20) };
        private static readonly SemaphoreSlim Downloads = new(4, 4);

        public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<RemoteImage, string?>(nameof(Url));
        public static readonly StyledProperty<Stretch> StretchProperty = AvaloniaProperty.Register<RemoteImage, Stretch>(nameof(Stretch), Stretch.Uniform);
        public static readonly StyledProperty<Bitmap?> ArtworkProperty = AvaloniaProperty.Register<RemoteImage, Bitmap?>(nameof(Artwork));
        public static readonly StyledProperty<bool> HasFailedProperty = AvaloniaProperty.Register<RemoteImage, bool>(nameof(HasFailed));
        public static readonly StyledProperty<bool> IsPlaceholderVisibleProperty = AvaloniaProperty.Register<RemoteImage, bool>(nameof(IsPlaceholderVisible), true);
        public static readonly StyledProperty<string> ErrorMessageProperty = AvaloniaProperty.Register<RemoteImage, string>(nameof(ErrorMessage), string.Empty);

        private CancellationTokenSource? _download;

        public RemoteImage() => InitializeComponent();

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
            if (change.Property == UrlProperty && this.IsAttachedToVisualTree())
            {
                Reload();
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
            ErrorMessage = string.Empty;
            IsPlaceholderVisible = true;
        }

        private void Reload()
        {
            _download?.Cancel();
            _download = new CancellationTokenSource();
            ClearArtwork();
            _ = LoadAsync(Url, _download);
        }

        private void ShowFailure(string message)
        {
            ErrorMessage = message;
            IsPlaceholderVisible = true;
            HasFailed = true;
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

                await Downloads.WaitAsync(download.Token);
                try
                {
                    var bytes = await Client.GetByteArrayAsync(uri, download.Token);
                    download.Token.ThrowIfCancellationRequested();
                    using var stream = new MemoryStream(bytes);
                    var bitmap = Bitmap.DecodeToWidth(stream, 96);
                    if (download.IsCancellationRequested)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    Artwork = bitmap;
                    IsPlaceholderVisible = false;
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
                ShowFailure(ArtworkConstants.ArtworkInvalid);
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
    }
}
