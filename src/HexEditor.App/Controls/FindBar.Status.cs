using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索の結果をステータスバーに出す (FIND-02 の仕様 5: 検索バーにフォーカスがなければ 5 秒間。FIND-09 の仕様 4: 折り返したときは
/// 検索バーとステータスバーの両方)。表示はメインウィンドウが行う。
/// </summary>
public sealed partial class FindBar
{
    /// <summary>ステータスバーに出す結果 (「12 件見つかりました」「先頭に戻って検索しました」など)。</summary>
    public event EventHandler<string>? ResultReported;

    /// <summary>
    /// 結果を知らせる。<paramref name="important"/> (折り返し) なら常に、そうでなければ検索バーにフォーカスがないときだけ
    /// ステータスバーに出す。
    /// </summary>
    private void ReportResult(string message, bool important)
    {
        if (message.Length > 0 && (important || !HasFocusWithin()))
        {
            ResultReported?.Invoke(this, message);
        }
    }

    /// <summary>表示設定が変わった: 「表示中の文字コードに合わせる」で文字コードが変わっていれば検索語を作り直す。</summary>
    private void Editor_ViewChanged(object? sender, EventArgs e)
    {
        if (SelectedEncoding == Core.Search.TextEncodings.DisplayEncodingId && Kind == Core.Search.SearchKind.Text)
        {
            DispatcherQueue.TryEnqueue(Validate);
        }
    }

    /// <summary>範囲を「選択範囲」にしている間の、その範囲 (エディタに枠で示す。FIND-11 の仕様 3)。それ以外は空。</summary>
    public IReadOnlyList<Core.Search.SearchRange> OutlinedScope => IsOpen && ScopeChoice.SelectedIndex == 1 ? _scope.Ranges : [];

    /// <summary>「すべて置換」を押せるか (置換欄が開いていて、検索語と置換語が正しい)。</summary>
    public bool CanReplaceAll => IsReplaceMode && ReplaceAllButton.IsEnabled;

    /// <summary>フォーカスが検索バーの中にあるか (閉じていれば false)。</summary>
    internal bool HasFocusWithin()
    {
        if (!IsOpen || XamlRoot is null)
        {
            return false;
        }

        for (var element = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, this))
            {
                return true;
            }
        }

        return false;
    }
}
