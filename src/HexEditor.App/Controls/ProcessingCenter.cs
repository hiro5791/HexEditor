using HexEditor.App.Services;
using HexEditor.Core.Operations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// 処理センター (UI-37): 実行中の処理と直近に完了した処理 (最大 20 件) の一覧。進捗率・処理速度・残り時間・経過時間・
/// キャンセルを表示する。開始から 0.5 秒以内に終わった処理は出さない。表示の更新は 1 秒に 4 回まで (呼び出し側のタイマー)。
/// </summary>
public sealed partial class ProcessingCenter : UserControl
{
    public const int MaxCompleted = 20;

    private readonly StackPanel _list = new() { Spacing = 12 };
    private readonly TextBlock _empty = new();
    private readonly Button _clear = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private readonly HashSet<LongRunningOperation> _cleared = [];

    // 行は処理ごとに作り、更新では中身だけ変える (作り直すと「キャンセル」ボタンのフォーカスが外れる)。
    private readonly Dictionary<LongRunningOperation, RowParts> _rows = [];

    public ProcessingCenter()
    {
        _empty.Text = Loc.Get("Operations_None");
        _clear.Content = Loc.Get("Operations_Clear");
        AutomationProperties.SetAutomationId(_clear, "Operations_Clear");
        _clear.Click += (_, _) =>
        {
            if (Center is not null)
            {
                foreach (LongRunningOperation op in Center.History)
                {
                    _cleared.Add(op);
                }
            }

            Refresh();
        };
        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(new TextBlock { Text = Loc.Get("Operations_Title"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 420 });
        root.Children.Add(_clear);
        Content = root;
        AutomationProperties.SetAutomationId(this, "ProcessingCenter");
    }

    public OperationCenter? Center { get; set; }

    /// <summary>文書の表示名 (処理の対象の名前を出すため)。</summary>
    public Func<object?, string?>? TargetName { get; set; }

    /// <summary>一覧を新しい状態にする。前と同じ処理の行は、作り直さずに中身だけ更新する。</summary>
    public void Refresh()
    {
        (List<LongRunningOperation> running, List<LongRunningOperation> completed) = Shown();
        var shown = new List<UIElement>();
        foreach (LongRunningOperation op in running.Concat(completed))
        {
            bool active = IsActive(op);
            if (!_rows.TryGetValue(op, out RowParts? parts) || parts.Active != active)
            {
                _rows[op] = parts = Row(op, active);
            }

            Update(parts, op);
            shown.Add(parts.Root);
        }

        foreach (LongRunningOperation gone in _rows.Keys.Where(op => !running.Contains(op) && !completed.Contains(op)).ToList())
        {
            _rows.Remove(gone);
        }

        if (shown.Count == 0)
        {
            shown.Add(_empty);
        }

        // 並びが変わったときだけ入れ替える。
        if (!_list.Children.SequenceEqual(shown))
        {
            _list.Children.Clear();
            foreach (UIElement row in shown)
            {
                _list.Children.Add(row);
            }
        }

        _clear.IsEnabled = completed.Count > 0;
    }

    private static bool IsActive(LongRunningOperation op) =>
        op.State is OperationState.Running or OperationState.Cancelling or OperationState.Pending;

    /// <summary>1 行の、更新で変わる部分。</summary>
    private sealed record RowParts(Grid Root, bool Active, ProgressBar? Bar, TextBlock? Details, Button? Cancel, TextBlock? Result);

    private static void Update(RowParts parts, LongRunningOperation op)
    {
        if (parts.Bar is { } bar)
        {
            bar.IsIndeterminate = op.Fraction is null;
            if (op.Fraction is double f)
            {
                bar.Value = f;
            }
        }

        if (parts.Details is { } details)
        {
            details.Text = OperationText.Details(op);
        }

        if (parts.Cancel is { } cancel)
        {
            cancel.IsEnabled = op.State != OperationState.Cancelling && op.CanCancel;
        }

        if (parts.Result is { } result)
        {
            result.Text = OperationText.Result(op);
        }
    }

    private RowParts Row(LongRunningOperation op, bool active)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        string? target = TargetName?.Invoke(op.Target);
        text.Children.Add(new TextBlock { Text = op.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (target is not null)
        {
            text.Children.Add(Caption(target));
        }

        if (active)
        {
            var bar = new ProgressBar { Maximum = 1 };
            text.Children.Add(bar);
            TextBlock details = Caption(string.Empty);
            AutomationProperties.SetAutomationId(details, "Operations_Details");
            text.Children.Add(details);
            var cancel = new Button { Content = Loc.Get("Common_Cancel"), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(cancel, "Operations_Cancel");
            AutomationProperties.SetName(cancel, $"{Loc.Get("Common_Cancel")}: {op.Name}");
            if (!op.CanCancel)
            {
                // 書き込みを始めたずらしながらのその場保存はキャンセルできない (ENG-24 の仕様 5)。理由をツールチップで示す。
                ToolTipService.SetToolTip(cancel, Loc.Get("Operations_CannotCancel"));
            }
            cancel.Click += (_, _) => _ = op.RequestCancelAsync();
            Grid.SetColumn(cancel, 1);
            row.Children.Add(cancel);
            row.Children.Add(text);
            return new RowParts(row, true, bar, details, cancel, null);
        }

        TextBlock result = Caption(string.Empty);
        AutomationProperties.SetAutomationId(result, "Operations_Result");
        text.Children.Add(result);
        row.Children.Add(text);
        return new RowParts(row, false, null, null, null, result);
    }

    /// <summary>
    /// 一覧に出す処理: 実行中で 0.5 秒を過ぎたもの (UI-37 の受け入れ基準 4) と、0.5 秒より長くかかった・失敗した完了済みのもの。
    /// </summary>
    internal (List<LongRunningOperation> Running, List<LongRunningOperation> Completed) Shown()
    {
        if (Center is null)
        {
            return ([], []);
        }

        var running = Center.Active.Where(op => op.ShouldShow).ToList();
        var completed = Center.History
            .Where(op => !_cleared.Contains(op) && (op.Elapsed > LongRunningOperation.ShowDelay || op.State == OperationState.Failed))
            .Take(MaxCompleted)
            .ToList();
        return (running, completed);
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
    };
}

/// <summary>処理の表示の文言 (UI-37 の仕様 3・5)。</summary>
public static class OperationText
{
    /// <summary>「45% · 1.2 GB/s · 残り約 2 分 · 経過 0:35」。分からない値は省く。</summary>
    public static string Details(LongRunningOperation op)
    {
        var parts = new List<string>();
        if (op.Fraction is double f)
        {
            parts.Add(f.ToString("P0", System.Globalization.CultureInfo.CurrentCulture));
        }

        double speed = op.BytesPerSecond;
        if (speed > 0)
        {
            parts.Add(Loc.Format("Operations_Speed", Core.View.StatusFormat.ShortSize((long)speed, System.Globalization.CultureInfo.CurrentCulture)
                ?? Core.View.StatusFormat.Number((long)speed, System.Globalization.CultureInfo.CurrentCulture) + " B"));
        }

        if (op.EstimatedRemaining is TimeSpan remaining)
        {
            parts.Add(remaining.TotalMinutes >= 1
                ? Loc.Format("Operations_RemainingMinutes", Math.Ceiling(remaining.TotalMinutes))
                : Loc.Format("Operations_RemainingSeconds", Math.Max(1, Math.Ceiling(remaining.TotalSeconds))));
        }

        // 検索の処理では、これまでに見つかった一致の数 (FIND-02 の仕様 2)。
        if (op.Matches is long matches)
        {
            parts.Add(Loc.Format("Operations_Matches", matches));
        }

        // 処理の詳細 (複数ファイル検索のファイル数・今のファイル。FIND-30 の「巨大ファイル・長時間処理」)。
        if (op.Detail is { Length: > 0 } detail)
        {
            parts.Add(detail);
        }

        parts.Add(Loc.Format("Operations_Elapsed",op.Elapsed.ToString(op.Elapsed.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")));
        if (op.State == OperationState.Cancelling)
        {
            parts.Add(Loc.Get("Operations_Cancelling"));
        }

        return string.Join(" · ", parts);
    }

    public static string Result(LongRunningOperation op) => op.State switch
    {
        OperationState.Completed => Loc.Get("Operations_Completed"),
        OperationState.Cancelled => Loc.Get("Operations_Cancelled"),
        OperationState.Failed => Loc.Format("Operations_Failed", op.Error?.Message ?? string.Empty),
        _ => string.Empty,
    };
}
