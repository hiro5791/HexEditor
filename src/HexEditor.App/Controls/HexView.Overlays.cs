using Microsoft.UI.Xaml;

namespace HexEditor.App.Controls;

/// <summary>Hex ビューの右上に重ねる表示 (フォレンジックの強調の凡例。FOR-16 の仕様 2)。</summary>
public sealed partial class HexView
{
    private readonly Dictionary<string, FrameworkElement> _overlays = [];

    /// <summary>重ねる表示を置く (null で外す)。<paramref name="element"/> は右上に置き、マウスの操作は受けない。</summary>
    public void SetOverlay(string name, FrameworkElement? element)
    {
        if (_overlays.Remove(name, out FrameworkElement? old))
        {
            Surface.Children.Remove(old);
        }

        if (element is null)
        {
            return;
        }

        element.HorizontalAlignment = HorizontalAlignment.Right;
        element.VerticalAlignment = VerticalAlignment.Top;
        element.Margin = new Thickness(0, 4 + _overlays.Count * 4, 20, 0);
        element.IsHitTestVisible = false;
        _overlays[name] = element;
        Surface.Children.Add(element);
    }

    /// <summary>重ねている表示 (テスト用)。</summary>
    internal FrameworkElement? Overlay(string name) => _overlays.GetValueOrDefault(name);
}
