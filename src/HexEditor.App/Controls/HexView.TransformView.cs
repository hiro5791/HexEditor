using HexEditor.Core.Engine;
using Microsoft.UI.Xaml;

namespace HexEditor.App.Controls;

public sealed partial class HexView
{
    private IViewTransform? _shownTransform;
    private bool _transformShown;

    /// <summary>
    /// 変換ビュー (EDIT-45 の仕様 9) の表示: 有効な間はエディタの枠を破線で示し、左上に変換の内容を文字で出す。表示を変えたときだけ要素を更新する。
    /// </summary>
    private void UpdateTransformIndicator()
    {
        IViewTransform? transform = _editor?.Document.ViewTransform;
        if (_transformShown == transform is not null && ReferenceEquals(_shownTransform, transform))
        {
            return;
        }

        _shownTransform = transform;
        _transformShown = transform is not null;
        Visibility visibility = transform is null ? Visibility.Collapsed : Visibility.Visible;
        TransformFrame.Visibility = visibility;
        TransformBadge.Visibility = visibility;
        TransformBadgeText.Text = transform is null ? string.Empty : TransformViewLabel(transform);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TransformBadgeText, TransformBadgeText.Text);
    }

    /// <summary>「変換ビュー: XOR 5A」の文 (バッジ・ステータスバー・検索バーで同じ文を使う)。</summary>
    public static string TransformViewLabel(IViewTransform transform) => Services.Loc.Format("TransformView_Badge", transform.Label);

    /// <summary>テスト用: 変換ビューの表示 (バッジの文字と、枠を出しているか)。</summary>
    internal (string Badge, bool Frame) TransformIndicatorForTest() =>
        (TransformBadge.Visibility == Visibility.Visible ? TransformBadgeText.Text : string.Empty, TransformFrame.Visibility == Visibility.Visible);
}
