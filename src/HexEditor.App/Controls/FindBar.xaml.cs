using System.Text;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索バー (FIND-04、FIND-05、FIND-07、FIND-09)。検索はバックグラウンドで実行し、UI は止めない。
/// 状態 (種類・検索語・オプション) はウィンドウごとに持ち、タブを切り替えても変わらない。
/// </summary>
public sealed partial class FindBar : UserControl
{
    private CancellationTokenSource? _running;
    private SearchPattern? _pattern;

    public FindBar()
    {
        InitializeComponent();
        AutomationProperties.SetName(KindChoice, Loc.Get("Find_Kind_Name"));
        AutomationProperties.SetName(EncodingChoice, Loc.Get("Find_Encoding_Name"));
        AutomationProperties.SetName(PreviousButton, Loc.Get("Find_Previous_Name"));
        AutomationProperties.SetName(NextButton, Loc.Get("Find_Next_Name"));
        AutomationProperties.SetName(CloseButton, Loc.Get("Find_Close_Name"));
        ToolTipService.SetToolTip(PreviousButton, Loc.Get("Find_Previous_Name") + " (Shift+F3)");
        ToolTipService.SetToolTip(NextButton, Loc.Get("Find_Next_Name") + " (F3)");
        ToolTipService.SetToolTip(CloseButton, Loc.Get("Find_Close_Name"));
    }

    /// <summary>検索の対象のビュー。</summary>
    public EditorState? Editor { get; set; }

    /// <summary>長時間処理の管理 (ENG-09)。</summary>
    public OperationCenter? Operations { get; set; }

    /// <summary>一度でも有効な検索語があったか (F3 で検索バーを開くかどうかの判定。FIND-09 の仕様 7)。</summary>
    public bool HasPattern => _pattern is not null;

    public event EventHandler? Closed;

    /// <summary>
    /// 検索バーを開く (FIND-04 の仕様 1・2)。選択範囲が 1〜256 バイトならその内容を検索欄に入れる。
    /// </summary>
    public void Open()
    {
        if (Editor is { HasSelection: true, SelectionLength: <= 256 } editor && KindChoice.SelectedIndex == 0)
        {
            byte[] bytes = new byte[editor.SelectionLength];
            editor.Document.Current.Read(editor.SelectionStart, bytes);
            Query.Text = Convert.ToHexString(bytes).Chunk(2).Aggregate(new StringBuilder(), (sb, c) => sb.Append(sb.Length > 0 ? " " : string.Empty).Append(c)).ToString();
        }

        Visibility = Visibility.Visible;
        Query.Focus(FocusState.Programmatic);
        Query.SelectAll();
        Validate();
    }

    public void Close()
    {
        _running?.Cancel();
        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>次 (F3) または前 (Shift+F3) を検索する。検索語がなければ何もしない。</summary>
    public async Task FindAsync(bool forward)
    {
        if (Editor is not { } editor || _pattern is not { } pattern)
        {
            return;
        }

        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;

        // 開始位置 (FIND-09 の仕様 1): 選択範囲が前回の一致なら、その次 (前) から探す。
        long start = forward
            ? (editor.HasSelection ? editor.SelectionStart + 1 : editor.Cursor)
            : (editor.HasSelection ? editor.SelectionStart : editor.Cursor);
        DocumentSnapshot snapshot = editor.Document.Current;
        bool wrap = WrapChoice.IsChecked == true;
        Busy.IsActive = true;
        Status.Text = Loc.Get("Find_Searching");
        try
        {
            SearchHit? hit = Operations is null
                ? await Task.Run(() => SearchEngine.Find(snapshot, pattern, start, forward, wrap), cts.Token)
                : await Operations.RunAsync(
                    Loc.Get("Operation_Find"),
                    OperationKind.ReadOnly,
                    editor.Document,
                    snapshot.Length,
                    op =>
                    {
                        cts.Token.Register(op.Cancel);
                        return Task.FromResult(SearchEngine.Find(snapshot, pattern, start, forward, wrap, op));
                    });
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (hit is { } h)
            {
                editor.SelectMatch(h.Offset, h.Length);
                Status.Text = h.Wrapped ? Loc.Get(forward ? "Find_WrappedToStart" : "Find_WrappedToEnd") : string.Empty;
            }
            else
            {
                Status.Text = wrap ? Loc.Get("Find_NotFound") : Loc.Get(forward ? "Find_NotFoundToEnd" : "Find_NotFoundToStart");
            }
        }
        catch (OperationCanceledException)
        {
            Status.Text = Loc.Get("Find_Cancelled");
        }
        finally
        {
            if (_running == cts)
            {
                Busy.IsActive = false;
                _running = null;
            }
        }
    }

    /// <summary>入力中の検索語を検証し、変換後のバイト列を表示する (FIND-04 の仕様 6・7)。</summary>
    private void Validate()
    {
        if (Query is null || KindChoice is null || EncodingChoice is null || Status is null || NextButton is null)
        {
            return;
        }

        EncodingChoice.Visibility = KindChoice.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            _pattern = KindChoice.SelectedIndex == 0
                ? SearchPattern.FromHex(Query.Text)
                : SearchPattern.FromText(Query.Text, SelectedEncoding());
            string preview = string.Join(' ', _pattern.Bytes.Take(32).Select((b, i) => _pattern.Mask is { } m && m[i] != 0xFF ? "??" : b.ToString("X2")));
            Status.Text = _pattern.Length > 32 ? preview + " …" : preview;
            Query.BorderBrush = null;
        }
        catch (PatternException ex)
        {
            _pattern = null;
            Status.Text = Query.Text.Length == 0 ? string.Empty : Loc.Format("Find_Error_" + ex.Error, ex.Detail);
            Query.BorderBrush = Query.Text.Length == 0 ? null : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }

        NextButton.IsEnabled = PreviousButton.IsEnabled = _pattern is not null;
    }

    private Encoding SelectedEncoding() => EncodingChoice.SelectedIndex switch
    {
        1 => new UTF8Encoding(false),
        2 => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        3 => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        4 => Encoding.GetEncoding(932),
        _ => Encoding.ASCII,
    };

    private void Query_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Option_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Hex バイト列の入力は左から右に固定する。テキストの検索は表示言語の向きに従う (UI-44 の仕様 2)。
        if (Query is not null && KindChoice is not null)
        {
            Query.FlowDirection = KindChoice.SelectedIndex == 0 ? FlowDirection.LeftToRight : FlowDirection;
        }

        Validate();
    }

    /// <summary>検索欄のキー (00-overview 8.4): Enter / Shift+Enter で次 / 前、Esc で検索の取り消し、なければ閉じる。</summary>
    private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                await FindAsync(forward: !shift);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (_running is not null)
                {
                    _running.Cancel();
                }
                else
                {
                    Close();
                }

                break;
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await FindAsync(forward: true);

    private async void Previous_Click(object sender, RoutedEventArgs e) => await FindAsync(forward: false);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
