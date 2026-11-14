using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Controls.Presenters;
using System;
using System.Linq;
using Avalonia.Input;
using KK.SFPlayer.ViewModels;

namespace KK.SFPlayer.Views
{
    public partial class PlaylistCardView : UserControl
    {
        private PlaylistCardViewModel? _model;
        private ScrollViewer? _scroll;
        private ScrollContentPresenter? _viewport;

        public PlaylistCardView()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => UpdateModel();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _scroll = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
            _viewport = this.GetVisualAncestors().OfType<ScrollContentPresenter>().FirstOrDefault();
            if (_scroll is not null)
            {
                _scroll.ScrollChanged += OnScrollChanged;
            }

            LayoutUpdated += OnLayoutUpdated;
            UpdateModel();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _model?.SetVisible(false);
            if (_scroll is not null)
            {
                _scroll.ScrollChanged -= OnScrollChanged;
            }

            LayoutUpdated -= OnLayoutUpdated;
            _scroll = null;
            _viewport = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void UpdateModel()
        {
            var model = DataContext as PlaylistCardViewModel;
            if (!ReferenceEquals(model, _model))
            {
                _model?.SetVisible(false);
                _model = model;
            }

            UpdateVisibility();
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => UpdateVisibility();

        private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateVisibility();

        private void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Left || e.Source is not Visual source)
            {
                return;
            }

            var ancestors = source.GetVisualAncestors().Prepend(source);
            if (ancestors.Any(visual => visual is Button || ReferenceEquals(visual, Cover)))
            {
                return;
            }

            if (_model?.OpenCommand?.CanExecute(_model.Playlist) == true)
            {
                _model.OpenCommand.Execute(_model.Playlist);
                e.Handled = true;
            }
        }

        private void UpdateVisibility()
        {
            if (_viewport is null || !this.IsAttachedToVisualTree() || !IsEffectivelyVisible)
            {
                _model?.SetVisible(false);
                return;
            }

            var origin = this.TranslatePoint(new Point(0, 12), _viewport);
            var visible = origin.HasValue && Bounds.Height > 24 &&
                new Rect(origin.Value, new Size(Bounds.Width, Bounds.Height - 24))
                    .Intersects(new Rect(_viewport.Bounds.Size));
            _model?.SetVisible(visible);
        }
    }
}
