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
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索バー (FIND-04、FIND-05、FIND-07、FIND-09〜FIND-12)。検索はバックグラウンドで実行し、UI は止めない。
/// 状態 (種類・検索語・オプション・直前の一致) はウィンドウごとに持ち、タブを切り替えても変わらない (FIND-04 の仕様 10)。
/// </summary>
public sealed partial class FindBar : UserControl
{
    /// <summary>件数の表示の上限 (FIND-12 の仕様 3)。</summary>
    private const long CountLimit = SearchEngine.CountLimit;

    private readonly FindNavigator _navigator = new();
    private CancellationTokenSource? _running;
    private CancellationTokenSource? _counting;
    private SearchPattern? _pattern;
    private SearchScope _scope = SearchScope.WholeDocument;
    private SearchResults? _count;
    private bool _kindChosen;

    public FindBar()
    {
        InitializeComponent();
        foreach (TextEncodingId id in TextEncodings.All)
        {
            EncodingChoice.Items.Add(new ComboBoxItem { Content = EncodingName(id), Tag = id });
        }

        EncodingChoice.SelectedIndex = 0;
        AutomationProperties.SetName(KindChoice, Loc.Get("Find_Kind_Name"));
        AutomationProperties.SetName(EncodingChoice, Loc.Get("Find_Encoding_Name"));
        AutomationProperties.SetName(DirectionChoice, Loc.Get("Find_Direction_Name"));
        AutomationProperties.SetName(ScopeChoice, Loc.Get("Find_Scope_Name"));
        AutomationProperties.SetName(PreviousButton, Loc.Get("Find_Previous_Name"));
        AutomationProperties.SetName(NextButton, Loc.Get("Find_Next_Name"));
        AutomationProperties.SetName(OptionsToggle, Loc.Get("Find_Options_Name"));
        AutomationProperties.SetName(CloseButton, Loc.Get("Find_Close_Name"));
        ToolTipService.SetToolTip(PreviousButton, Loc.Get("Find_Previous_Name") + " (Shift+F3)");
        ToolTipService.SetToolTip(NextButton, Loc.Get("Find_Next_Name") + " (F3)");
        ToolTipService.SetToolTip(OptionsToggle, Loc.Get("Find_Options_Name"));
        ToolTipService.SetToolTip(CloseButton, Loc.Get("Find_Close_Name"));
    }

    private EditorState? _editor;

    /// <summary>検索の対象のビュー。変わったら (タブの切り替え) 直前の一致と件数を忘れる。検索語と条件は残す。</summary>
    public EditorState? Editor
    {
        get => _editor;
        set
        {
            if (_editor != value)
            {
                _editor = value;
                Validate();
            }
        }
    }

    /// <summary>長時間処理の管理 (ENG-09)。</summary>
    public OperationCenter? Operations { get; set; }

    /// <summary>一度でも有効な検索語があったか (F3 で検索バーを開くかどうかの判定。FIND-09 の仕様 7)。</summary>
    public bool HasPattern => _pattern is not null;

    public bool IsOpen => Visibility == Visibility.Visible;

    public event EventHandler? Closed;

    /// <summary>検索語・条件・件数が変わった (表示中の一致の強調とスクロールバーの印を更新する)。</summary>
    public event EventHandler? MatchesChanged;

    /// <summary>
    /// 表示中の範囲と重なる一致 (FIND-04 の仕様 9)。検索バーが開いていて検索語が有効なときだけ返す。
    /// </summary>
    public IReadOnlyList<(long Offset, long Length)> MatchesInView(DocumentSnapshot snapshot, long offset, long length)
    {
        if (!IsOpen || _pattern is not { } pattern)
        {
            return [];
        }

        return SearchEngine.FindInView(snapshot, pattern, offset, length, _scope, out _)
            .Select(m => (m.Offset, m.Length)).ToList();
    }

    /// <summary>スクロールバーの印にする一致の位置 (数え上げが終わっていれば。多すぎる場合は間引く)。</summary>
    public IReadOnlyList<long>? MarkerOffsets()
    {
        if (!IsOpen || _count is not { } count)
        {
            return null;
        }

        IReadOnlyList<SearchMatch> matches = count.Matches;
        int step = Math.Max(1, matches.Count / 10_000);
        return matches.Where((_, i) => i % step == 0).Select(m => m.Offset).ToList();
    }

    /// <summary>
    /// 検索バーを開く (FIND-04 の仕様 1・2)。選択範囲が 1〜256 バイトならその内容を検索欄に入れ、257 バイト以上なら
    /// 検索範囲を「選択範囲」にする。
    /// </summary>
    public void Open()
    {
        if (Editor is { } editor)
        {
            // 種類の既定: 前回使った種類。初回はカーソルのある列 (FIND-04 の仕様 3)。
            if (!_kindChosen)
            {
                KindChoice.SelectedIndex = editor.ActiveColumn == ActiveColumn.Text ? 1 : 0;
            }

            if (editor.HasSelection && editor.SelectionLength <= 256)
            {
                byte[] bytes = new byte[editor.SelectionLength];
                editor.Document.Current.Read(editor.SelectionStart, bytes);
                Query.Text = KindChoice.SelectedIndex == 1 && TryDecode(bytes, out string? text) ? text : ToHex(bytes);
                if (KindChoice.SelectedIndex == 1 && Query.Text.Length > 0 && !TryDecode(bytes, out _))
                {
                    KindChoice.SelectedIndex = 0;
                }
            }

            // 選択範囲の検索は、開いたときの選択範囲に固定する (FIND-11 の仕様 1)。
            ScopeSelectionItem.IsEnabled = editor.HasSelection;
            if (editor.HasSelection)
            {
                _scope = SearchScope.Of(editor.SelectionStart, editor.SelectionLength);
                if (editor.SelectionLength > 256)
                {
                    ScopeChoice.SelectedIndex = 1;
                }
            }
            else if (ScopeChoice.SelectedIndex == 1)
            {
                ScopeChoice.SelectedIndex = 0;
            }
        }

        Visibility = Visibility.Visible;
        Query.Focus(FocusState.Programmatic);
        Query.SelectAll();
        Validate();
    }

    public void Close()
    {
        _running?.Cancel();
        _counting?.Cancel();
        Visibility = Visibility.Collapsed;
        MatchesChanged?.Invoke(this, EventArgs.Empty);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 次 (F3) または前 (Shift+F3) を検索する (FIND-09)。開始位置は、選択範囲が直前の一致と同じならその次 (前)、
    /// そうでなければカーソル位置。検索語がなければ何もしない。
    /// </summary>
    public async Task FindAsync(bool forward)
    {
        if (Editor is not { } editor || _pattern is not { } pattern)
        {
            return;
        }

        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        DocumentSnapshot snapshot = editor.Document.Current;
        bool wrap = WrapChoice.IsChecked == true;
        var options = new SearchOptions { Scope = CurrentScope };
        long cursor = editor.Cursor;
        long selStart = editor.SelectionStart;
        long selLength = editor.SelectionLength;
        Busy.IsActive = true;
        Status.Text = Loc.Get("Find_Searching");
        try
        {
            SearchHit? hit = Operations is null
                ? await Task.Run(() => _navigator.FindNext(snapshot, pattern, forward, wrap, cursor, selStart, selLength, options, null, cts.Token), cts.Token)
                : await Operations.RunAsync(
                    Loc.Get("Operation_Find"),
                    OperationKind.ReadOnly,
                    editor.Document,
                    snapshot.Length,
                    op =>
                    {
                        cts.Token.Register(op.Cancel);
                        return Task.FromResult(_navigator.FindNext(snapshot, pattern, forward, wrap, cursor, selStart, selLength, options, op, op.CancellationToken));
                    });
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (hit is { } h)
            {
                editor.SelectMatch(h.Offset, h.Length);
                Status.Text = h.Wrapped ? Loc.Get(forward ? "Find_WrappedToStart" : "Find_WrappedToEnd") : string.Empty;
                MarkQuery(QueryState.Normal);
                UpdateCountText();
                Announce(h.Wrapped ? Status.Text : Loc.Format("Find_FoundAt", StatusFormat.Hex(h.Offset)));
            }
            else
            {
                Status.Text = wrap ? Loc.Get("Find_NotFound") : Loc.Get(forward ? "Find_NotFoundToEnd" : "Find_NotFoundToStart");
                MarkQuery(QueryState.NotFound);
                Announce(Status.Text);
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

    // ---- 件数 (FIND-12) ----

    /// <summary>検索語を確定したら件数を数える。範囲が 1 GiB を超える場合はボタンを出し、押したときだけ数える。</summary>
    private void StartCountIfAutomatic()
    {
        if (Editor is not { } editor || _pattern is null)
        {
            return;
        }

        // 同じ条件ですでに数えた (数えている) ときは数え直さない。
        if (_count is not null || _counting is not null)
        {
            UpdateCountText();
            return;
        }

        long scopeBytes = CurrentScope.TotalLength(editor.Document.Length);
        if (SearchEngine.CountsAutomatically(scopeBytes))
        {
            _ = CountAsync();
        }
        else
        {
            CountButton.Visibility = Visibility.Visible;
            CountText.Visibility = Visibility.Collapsed;
        }
    }

    private async void Count_Click(object sender, RoutedEventArgs e) => await CountAsync();

    private async Task CountAsync()
    {
        if (Editor is not { } editor || _pattern is not { } pattern)
        {
            return;
        }

        _counting?.Cancel();
        var cts = new CancellationTokenSource();
        _counting = cts;
        _count = null;
        CountButton.Visibility = Visibility.Collapsed;
        CountText.Visibility = Visibility.Visible;
        UpdateCountText();
        DocumentSnapshot snapshot = editor.Document.Current;
        var options = new SearchOptions { Scope = CurrentScope };
        try
        {
            SearchResults results = Operations is null
                ? await Task.Run(() => SearchEngine.Count(snapshot, pattern, options, null, cts.Token), cts.Token)
                : await Operations.RunAsync(
                    Loc.Get("Operation_Count"),
                    OperationKind.ReadOnly,
                    editor.Document,
                    CurrentScope.TotalLength(snapshot.Length),
                    op =>
                    {
                        cts.Token.Register(op.Cancel);
                        return Task.FromResult(SearchEngine.Count(snapshot, pattern, options, op, op.CancellationToken));
                    });
            if (_counting == cts)
            {
                _count = results;
                UpdateCountText();
                MatchesChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_counting == cts)
            {
                _counting = null;
                UpdateCountText();
            }
        }
    }

    /// <summary>「3 / 125」。数え上げ中は「3 / 数えています…」、100 万件を超えたら「100 万件以上」(FIND-12 の仕様 1〜3)。</summary>
    private void UpdateCountText()
    {
        if (Editor is not { } editor || _pattern is null)
        {
            CountText.Text = string.Empty;
            return;
        }

        string current = _navigator.IsLastMatch(editor.SelectionStart, editor.SelectionLength) && _navigator.LastMatch is { } m
            ? _count is { } c ? (c.CountBefore(m.Offset) + 1).ToString("N0") : "?"
            : "-";
        CountText.Text = _counting is not null
            ? Loc.Format("Find_Counting", current)
            : _count is { LimitReached: true }
                ? Loc.Format("Find_CountOverLimit", current, CountLimit.ToString("N0"))
                : _count is { } done ? Loc.Format("Find_CountResult", current, done.Count.ToString("N0")) : string.Empty;
    }

    // ---- 入力の検証 (FIND-04 の仕様 6・7) ----

    private enum QueryState
    {
        Normal,
        Invalid,
        NotFound,
    }

    private SearchScope CurrentScope => ScopeChoice.SelectedIndex == 1 ? _scope : SearchScope.WholeDocument;

    /// <summary>入力中の検索語を検証し、変換後のバイト列を表示する。条件が変わったら直前の一致と件数を忘れる。</summary>
    private void Validate()
    {
        if (Query is null || KindChoice is null || EncodingChoice is null || Status is null || NextButton is null || CaseChoice is null)
        {
            return;
        }

        bool text = KindChoice.SelectedIndex == 1;
        EncodingChoice.Visibility = CaseChoice.Visibility = EscapeChoice.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        AlignChoice.Visibility = text && TextEncodings.SupportsAlignment(SelectedEncoding) ? Visibility.Visible : Visibility.Collapsed;
        _navigator.Reset();
        _counting?.Cancel();
        _counting = null;
        _count = null;
        CountButton.Visibility = Visibility.Collapsed;
        CountText.Visibility = Visibility.Visible;
        try
        {
            _pattern = text
                ? SearchPattern.FromText(Query.Text, TextEncodings.Get(SelectedEncoding), new TextSearchOptions
                {
                    CaseSensitive = CaseChoice.IsChecked == true,
                    UseEscapes = EscapeChoice.IsChecked == true,
                    AlignToCharacters = AlignChoice.IsChecked == true,
                })
                : SearchPattern.FromHex(Query.Text);

            // 変換後のバイト列の先頭 32 バイト。超える場合は全体の長さを添える。
            string preview = string.Join(' ', _pattern.Bytes.Take(32).Select((b, i) => _pattern.Mask is { } m && m[i] != 0xFF ? "??" : b.ToString("X2")));
            Status.Text = _pattern.Length > 32 ? Loc.Format("Find_PreviewMore", preview, _pattern.Length.ToString("N0")) : preview;
            MarkQuery(QueryState.Normal);
        }
        catch (PatternException ex)
        {
            _pattern = null;
            string message = Query.Text.Length == 0 ? string.Empty : Loc.Format("Find_Error_" + ex.Error, ex.Detail);
            Status.Text = ex.Position is int position && message.Length > 0 ? Loc.Format("Find_ErrorAt", message, position) : message;
            MarkQuery(Query.Text.Length == 0 ? QueryState.Normal : QueryState.Invalid);
        }

        NextButton.IsEnabled = PreviousButton.IsEnabled = _pattern is not null;
        UpdateCountText();
        MatchesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>不正な入力は赤枠、見つからないときは警告色の枠 (FIND-04 の仕様 6、エラー)。</summary>
    private void MarkQuery(QueryState state)
    {
        if (state == QueryState.Normal)
        {
            Query.ClearValue(Control.BorderBrushProperty);
            return;
        }

        Query.BorderBrush = (Brush)Application.Current.Resources[state == QueryState.Invalid ? "SystemFillColorCriticalBrush" : "SystemFillColorCautionBrush"];
    }

    private void Announce(string message)
    {
        if (message.Length > 0)
        {
            Microsoft.UI.Xaml.Automation.Peers.AutomationPeer? peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(Status)
                ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(Status);
            peer?.RaiseNotificationEvent(
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationKind.ActionCompleted,
                Microsoft.UI.Xaml.Automation.Peers.AutomationNotificationProcessing.ImportantMostRecent,
                message,
                "FindResult");
        }
    }

    private TextEncodingId SelectedEncoding =>
        EncodingChoice.SelectedItem is ComboBoxItem { Tag: TextEncodingId id } ? id : TextEncodingId.Ascii;

    private bool TryDecode(byte[] bytes, out string text)
    {
        try
        {
            Encoding strict = Encoding.GetEncoding(TextEncodings.Get(SelectedEncoding).CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            text = strict.GetString(bytes);
            return !text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'));
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string ToHex(byte[] bytes) => string.Join(' ', bytes.Select(b => b.ToString("X2")));

    private static string EncodingName(TextEncodingId id) => id switch
    {
        TextEncodingId.Ascii => "ASCII",
        TextEncodingId.Ansi => $"ANSI ({TextEncodings.CodePage(id)})",
        TextEncodingId.Oem => $"OEM ({TextEncodings.CodePage(id)})",
        TextEncodingId.Ebcdic => "EBCDIC (37)",
        TextEncodingId.Utf8 => "UTF-8",
        TextEncodingId.Utf16LE => "UTF-16 LE",
        TextEncodingId.Utf16BE => "UTF-16 BE",
        TextEncodingId.Utf32LE => "UTF-32 LE",
        TextEncodingId.Utf32BE => "UTF-32 BE",
        TextEncodingId.ShiftJis => "Shift_JIS",
        TextEncodingId.EucJp => "EUC-JP",
        TextEncodingId.Gb18030 => "GB18030",
        TextEncodingId.Big5 => "Big5",
        TextEncodingId.EucKr => "EUC-KR",
        _ => id.ToString(),
    };

    // ---- イベント ----

    private void Query_TextChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Option_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, KindChoice) && IsLoaded)
        {
            _kindChosen = true;
        }

        // Hex バイト列の入力は左から右に固定する。テキストの検索は表示言語の向きに従う (UI-44 の仕様 2)。
        if (Query is not null && KindChoice is not null)
        {
            Query.FlowDirection = KindChoice.SelectedIndex == 0 ? FlowDirection.LeftToRight : FlowDirection;
        }

        Validate();
    }

    private void Check_Changed(object sender, RoutedEventArgs e) => Validate();

    private void OptionsToggle_Click(object sender, RoutedEventArgs e) =>
        OptionsPanel.Visibility = OptionsToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 検索欄のキー (00-overview 8.4): Enter / Shift+Enter で検索 (方向のオプションに従う)、Esc で実行中の検索・数え上げの
    /// 取り消し、なければ閉じる。
    /// </summary>
    private async void Query_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                bool forward = DirectionChoice.SelectedIndex == 0;
                await FindAsync(shift ? !forward : forward);
                StartCountIfAutomatic();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (_running is not null || _counting is not null)
                {
                    _running?.Cancel();
                    _counting?.Cancel();
                }
                else
                {
                    Close();
                }

                break;
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        await FindAsync(forward: true);
        StartCountIfAutomatic();
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        await FindAsync(forward: false);
        StartCountIfAutomatic();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
