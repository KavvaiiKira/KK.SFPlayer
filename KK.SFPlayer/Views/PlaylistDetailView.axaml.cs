using Avalonia;
using Avalonia.Controls;
using System.ComponentModel;
using System.Linq;
using Avalonia.VisualTree;
using KK.SFPlayer.ViewModels;

namespace KK.SFPlayer.Views
{
    public partial class PlaylistDetailView : UserControl
    {
        private MainViewModel? _model;

        public PlaylistDetailView()
        {
            InitializeComponent();
            TracksList.AddHandler(ScrollViewer.ScrollChangedEvent, OnTracksScrolled);
            DataContextChanged += (_, _) => UpdateModel();
        }

        private void OnTracksScrolled(object? sender, ScrollChangedEventArgs e)
        {
            if (e.OffsetDelta.Y > 0 && e.ExtentDelta.Y == 0 && e.Source is ScrollViewer scroll &&
                scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 100 &&
                _model?.LoadMoreTracksCommand.CanExecute(null) == true)
            {
                _model.LoadMoreTracksCommand.Execute(null);
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            UpdateModel();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelPropertyChanged;
                _model = null;
            }

            base.OnDetachedFromVisualTree(e);
        }

        private void UpdateModel()
        {
            if (_model is not null)
            {
                _model.PropertyChanged -= OnModelPropertyChanged;
            }

            _model = DataContext as MainViewModel;
            if (_model is not null)
            {
                _model.PropertyChanged += OnModelPropertyChanged;
            }
        }

        private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(MainViewModel.SelectedPlaylist) or nameof(MainViewModel.SelectedSort))
            {
                var scroll = TracksList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (scroll is not null)
                {
                    scroll.Offset = default;
                }
            }
        }
    }
}
