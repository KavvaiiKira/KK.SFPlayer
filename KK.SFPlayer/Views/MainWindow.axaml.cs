using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.ComponentModel;
using System;
using KK.SFPlayer.ViewModels;
using KK.SFPlayer.Constants;
using Avalonia.VisualTree;

namespace KK.SFPlayer.Views
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer _volumeCloseTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(StreamingConstants.VolumePopupCloseDelayMilliseconds),
        };
        private IPointer? _volumePointer;

        public MainWindow()
        {
            InitializeComponent();
            _volumeCloseTimer.Tick += OnVolumeCloseTick;
            VolumePopup.Closed += (_, _) => _volumeCloseTimer.Stop();
            AddHandler(KeyDownEvent, OnVolumeKeyDown, RoutingStrategies.Tunnel);
            ProgressSlider.AddHandler(PointerPressedEvent, OnSeekPointerPressed, RoutingStrategies.Tunnel);
            ProgressSlider.AddHandler(PointerReleasedEvent, OnSeekPointerReleased, RoutingStrategies.Bubble, true);
            ProgressSlider.AddHandler(KeyDownEvent, OnSeekKeyDown, RoutingStrategies.Tunnel);
            ProgressSlider.AddHandler(KeyUpEvent, OnSeekKeyUp, RoutingStrategies.Tunnel);
            Deactivated += (_, _) =>
            {
                VolumePopup.IsOpen = false;
                (DataContext as MainViewModel)?.Playback.CommitSeek();
            };
            PlaylistsList.AddHandler(ScrollViewer.ScrollChangedEvent, OnPlaylistsScrolled);
            Opened += async (_, _) =>
            {
                if (DataContext is MainViewModel viewModel)
                {
                    viewModel.PropertyChanged += OnViewModelPropertyChanged;
                    await viewModel.InitializeAsync();
                }
            };
            Closed += (_, _) =>
            {
                _volumeCloseTimer.Stop();
                VolumePopup.IsOpen = false;
                if (DataContext is MainViewModel viewModel)
                {
                    viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                    viewModel.Dispose();
                }
            };
        }

        private void OnVolumePointerEntered(object? sender, PointerEventArgs e)
        {
            _volumeCloseTimer.Stop();
            VolumePopup.IsOpen = true;
        }

        private void OnVolumePointerExited(object? sender, PointerEventArgs e)
        {
            _volumePointer = e.Pointer;
            _volumeCloseTimer.Start();
        }

        private void OnVolumeClick(object? sender, RoutedEventArgs e) => VolumePopup.IsOpen = true;

        private void OnVolumeKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && VolumePopup.IsOpen)
            {
                VolumePopup.IsOpen = false;
                e.Handled = true;
            }
        }

        private void OnVolumeCloseTick(object? sender, EventArgs e)
        {
            if (VolumeButton.IsPointerOver || VolumePanel.IsPointerOver)
            {
                _volumeCloseTimer.Stop();
                return;
            }

            if (_volumePointer?.Captured is Visual captured &&
                (ReferenceEquals(captured, VolumeSlider) || VolumeSlider.IsVisualAncestorOf(captured)))
            {
                return;
            }

            _volumeCloseTimer.Stop();
            VolumePopup.IsOpen = false;
        }

        private void OnPlaylistsScrolled(object? sender, ScrollChangedEventArgs e)
        {
            if (e.OffsetDelta.Y > 0 && e.Source is ScrollViewer scroll &&
                scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 100 &&
                DataContext is MainViewModel viewModel && viewModel.Library.LoadMorePlaylistsCommand.CanExecute(null))
            {
                viewModel.Library.LoadMorePlaylistsCommand.Execute(null);
            }
        }

        private void OnSeekPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(ProgressSlider).Properties.IsLeftButtonPressed)
            {
                (DataContext as MainViewModel)?.Playback.BeginSeek();
            }
        }

        private void OnSeekPointerReleased(object? sender, PointerReleasedEventArgs e) => (DataContext as MainViewModel)?.Playback.CommitSeek();

        private void OnSeekKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            {
                (DataContext as MainViewModel)?.Playback.BeginSeek();
            }
        }

        private void OnSeekKeyUp(object? sender, KeyEventArgs e) => (DataContext as MainViewModel)?.Playback.CommitSeek();

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsLogoutConfirmationOpen) && sender is MainViewModel viewModel)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (viewModel.IsLogoutConfirmationOpen)
                    {
                        CancelLogoutButton.Focus();
                    }
                    else
                    {
                        AccountButton.Focus();
                    }
                });
            }
        }

        private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void OnResizePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Control { Tag: string tag } && Enum.TryParse<WindowEdge>(tag, out var edge) &&
                e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginResizeDrag(edge, e);
                e.Handled = true;
            }
        }

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    }
}
