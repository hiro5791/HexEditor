using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace HexEditor.App.Controls;

/// <summary>確認ダイアログの答え。</summary>
public enum ConfirmChoice
{
    Cancel,
    Primary,
    Secondary,
}

/// <summary>確認ダイアログの内容 (すべて置換の件数の確認、末尾を超える上書きの確認)。</summary>
public sealed record ConfirmRequest(string AutomationId, string Title, string Body, string Primary, string? Secondary, string Close);

/// <summary>
/// 置換 (FIND-22〜FIND-24)。Ctrl+H で検索欄の下に置換欄を出し、1 件ずつの置換・スキップ・すべて置換を行う。
/// すべて置換はバックグラウンドで一致を探し、最後にまとめて 1 回の編集 (1 回の Undo) として適用する。
/// </summary>
public sealed partial class FindBar
{
    /// <summary>この件数を超えるすべて置換は、適用の前に確認する (FIND-23 の仕様 5)。</summary>
    public const long ReplaceAllConfirmThreshold = 1_000_000;

    private CancellationTokenSource? _replacing;
    private ReplacementTemplate? _template;
    private bool _policyChosen;

    /// <summary>確認ダイアログを出す (メインウィンドウが ContentDialog で出す)。null なら確認せずに既定の動作 (やめる) にする。</summary>
    public Func<ConfirmRequest, Task<ConfirmChoice>>? Confirm { get; set; }

    /// <summary>すべて置換が終わった (件数)。メインウィンドウが「1,234 件置換しました」の InfoBar と「元に戻す」を出す (FIND-23 の仕様 4)。</summary>
    public event EventHandler<long>? ReplaceAllCompleted;

    /// <summary>置換できなかった理由を InfoBar で知らせる。</summary>
    public event EventHandler<string>? ReplaceFailed;

    /// <summary>置換欄を出しているか。</summary>
    public bool IsReplaceMode => ReplaceRow.Visibility == Visibility.Visible;

    /// <summary>今の置換語 (無効なら null)。</summary>
    internal ReplacementTemplate? CurrentTemplate => _template;

    /// <summary>長さが違う場合の扱いの選択を強調しているか (置換語と一致の長さが違う。FIND-24 の画面)。</summary>
    internal bool PolicyHighlighted { get; private set; }

    private void SetReplaceMode(bool replace)
    {
        ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        UpdateReplaceAvailability();
        ValidateReplacement();
    }

    /// <summary>読み取り専用のドキュメントでは置換欄を無効にして理由を出す (FIND-22 の仕様 6)。</summary>
    private bool IsReadOnlyDocument => Editor is { } e && (e.ReadOnly || !e.Document.CanSave);

    /// <summary>長さが固定のドキュメントでは「長さを変える」を選べない。既定は長さを変えられるかで決める (FIND-24 の仕様 2)。</summary>
    private void UpdateReplaceAvailability()
    {
        if (!_ready || Editor is not { } editor)
        {
            return;
        }

        bool canResize = editor.Document.CanResize;
        PolicyChangeItem.IsEnabled = canResize;
        if (!_policyChosen || (!canResize && LengthPolicyChoice.SelectedIndex == 0) || LengthPolicyChoice.SelectedIndex < 0)
        {
            _suppressPolicy = true;
            LengthPolicyChoice.SelectedIndex = (int)ReplaceOptions.DefaultPolicy(canResize);
            _suppressPolicy = false;
        }
    }

    private bool _suppressPolicy;

    /// <summary>今の置換の条件。</summary>
    private ReplaceOptions CurrentReplaceOptions(OverflowMode overflow = OverflowMode.Ask) => new()
    {
        Policy = (LengthPolicy)Math.Max(0, LengthPolicyChoice.SelectedIndex),
        Filler = CurrentFiller() ?? [0x00],
        Overflow = overflow,
    };

    /// <summary>埋め草 (FIND-24 の仕様 3)。指定バイトが不正なら null。</summary>
    private byte[]? CurrentFiller() => FillerChoice.SelectedIndex switch
    {
        1 => [0xFF],
        2 => Kind == SearchKind.Text ? SearchPattern.EncodeText(" ", TextEncodings.Get(SelectedEncoding), false) : [0x20],
        3 => ParseFiller(),
        _ => [0x00],
    };

    private byte[]? ParseFiller()
    {
        try
        {
            (byte Value, bool Keep)[] bytes = SearchPattern.ParseReplacementHex(FillerCustom.Text);
            return bytes.Length is >= 1 and <= 16 && !bytes.Any(b => b.Keep) ? [.. bytes.Select(b => b.Value)] : null;
        }
        catch (PatternException)
        {
            return null;
        }
    }

    /// <summary>置換語を検証し、変換結果と、置換できない理由を表示する (FIND-22 の「エラー」、FIND-24 の「エラー」)。</summary>
    private void ValidateReplacement()
    {
        if (!_ready)
        {
            return;
        }

        FillerChoice.Visibility = Show(LengthPolicyChoice.SelectedIndex == (int)LengthPolicy.PadKeepLength);
        FillerCustom.Visibility = Show(FillerChoice.Visibility == Visibility.Visible && FillerChoice.SelectedIndex == 3);
        _template = null;
        string reason = string.Empty;
        bool readOnly = IsReadOnlyDocument;
        ReplaceQuery.IsEnabled = !readOnly;
        if (readOnly)
        {
            reason = Loc.Get("Find_ReplaceReadOnly");
            ReplacePreview.Text = string.Empty;
        }
        else
        {
            try
            {
                _template = Kind switch
                {
                    SearchKind.Text => ReplacementTemplate.FromText(ReplaceQuery.Text, TextEncodings.Get(SelectedEncoding), EscapeChoice.IsChecked == true),
                    SearchKind.Integer or SearchKind.Float when _pattern?.Numeric is { } numeric =>
                        ReplacementTemplate.FromNumeric(ReplaceQuery.Text, numeric, Editor is { } e ? new EditorExpressionContext(e) : null),
                    SearchKind.Integer or SearchKind.Float => null,
                    _ => ReplacementTemplate.FromHex(ReplaceQuery.Text),
                };
                ReplacePreview.Text = _template is null ? string.Empty : _template.IsEmpty ? Loc.Get("Find_ReplaceDeletes") : _template.Preview();
                SetBorder(ReplaceQuery, null);
            }
            catch (PatternException ex)
            {
                string message = ErrorText(ex);
                ReplacePreview.Text = ex.Position is int position ? Loc.Format("Find_ErrorAt", message, position) : message;
                SetBorder(ReplaceQuery, (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]);
            }
        }

        // 置換語と一致の長さが違うときは「長さが違う場合」を強調し、「埋めて長さを保つ」で長い場合は置換できない。
        PolicyHighlighted = false;
        if (_template is { } t && _pattern is { } p && p.MinMatchLength == p.MaxMatchLength)
        {
            int n = t.LengthFor(0);
            int m = p.MinMatchLength;
            PolicyHighlighted = n != m;
            if (n > m && LengthPolicyChoice.SelectedIndex == (int)LengthPolicy.PadKeepLength)
            {
                reason = Loc.Format("Find_ReplaceTooLong", n.ToString("N0", CultureInfo.CurrentCulture), m.ToString("N0", CultureInfo.CurrentCulture));
            }
            else if (t.IsEmpty && !(Editor?.Document.CanResize ?? true) && LengthPolicyChoice.SelectedIndex != (int)LengthPolicy.PadKeepLength)
            {
                reason = Loc.Get("Find_ReplaceCannotResize");
            }
        }

        if (LengthPolicyChoice.SelectedIndex == (int)LengthPolicy.PadKeepLength && FillerChoice.SelectedIndex == 3 && ParseFiller() is null)
        {
            reason = Loc.Get("Find_FillerInvalid");
        }

        SetBorder(LengthPolicyChoice, PolicyHighlighted ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] : null);
        ReplaceReason.Text = reason;
        bool can = !readOnly && _template is not null && _pattern is not null && reason.Length == 0 && ScopeIsValid;
        ReplaceButton.IsEnabled = ReplaceAllButton.IsEnabled = can;
        SkipButton.IsEnabled = _pattern is not null && ScopeIsValid;
        LengthPolicyChoice.IsEnabled = FillerChoice.IsEnabled = !readOnly;
    }

    private void ReplaceQuery_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_applyingHistory)
        {
            _replaceCursor.Reset();
        }

        ValidateReplacement();
    }

    private void ReplaceOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(sender, LengthPolicyChoice) && !_suppressPolicy && IsLoaded)
        {
            _policyChosen = true;
        }

        ValidateReplacement();
    }

    /// <summary>置換欄のキー: Enter で置換、↑ / ↓ で置換欄の履歴、Esc で閉じる。</summary>
    private async void ReplaceQuery_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                if (alt)
                {
                    await ReplaceAllAsync();
                }
                else
                {
                    await ReplaceAsync();
                }

                break;
            case VirtualKey.Up or VirtualKey.Down:
                e.Handled = true;
                RecallHistory(HistoryList.Replace, older: e.Key == VirtualKey.Up);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                await HandleQueryKeyAsync(VirtualKey.Escape, false);
                break;
        }
    }

    /// <summary>テスト用の命令の通り道: 置換欄の ↑ / ↓。</summary>
    internal void RecallReplacementHistory(bool older) => RecallHistory(HistoryList.Replace, older);

    /// <summary>
    /// 「置換」(FIND-22 の仕様 3): 現在の選択範囲が一致なら置換して次の一致に移る。一致でなければ置換せずに次の一致に移る。
    /// 1 回の置換は 1 回の Undo で戻せる。
    /// </summary>
    public async Task ReplaceAsync()
    {
        if (Editor is not { } editor || _pattern is not { } pattern || _template is not { } template || IsReadOnlyDocument)
        {
            return;
        }

        if (_navigator.IsLastMatch(editor.SelectionStart, editor.SelectionLength))
        {
            AddReplacementToHistory();
            ReplaceOneResult result;
            try
            {
                result = Replacer.ReplaceAt(editor.Document, pattern, editor.SelectionStart, template, CurrentReplaceOptions(), Loc.Get("History_Replace"));
                if (result.Issue == ReplaceIssue.ExceedsEnd && await AskOverflowAsync(editor.Document.CanResize) is { } overflow)
                {
                    result = Replacer.ReplaceAt(editor.Document, pattern, editor.SelectionStart, template, CurrentReplaceOptions(overflow), Loc.Get("History_Replace"));
                }
            }
            catch (Exception ex) when (ex is DocumentLockedException or FixedLengthException)
            {
                ReplaceFailed?.Invoke(this, ex is FixedLengthException ? Loc.Get("Find_ReplaceCannotResize") : Loc.Get("Notice_Busy"));
                return;
            }

            if (result.Applied)
            {
                // 置き換えた範囲の後ろから次の一致を探す (置換語の中を探し直さない)。
                _navigator.Reset();
                editor.GoTo(result.Offset + result.InsertedLength);
            }
            else if (result.Issue != ReplaceIssue.NoLongerMatches)
            {
                ReplaceFailed?.Invoke(this, IssueText(result.Issue, template, pattern));
                return;
            }
        }

        await FindAsync(forward: true);
    }

    /// <summary>「スキップ」(FIND-22 の仕様 4): 置換せずに次の一致に移る。</summary>
    public Task SkipAsync() => FindAsync(forward: true);

    /// <summary>
    /// 「すべて置換」(FIND-23)。範囲のすべての一致を、重ならないように前から順に置換する。検索中にキャンセルした場合は何も置換しない。
    /// 100 万件を超える場合は適用の前に確認する。適用中はドキュメントの編集を止める (長時間処理の「ドキュメントを変更する」)。
    /// </summary>
    public async Task ReplaceAllAsync()
    {
        if (Editor is not { } editor || _pattern is not { } pattern || _template is not { } template || IsReadOnlyDocument || !ScopeIsValid)
        {
            return;
        }

        AddReplacementToHistory();
        StopIncremental();
        Document doc = editor.Document;
        _replacing?.Cancel();
        var cts = new CancellationTokenSource();
        _replacing = cts;
        SearchScope scope = CurrentScope;
        ReplaceOptions options = CurrentReplaceOptions();
        Status.Text = Loc.Get("Find_Replacing");
        StartProgress();
        AppLog.Info("Replace all: start");
        long count = -1;
        try
        {
            async Task<long> Work(LongRunningOperation? op)
            {
                CancellationToken token = op?.CancellationToken ?? cts.Token;
                if (op is not null)
                {
                    cts.Token.Register(op.Cancel);
                    _activeOperation = op;
                }

                // 1. 一致を探す (編集を止めたあとの状態に対して)。
                DocumentSnapshot snapshot = doc.Current;
                using SearchResults found = Replacer.FindForReplaceAll(snapshot, pattern, new SearchOptions { Scope = scope }, op, token);
                op?.ReportMatches(found.LongCount);
                if (found.LongCount == 0)
                {
                    return 0;
                }

                // 2. 置換できない一致 (長すぎる、末尾を超える) を先に見つける。
                ReplaceOptions effective = options;
                (ReplaceIssue issue, _) = Replacer.Check(found, template, effective, snapshot.Length, doc.CanResize);
                if (issue == ReplaceIssue.ExceedsEnd)
                {
                    OverflowMode? overflow = await OnUiAsync(() => AskOverflowAsync(doc.CanResize));
                    if (overflow is null)
                    {
                        return -1;
                    }

                    effective = options with { Overflow = overflow.Value };
                    (issue, _) = Replacer.Check(found, template, effective, snapshot.Length, doc.CanResize);
                }

                if (issue != ReplaceIssue.None)
                {
                    throw new ReplaceException(issue, 0, pattern.MinMatchLength, template.LengthFor(0));
                }

                // 3. 100 万件を超える場合は確認する (FIND-23 の仕様 5)。
                if (found.LongCount > ReplaceAllConfirmThreshold)
                {
                    ConfirmChoice choice = await OnUiAsync(() => Confirm?.Invoke(new ConfirmRequest(
                        "ReplaceAllConfirm",
                        Loc.Get("Find_ReplaceAllConfirm_Title"),
                        Loc.Format("Find_ReplaceAllConfirm_Body", found.LongCount.ToString("N0", CultureInfo.CurrentCulture)),
                        Loc.Get("Find_ReplaceAllConfirm_Replace"),
                        null,
                        Loc.Get("Find_ReplaceAllConfirm_Cancel"))) ?? Task.FromResult(ConfirmChoice.Cancel));
                    if (choice != ConfirmChoice.Primary)
                    {
                        return -1;
                    }
                }

                // 4. 適用: 新しい木をバックグラウンドで作り、UI のスレッドで 1 回の編集として入れる。
                token.ThrowIfCancellationRequested();
                PreparedReplacement prepared = doc.PrepareReplacements(
                    Replacer.PlanAll(snapshot, found, template, effective, doc.CanResize), op, ignoreEditLock: true, token);
                await OnUiAsync(() =>
                {
                    doc.CommitReplacements(prepared, Loc.Get("History_ReplaceAll"), ignoreEditLock: true);
                    return Task.FromResult(true);
                });
                return prepared.Count;
            }

            count = Operations is null
                ? await Work(null)
                : await Operations.RunAsync(Loc.Get("Operation_ReplaceAll"), OperationKind.ModifiesDocument, doc, CurrentScope.TotalLength(doc.Length),
                    op => Work(op), locked => doc.SetEditLock(locked));
            AppLog.Info($"Replace all: end ({count})");
            if (count > 0)
            {
                _navigator.Reset();
                Status.Text = Loc.Format("Find_ReplacedCount", count.ToString("N0", CultureInfo.CurrentCulture));
                Announce(Status.Text);
                ReplaceAllCompleted?.Invoke(this, count);
            }
            else if (count == 0)
            {
                Status.Text = Loc.Get("Find_NotFound");
                MarkQuery(QueryState.NotFound);
                Announce(Status.Text);
            }
            else
            {
                Status.Text = Loc.Get("Find_ReplaceAllCancelled");
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Replace all: cancelled");
            Status.Text = Loc.Get("Find_ReplaceAllCancelled");
        }
        catch (ReplaceException ex)
        {
            ReplaceFailed?.Invoke(this, IssueText(ex.Issue, template, pattern));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            AppLog.Info($"Replace all: failed ({ex.Message})");
            ReplaceFailed?.Invoke(this, Loc.Format("Find_ReplaceAllFailed", ex.Message));
        }
        finally
        {
            if (_replacing == cts)
            {
                _replacing = null;
                UpdateProgress();
            }

            ValidateReplacement();
        }
    }

    /// <summary>
    /// 「後ろを上書きする」で置換語がドキュメントの末尾を超える場合に、超える分を書かないか末尾に追加するかを選ばせる (FIND-24 の仕様 5)。
    /// 長さが固定のドキュメントでは「書かない」だけを選べる。やめたら null。
    /// </summary>
    private async Task<OverflowMode?> AskOverflowAsync(bool canResize)
    {
        if (Confirm is null)
        {
            return null;
        }

        ConfirmChoice choice = await Confirm(new ConfirmRequest(
            "ReplaceOverflowConfirm",
            Loc.Get("Find_Overflow_Title"),
            Loc.Get(canResize ? "Find_Overflow_Body" : "Find_Overflow_BodyFixed"),
            Loc.Get("Find_Overflow_Truncate"),
            canResize ? Loc.Get("Find_Overflow_Append") : null,
            Loc.Get("Common_Cancel")));
        return choice switch
        {
            ConfirmChoice.Primary => OverflowMode.Truncate,
            ConfirmChoice.Secondary => OverflowMode.Append,
            _ => null,
        };
    }

    private static string IssueText(ReplaceIssue issue, ReplacementTemplate template, SearchPattern pattern) => issue switch
    {
        ReplaceIssue.TooLong => Loc.Format("Find_ReplaceTooLong", template.LengthFor(0).ToString("N0", CultureInfo.CurrentCulture),
            pattern.MinMatchLength.ToString("N0", CultureInfo.CurrentCulture)),
        ReplaceIssue.CannotResize => Loc.Get("Find_ReplaceCannotResize"),
        _ => Loc.Get("Find_ReplaceAllCancelled"),
    };

    /// <summary>UI のスレッドで実行して結果を待つ (長時間処理の中から確認ダイアログを出す・編集を適用する)。</summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetCanceled();
        }

        return tcs.Task;
    }

    private async void Replace_Click(object sender, RoutedEventArgs e) => await ReplaceAsync();

    private async void Skip_Click(object sender, RoutedEventArgs e) => await SkipAsync();

    private async void ReplaceAll_Click(object sender, RoutedEventArgs e) => await ReplaceAllAsync();
}
