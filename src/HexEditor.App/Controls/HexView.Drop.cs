using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace HexEditor.App.Controls;

/// <summary>ドロップ先のバイト (Ctrl を押しながらのファイルのドロップでの挿入位置。UI-34 の仕様 1、EDIT-18 の仕様 6)。</summary>
public sealed partial class HexView
{
    /// <summary>
    /// <paramref name="relativeTo"/> の座標 <paramref name="point"/> が Hex 列・テキスト列のセルの上なら、そのバイトのオフセット。
    /// 表示の外なら null。
    /// </summary>
    internal long? OffsetAt(UIElement relativeTo, Point point)
    {
        Point local = relativeTo.TransformToVisual(Surface).TransformPoint(point);
        if (local.X < 0 || local.Y < 0 || local.X > Surface.ActualWidth || local.Y > Surface.ActualHeight)
        {
            return null;
        }

        return TryHitTest(local, out HitResult hit) ? hit.Offset : null;
    }
}
