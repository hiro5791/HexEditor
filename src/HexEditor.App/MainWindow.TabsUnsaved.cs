using System.ComponentModel;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// タブの未保存の印 (UI-09 の仕様 2): 未保存の変更があるタブは、閉じるボタンの位置に「●」を出し、タブにポインタを置くと「×」に戻す。
/// 見出しの文字には ● を出さない (読み上げ用の名前 <see cref="DocumentViewModel.Header"/> には残す。色だけに頼らない)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>閉じるボタンの既定の記号 (×。TabViewItem のテンプレートと同じ)。</summary>
    internal const string CloseGlyph = "";

    /// <summary>未保存の印 (小さな塗りつぶしの円。Segoe Fluent Icons)。</summary>
    internal const string UnsavedGlyph = "";

    /// <summary>
    /// タブの見出しに表示する文字: <see cref="DocumentViewModel.Header"/> から未保存の印 (●) を除く (印は閉じるボタンの位置に出す)。
    /// </summary>
    public static string VisibleTabHeader(string header, bool modified)
    {
        if (!modified)
        {
            return header;
        }

        string lockPrefix = header.StartsWith(DocumentViewModel.LockGlyph + " ", StringComparison.Ordinal) ? DocumentViewModel.LockGlyph + " " : string.Empty;
        string rest = header[lockPrefix.Length..];
        return rest.StartsWith("● ", StringComparison.Ordinal) ? lockPrefix + rest[2..] : header;
    }

    /// <summary>文書のタブの項目ごとの状態 (見ている文書、ポインタが上にあるか)。</summary>
    private sealed class TabMarkState
    {
        public DocumentViewModel? Document;

        public PropertyChangedEventHandler? Handler;

        public bool PointerOver;

        /// <summary>テンプレートの閉じるボタン (見つけたら覚えておく)。</summary>
        public Button? Close;
    }

    private static readonly DependencyProperty TabMarkStateProperty = DependencyProperty.RegisterAttached(
        "TabMarkState", typeof(object), typeof(MainWindow), new PropertyMetadata(null));

    /// <summary>文書のタブの項目が表示された (DocumentTabTemplate の Loaded)。項目は使い回されるので、文書が変わったら付け直す。</summary>
    private void DocumentTab_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TabViewItem item)
        {
            return;
        }

        if (item.GetValue(TabMarkStateProperty) is not TabMarkState state)
        {
            state = new TabMarkState();
            item.SetValue(TabMarkStateProperty, state);
            item.PointerEntered += (_, _) =>
            {
                state.PointerOver = true;
                UpdateCloseMark(item);
            };
            item.PointerExited += (_, _) =>
            {
                state.PointerOver = false;
                UpdateCloseMark(item);
            };
            item.PointerCanceled += (_, _) =>
            {
                state.PointerOver = false;
                UpdateCloseMark(item);
            };
            item.DataContextChanged += (_, _) => WatchTabDocument(item, state);
            item.Unloaded += (_, _) =>
            {
                if (!item.IsLoaded)
                {
                    Unwatch(state);
                }
            };
        }

        WatchTabDocument(item, state);
    }

    private void WatchTabDocument(TabViewItem item, TabMarkState state)
    {
        DocumentViewModel? doc = item.DataContext as DocumentViewModel;
        if (!ReferenceEquals(state.Document, doc))
        {
            Unwatch(state);
            state.Document = doc;
            if (doc is not null)
            {
                state.Handler = (_, _) =>
                {
                    if (item.DispatcherQueue.HasThreadAccess)
                    {
                        UpdateCloseMark(item);
                    }
                    else
                    {
                        item.DispatcherQueue.TryEnqueue(() => UpdateCloseMark(item));
                    }
                };
                doc.PropertyChanged += state.Handler;
            }
        }

        UpdateCloseMark(item);
    }

    private static void Unwatch(TabMarkState state)
    {
        if (state.Document is { } old && state.Handler is { } handler)
        {
            old.PropertyChanged -= handler;
        }

        state.Document = null;
        state.Handler = null;
    }

    /// <summary>閉じるボタンの記号: 未保存なら ●、ポインタが上にあるか保存済みなら ×。</summary>
    private static void UpdateCloseMark(TabViewItem item)
    {
        // 文書の変化 (カーソルの移動を含む) のたびに呼ばれるので、閉じるボタンは 1 度だけ探す。
        if (item.GetValue(TabMarkStateProperty) is not TabMarkState state
            || (state.Close ??= FindByName(item, "CloseButton") as Button) is not { } close)
        {
            return;
        }

        string glyph = state.Document is { Document.IsModified: true } && !state.PointerOver ? UnsavedGlyph : CloseGlyph;
        if (close.Content as string != glyph)
        {
            close.Content = glyph;
        }
    }

    /// <summary>文書のタブの閉じるボタンの記号 (テスト用の命令)。表示されていなければ null。</summary>
    internal string? TabCloseMark(DocumentViewModel doc) =>
        Tabs.ContainerFromItem(doc) is TabViewItem item && FindByName(item, "CloseButton") is Button { Content: string glyph } ? glyph : null;

    /// <summary>タブにポインタを置いた・離したときと同じ処理 (テスト用の命令。実際のマウスは使わない)。</summary>
    internal void SimulateTabHover(DocumentViewModel doc, bool over)
    {
        if (Tabs.ContainerFromItem(doc) is TabViewItem item && item.GetValue(TabMarkStateProperty) is TabMarkState state)
        {
            state.PointerOver = over;
            UpdateCloseMark(item);
        }
    }
}
