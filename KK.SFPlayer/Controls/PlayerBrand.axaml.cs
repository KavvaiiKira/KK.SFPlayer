using Avalonia;
using Avalonia.Controls;

namespace KK.SFPlayer.Controls
{
    public sealed partial class PlayerBrand : UserControl
    {
        public static readonly StyledProperty<bool> IsSpinningProperty =
            AvaloniaProperty.Register<PlayerBrand, bool>(nameof(IsSpinning));

        public PlayerBrand() => InitializeComponent();

        public bool IsSpinning
        {
            get => GetValue(IsSpinningProperty);
            set => SetValue(IsSpinningProperty, value);
        }
    }
}
