using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace KK.SFPlayer.Controls
{
    public class TrimmedTextBlock : TextBlock
    {
        public TrimmedTextBlock() => Background = Brushes.Transparent;

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            ToolTip.SetTip(this, TextLayout.TextLines.Any(line => line.HasCollapsed) ? Text : null);
            base.OnPointerEntered(e);
        }
    }
}
