using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// 一覧 (ListView) と木 (TreeView) の行で Enter と Space を受ける。行の入れ物 (ListViewItem、TreeViewItem) は、この 2 つのキーを自分で処理済みにするので、
/// 一覧の <c>KeyDown</c> には届かない。入れ物から届いたキーだけを <c>PreviewKeyDown</c> で受けて、同じ処理に渡す
/// (行の中の TextBox や Button のキーは、これまでどおりそれぞれが受ける)。
/// </summary>
internal static class ItemKeys
{
    /// <summary>キーが、行の入れ物そのものから届いた Enter か Space か。</summary>
    public static bool IsActivation(KeyRoutedEventArgs e) =>
        e.OriginalSource is ListViewItem or GridViewItem or TreeViewItem && e.Key is VirtualKey.Enter or VirtualKey.Space;

    /// <summary>一覧の <c>KeyDown</c> に <paramref name="handler"/> をつけ、行の入れ物から届く Enter / Space もそこに渡す (コードで作る一覧用)。</summary>
    public static void OnKey(UIElement list, KeyEventHandler handler)
    {
        list.KeyDown += handler;
        Attach(list, handler);
    }

    /// <summary>行の入れ物から届いた Enter / Space を <paramref name="handler"/> (一覧の KeyDown の処理) にも渡す。</summary>
    public static void Attach(UIElement list, KeyEventHandler handler) =>
        list.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((sender, e) =>
        {
            if (IsActivation(e))
            {
                handler(sender, e);
            }
        }), handledEventsToo: false);
}
