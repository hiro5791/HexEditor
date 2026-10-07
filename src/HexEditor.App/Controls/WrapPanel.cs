using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HexEditor.App.Controls;

/// <summary>
/// 子要素を横に並べ、幅が足りなければ次の行に折り返すパネル (検索バーのオプションなど)。各要素は自分の幅のまま置く。
/// 右から左に書く言語では、FlowDirection によって全体が反転する。
/// </summary>
public sealed partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 12;

    public double VerticalSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0;
        double lineHeight = 0;
        double width = 0;
        double height = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            Size size = child.DesiredSize;
            if (size.Width == 0)
            {
                continue;
            }

            if (lineWidth > 0 && lineWidth + HorizontalSpacing + size.Width > availableSize.Width)
            {
                width = Math.Max(width, lineWidth);
                height += lineHeight + VerticalSpacing;
                lineWidth = 0;
                lineHeight = 0;
            }

            lineWidth += (lineWidth > 0 ? HorizontalSpacing : 0) + size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return new Size(Math.Max(width, lineWidth), height + lineHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double lineHeight = 0;
        foreach (UIElement child in Children)
        {
            Size size = child.DesiredSize;
            if (size.Width == 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            if (x > 0 && x + HorizontalSpacing + size.Width > finalSize.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }

            x += x > 0 ? HorizontalSpacing : 0;
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return finalSize;
    }
}
