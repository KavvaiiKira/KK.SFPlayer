using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.ComponentModel;
using KK.SFPlayer.ViewModels;

namespace KK.SFPlayer.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
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
                if (DataContext is MainViewModel viewModel)
                {
                    viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                    viewModel.Dispose();
                }
            };
        }

        private void OnPlaylistsScrolled(object? sender, ScrollChangedEventArgs e)
        {
            if (e.OffsetDelta.Y > 0 && e.Source is ScrollViewer scroll &&
                scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 100 &&
                DataContext is MainViewModel viewModel && viewModel.LoadMorePlaylistsCommand.CanExecute(null))
            {
                viewModel.LoadMorePlaylistsCommand.Execute(null);
            }
        }

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

        private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    }
}
