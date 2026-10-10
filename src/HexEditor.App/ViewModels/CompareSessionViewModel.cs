using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Compare;
using CompareOptions = HexEditor.Core.Compare.CompareOptions;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>比較対象の種類 (ANA-01 の仕様 1)。プロセスのスナップショット (ANA-09) と物理ディスクはそれぞれの担当がドキュメントとして開く。</summary>
public enum CompareSourceKind
{
    /// <summary>開いているドキュメント (タブ)。</summary>
    Document,

    /// <summary>ディスク上のファイル (タブとしては開かず、比較タブの中だけで読み取り専用で開く)。</summary>
    File,

    /// <summary>開いているドキュメントの、ディスク上に保存されている内容 (ANA-08)。</summary>
    Saved,

    /// <summary>
    /// 比較タブの中だけで読み取り専用で開く内容 (<see cref="CompareTargetSpec.Open"/> が作る)。履歴パネルの「2 つの時点を比較」(EDIT-20 の仕様 5)、
    /// プロセスのスナップショット (ANA-09) など。
    /// </summary>
    Content,
}

/// <summary>比較の片側の指定 (ANA-01 の仕様 1・2)。<see cref="Length"/> が null なら末尾まで。再比較 (仕様 8) に使う。</summary>
public sealed record CompareTargetSpec(CompareSourceKind Kind, DocumentViewModel? Document, string? Path, long Start, long? Length)
{
    /// <summary><see cref="CompareSourceKind.Content"/> の内容を開く (比較タブを閉じるときに閉じる)。</summary>
    public Func<HexEditor.Core.Engine.Document>? Open { get; init; }

    /// <summary><see cref="CompareSourceKind.Content"/> の表示名 (「時点 3」など)。</summary>
    public string? Name { get; init; }
}

/// <summary>
/// 比較タブの片側。比較タブの Hex ビューは、通常のタブとは別のカーソル・スクロール位置 (<see cref="EditorState"/>) で同じドキュメントを
/// 表示する (仕様 2: ドキュメントそのものを表示し、編集は通常のタブと同じく Undo できる)。ディスク上のファイル・保存済みの内容は
/// 比較タブが読み取り専用で開き、比較タブを閉じるときに閉じる。
/// </summary>
public sealed class CompareSideViewModel : IDisposable
{
    internal CompareSideViewModel(bool right, DocumentViewModel view, DocumentViewModel? owner, bool ownsDocument, string name)
    {
        IsRight = right;
        View = view;
        Owner = owner;
        OwnsDocument = ownsDocument;
        Name = name;
    }

    public bool IsRight { get; }

    /// <summary>この側の表示 (ドキュメントは通常のタブと共有し、カーソルなどは別)。ステータスバーの値もこれから作る。</summary>
    public DocumentViewModel View { get; }

    /// <summary>この側のドキュメントのタブ (ディスク上のファイル・保存済みの内容なら null)。</summary>
    public DocumentViewModel? Owner { get; }

    public bool OwnsDocument { get; }

    public Document Document => View.Document;

    public EditorState Editor => View.Editor;

    /// <summary>タブ名と見出しに出す名前。保存済みの内容は「名前 (保存済み)」(ANA-08 の「画面」)。</summary>
    public string Name { get; }

    /// <summary>この側のドキュメントのタブが閉じられた (比較を中止した。ANA-04 の「エラー」)。</summary>
    public bool IsClosed { get; internal set; }

    public void Dispose()
    {
        if (OwnsDocument)
        {
            View.Dispose();
        }
        else
        {
            // 開いているドキュメントを表示していた側: ドキュメントは閉じず、比較タブのビューの購読だけを外す (外さないとドキュメントを閉じるまで残る)。
            View.DetachFromDocument();
        }
    }
}

/// <summary>差分の一覧の種類の絞り込み (ANA-06 の仕様 3)。</summary>
[Flags]
public enum DiffKindFilter
{
    None = 0,
    Changed = 1,
    Inserted = 2,
    Deleted = 4,
    Unreadable = 8,
    All = Changed | Inserted | Deleted | Unreadable,
}

/// <summary>差分の一覧の並べ替え (ANA-06 の仕様 4)。</summary>
public enum DiffSortOrder
{
    /// <summary>番号 (= オフセット順)。</summary>
    Number,

    /// <summary>長さの長い順。</summary>
    LengthDescending,

    /// <summary>長さの短い順。</summary>
    LengthAscending,
}

/// <summary>
/// 比較タブ 1 つ (ANA-01〜ANA-08)。比較の実行・再比較、比較の結果、左右の対応付けと同期スクロール (ANA-04 の仕様 4)、差分の間の移動
/// (ANA-05)、差分の一覧の絞り込みと並べ替え (ANA-06)、マージ (ANA-07) を持つ。画面 (CompareView・差分の一覧のパネル) はこれを表示する。
/// UI スレッドで使う。
/// </summary>
public sealed partial class CompareSessionViewModel : ObservableObject, IDisposable
{
    private readonly OperationCenter _operations;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refresh;
    private readonly Dictionary<EditorState, (long Cursor, long Top, long SelStart, long SelLength)> _last = [];
    private CancellationTokenSource? _running;
    private Task _runTask = Task.CompletedTask;
    private bool _syncing;
    private bool _merging;
    private long[]? _listView;
    private CancellationTokenSource? _listCts;

    internal CompareSessionViewModel(int number, CompareSideViewModel left, CompareSideViewModel right, CompareTargetSpec leftSpec,
        CompareTargetSpec rightSpec, CompareOptions options, OperationCenter operations, Microsoft.UI.Dispatching.DispatcherQueue queue)
    {
        Number = number;
        Left = left;
        Right = right;
        LeftSpec = leftSpec;
        RightSpec = rightSpec;
        Options = options;
        _operations = operations;
        _queue = queue;
        _refresh = queue.CreateTimer();
        _refresh.Interval = TimeSpan.FromMilliseconds(500);
        _refresh.Tick += (_, _) => RaiseResultChanged();

        // 表示設定 (1 行のバイト数、グループ化、文字コード) は左右で共通にする (仕様 2)。右は左に合わせる。
        Right.Editor.ApplyView(Left.Editor.View);
        Right.Editor.TextEncoding = Left.Editor.TextEncoding;

        foreach (CompareSideViewModel side in new[] { Left, Right })
        {
            side.Editor.Changed += Editor_Changed;
            side.Document.Changed += Document_Changed;
            Remember(side.Editor);
        }
    }

    public int Number { get; }

    /// <summary>ページのタブの ID。</summary>
    public string Id => "compare:" + Number.ToString(CultureInfo.InvariantCulture);

    public CompareSideViewModel Left { get; }

    public CompareSideViewModel Right { get; }

    public CompareTargetSpec LeftSpec { get; private set; }

    public CompareTargetSpec RightSpec { get; private set; }

    /// <summary>タブ名: 「左の名前 ↔ 右の名前」(ANA-01 の仕様 5)。</summary>
    public string Title => $"{Left.Name} ↔ {Right.Name}";

    [ObservableProperty]
    public partial CompareOptions Options { get; set; }

    /// <summary>今の比較の結果 (比較中は途中の結果)。</summary>
    public CompareResult? Result { get; private set; }

    /// <summary>ピースツリーから差分を求める (保存済みの内容との比較で、ディスク上のファイルが変わっていない場合。ANA-08 の仕様 2)。</summary>
    public bool FromPieces { get; init; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>比較を始めた後にどちらかのドキュメントが編集された (「再比較」の InfoBar。ANA-04 の仕様 7)。</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    /// <summary>比較タブの上部に出す状態の文 (中止・失敗・ドキュメントが閉じられた)。なければ null。</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>同期スクロール (既定でオン。仕様 4)。</summary>
    [ObservableProperty]
    public partial bool SyncScroll { get; set; } = true;

    /// <summary>上下に並べる (表示 > 比較のレイアウト。既定は左右)。</summary>
    [ObservableProperty]
    public partial bool Stacked { get; set; }

    /// <summary>フォーカスのある側が右か (次 / 前の差分、マージの「現在の差分」はこの側のカーソルで決める)。</summary>
    public bool FocusedRight { get; set; }

    public CompareSideViewModel Focused => FocusedRight ? Right : Left;

    public CompareSideViewModel Other => FocusedRight ? Left : Right;

    /// <summary>選んでいる差分の番号 (なければ -1)。</summary>
    public long CurrentIndex { get; private set; } = -1;

    /// <summary>差分の分布 (ANA-06 の仕様 7)。比較が終わったら計算する。</summary>
    public DiffDistribution? Distribution { get; private set; }

    /// <summary>グラフの区間の数 (64〜4,096、既定 512)。</summary>
    public int Buckets { get; set; } = DiffDistribution.DefaultBuckets;

    /// <summary>結果が変わった (比較中は 0.5 秒ごと、終わったとき、マージしたとき)。</summary>
    public event EventHandler? ResultChanged;

    /// <summary>カーソル・選択・スクロールが変わった (差分マップの表示範囲、ステータスバーの更新)。</summary>
    public event EventHandler? ViewChanged;

    // ---- 比較の実行 (ANA-01 の仕様 5〜8) ----

    /// <summary>今の内容 (未保存の編集を含む) で比較する。比較中なら中止してやり直す。</summary>
    public async Task RunAsync()
    {
        if (Left.IsClosed || Right.IsClosed)
        {
            return;
        }

        _running?.Cancel();
        try
        {
            await _runTask;
        }
        catch (Exception)
        {
        }

        CompareResult? old = Result;
        DocumentSnapshot leftSnapshot = Left.Document.Current;
        DocumentSnapshot rightSnapshot = Right.Document.Current;
        CompareRange leftRange = RangeOf(LeftSpec, leftSnapshot);
        CompareRange rightRange = RangeOf(RightSpec, rightSnapshot);
        CompareOptions options = Options;
        var result = new CompareResult(FromPieces ? CompareMethod.InsertDelete : options.Method, leftRange, rightRange);
        Result = result;
        CurrentIndex = -1;
        Distribution = null;
        _listView = null;
        IsStale = false;
        StatusMessage = null;
        old?.Dispose();

        if (FromPieces)
        {
            DataComparer.FromPieces(SavedContentDiff.Compute(rightSnapshot), result);
            await FinishAsync(result);
            return;
        }

        IsRunning = true;
        RaiseResultChanged();
        _refresh.Start();
        var cts = new CancellationTokenSource();
        _running = cts;
        Task task = _operations.RunAsync(Loc.Get("Compare_OperationName"), OperationKind.ReadOnly, this,
            Math.Max(leftRange.Length, rightRange.Length), op =>
            {
                using CancellationTokenRegistration link = cts.Token.Register(op.Cancel);
                DataComparer.Run(options, result, op.CancellationToken, op.Report);
                return Task.CompletedTask;
            });
        _runTask = task;
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(Result, result))
            {
                StatusMessage = Left.IsClosed ? Loc.Get("Compare_LeftClosed") : Right.IsClosed ? Loc.Get("Compare_RightClosed")
                    : Loc.Format("Compare_StoppedAt", "0x" + result.StoppedAt.ToString("X", CultureInfo.InvariantCulture));
            }
        }
        catch (IOException)
        {
            // 一時ファイルに書けない (容量不足)。それまでの結果は残す (ANA-02 の「エラー」)。
            StatusMessage = Loc.Get("Compare_TempFull");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ObjectDisposedException)
        {
            StatusMessage = Loc.Format("Compare_Failed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_running, cts))
            {
                _running = null;
            }

            cts.Dispose();
        }

        if (ReferenceEquals(Result, result))
        {
            await FinishAsync(result);
        }
    }

    private async Task FinishAsync(CompareResult result)
    {
        IsRunning = false;
        _refresh.Stop();
        RaiseResultChanged();
        int buckets = Buckets;
        DiffDistribution distribution = await Task.Run(() => DiffDistribution.Compute(result, buckets));
        if (ReferenceEquals(Result, result))
        {
            Distribution = distribution;
            RaiseResultChanged();
        }
    }

    /// <summary>グラフの区間の数を変えて分布を計算し直す (64〜4,096。ANA-06 の仕様 7)。</summary>
    public async Task SetBucketsAsync(int buckets)
    {
        Buckets = Math.Clamp(buckets, DiffDistribution.MinBuckets, DiffDistribution.MaxBuckets);
        if (Result is { State: not CompareState.Running } r)
        {
            DiffDistribution distribution = await Task.Run(() => DiffDistribution.Compute(r, Buckets));
            if (ReferenceEquals(Result, r))
            {
                Distribution = distribution;
                RaiseResultChanged();
            }
        }
    }

    /// <summary>実行中の比較を中止する (結果は中止した位置まで残る)。</summary>
    public void Cancel() => _running?.Cancel();

    /// <summary>方式を替えて比較し直す (比較タブのツールバーの方式の切り替え。ANA-02・ANA-03 の「呼び出し」)。</summary>
    public Task RecompareWithAsync(CompareOptions options)
    {
        Options = options;
        return RunAsync();
    }

    /// <summary>指定の範囲 (開始・長さ) で比較の範囲を作る。範囲がデータを越える分は切り詰める。</summary>
    private static CompareRange RangeOf(CompareTargetSpec spec, DocumentSnapshot snapshot)
    {
        long start = Math.Clamp(spec.Start, 0, snapshot.Length);
        long length = Math.Clamp(spec.Length ?? long.MaxValue, 0, snapshot.Length - start);
        return new CompareRange(CompareData.FromSnapshot(snapshot), start, length);
    }

    private void RaiseResultChanged()
    {
        ResultChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>片側のドキュメントのタブが閉じられた (比較を中止する。ANA-04 の「エラー」)。</summary>
    public void SideClosed(CompareSideViewModel side)
    {
        side.IsClosed = true;
        side.Editor.Changed -= Editor_Changed;
        side.Document.Changed -= Document_Changed;
        _running?.Cancel();
        StatusMessage = Loc.Get(side.IsRight ? "Compare_RightClosed" : "Compare_LeftClosed");
    }

    private void Document_Changed(object? sender, DocumentChangedEventArgs e)
    {
        // マージ (ANA-07) は対応付けを更新するので古くならない (仕様 5)。
        if (!_merging && Result is not null)
        {
            _queue.TryEnqueue(() => IsStale = true);
        }
    }

    // ---- 同期スクロール (ANA-04 の仕様 4) ----

    private void Remember(EditorState editor) =>
        _last[editor] = (editor.Cursor, editor.TopRow, editor.SelectionStart, editor.SelectionLength);

    private void Editor_Changed(object? sender, EventArgs e)
    {
        if (sender is not EditorState driver)
        {
            return;
        }

        (long cursor, long top, long selStart, long selLength) = _last.GetValueOrDefault(driver);
        Remember(driver);
        if (!_syncing && SyncScroll && Result is not null)
        {
            bool fromRight = ReferenceEquals(driver, Right.Editor);
            CompareSideViewModel follower = fromRight ? Left : Right;
            if (!follower.IsClosed)
            {
                if (driver.Cursor != cursor || driver.SelectionStart != selStart || driver.SelectionLength != selLength)
                {
                    FollowCursor(driver, follower.Editor, fromRight);
                }
                else if (driver.TopRow != top)
                {
                    FollowScroll(driver, follower.Editor, driver.TopRow - top);
                }
            }
        }

        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>もう一方のカーソルを対応する位置に置き、カーソルの行を同じ高さに並べる。</summary>
    private void FollowCursor(EditorState driver, EditorState follower, bool fromRight)
    {
        long mapped = Math.Clamp(DiffNavigation.Map(Result!, fromRight, driver.Cursor), 0, follower.Layout.MaxCursor);
        long rowOnScreen = driver.Layout.RowOf(driver.Cursor) - driver.TopRow;
        Sync(() => follower.FollowTo(mapped, follower.Layout.RowOf(mapped) - rowOnScreen));
    }

    /// <summary>
    /// スクロールだけが変わった: もう一方も同じ行数だけスクロールする (カーソルで合わせた左右の行の対応を保つ。対応する位置が同じ高さのまま)。
    /// </summary>
    private void FollowScroll(EditorState driver, EditorState follower, long rows) =>
        Sync(() => follower.ScrollToRow(follower.TopRow + rows));

    private void Sync(Action action)
    {
        _syncing = true;
        try
        {
            action();
        }
        finally
        {
            _syncing = false;
        }

        Remember(Left.Editor);
        Remember(Right.Editor);
    }

    /// <summary>同期スクロールをオンに戻した: フォーカスのある側に合わせる (仕様 4)。</summary>
    partial void OnSyncScrollChanged(bool value)
    {
        if (value && Result is not null && !Other.IsClosed)
        {
            FollowCursor(Focused.Editor, Other.Editor, FocusedRight);
        }
    }

    // ---- 差分の間の移動 (ANA-05) ----

    /// <summary>次 / 前の差分へ移動する。差分がなければ null、末尾から先頭 (先頭から末尾) に戻ったら Wrapped。</summary>
    public DiffStep? Move(bool next)
    {
        if (Result is null)
        {
            return null;
        }

        EditorState editor = Focused.Editor;
        DiffStep? step = next
            ? DiffNavigation.Next(Result.Diffs, FocusedRight, editor.Cursor, CurrentIndex)
            : DiffNavigation.Previous(Result.Diffs, FocusedRight, editor.Cursor);
        if (step is { } s)
        {
            SelectDiff(s.Index);
        }

        return step;
    }

    /// <summary>
    /// 差分を左右とも選択する (仕様 1)。フォーカスのある側はジャンプ履歴に記録して移動し (仕様 5)、もう一方は差分の先頭の行を同じ高さに置く。
    /// </summary>
    public void SelectDiff(long index)
    {
        if (Result is null || index < 0 || index >= Result.Diffs.Count)
        {
            return;
        }

        DiffRange d = Result.Diffs[index];
        CurrentIndex = index;
        bool right = FocusedRight;
        EditorState focused = Focused.Editor;
        EditorState other = Other.Editor;
        Sync(() =>
        {
            focused.SelectMatch(d.Start(right), d.Length(right));
            if (!Other.IsClosed)
            {
                long rowOnScreen = focused.Layout.RowOf(d.Start(right)) - focused.TopRow;
                long start = Math.Min(d.Start(!right), other.Layout.MaxCursor);
                other.FollowSelection(start, d.Length(!right), other.Layout.RowOf(start) - rowOnScreen);
            }
        });
        OnPropertyChanged(nameof(StatusText));
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>差分マップのクリック (仕様 5): 片側の比較範囲のうち <paramref name="fraction"/> の位置へ移動する。</summary>
    public void JumpToFraction(bool right, double fraction)
    {
        if (Result is null)
        {
            return;
        }

        CompareSideViewModel side = right ? Right : Left;
        CompareRange range = right ? Result.Right : Result.Left;
        FocusedRight = right;
        long offset = range.Start + (long)Math.Floor(Math.Clamp(fraction, 0, 1) * Math.Max(0, range.Length - 1));
        side.Editor.GoTo(offset);
    }

    /// <summary>グラフの棒のクリック (ANA-06 の仕様 7): その区間の最初の差分へ移動する。差分がなければ false。</summary>
    public bool JumpToBucket(int bucket)
    {
        if (Result is null || Distribution is not { } distribution || bucket < 0 || bucket >= distribution.Count)
        {
            return false;
        }

        if (distribution.FirstDiffIn(Result.Diffs, bucket) is not { } index)
        {
            return false;
        }

        FocusedRight = false;
        SelectDiff(index);
        return true;
    }

    // ---- 状態の表示 (ANA-04 の仕様 6・8) ----

    /// <summary>ステータスバーの文: 「差分 12 / 340、異なるバイト 1,234、一致率 99.50%」。</summary>
    public string StatusText
    {
        get
        {
            if (Result is not { } r)
            {
                return string.Empty;
            }

            CultureInfo culture = CultureInfo.CurrentCulture;
            long count = r.Diffs.Count;
            string position = CurrentIndex >= 0 && CurrentIndex < count
                ? Loc.Format("Compare_Status_Position", (CurrentIndex + 1).ToString("N0", culture), count.ToString("N0", culture))
                : Loc.Format("Compare_Status_Count", count.ToString("N0", culture));
            return Loc.Format("Compare_Status", position, r.DifferentBytes.ToString("N0", culture), r.MatchPercent.ToString("N2", culture));
        }
    }

    /// <summary>差分の種類の表示名。</summary>
    public static string KindName(DiffKind kind) => Loc.Get(kind switch
    {
        DiffKind.Changed => "Compare_Kind_Changed",
        DiffKind.Inserted => "Compare_Kind_Inserted",
        DiffKind.Deleted => "Compare_Kind_Deleted",
        _ => "Compare_Kind_Unreadable",
    });

    /// <summary>
    /// カーソル位置のバイトの状態 (読み上げと UI オートメーション。仕様 8): 差分の種類 (変更 / 挿入 / 削除 / 一致) と相手側の値。
    /// </summary>
    public IReadOnlyList<string>? CellStates(bool right, long offset)
    {
        if (Result is not { } r)
        {
            return null;
        }

        var states = new List<string>();
        if (DiffNavigation.At(r.Diffs, right, offset) is { } at)
        {
            states.Add(Loc.Format("Compare_State_Kind", KindName(at.Diff.Kind)));
        }
        else
        {
            states.Add(Loc.Format("Compare_State_Kind", Loc.Get("Compare_Kind_Same")));
        }

        CompareSideViewModel other = right ? Left : Right;
        long mapped = DiffNavigation.Map(r, right, offset);
        if (!other.IsClosed && mapped >= 0 && mapped < other.Document.Length
            && !(DiffNavigation.At(r.Diffs, right, offset) is { Diff.Kind: DiffKind.Inserted or DiffKind.Deleted }))
        {
            byte[] one = new byte[1];
            var state = new ByteState[1];
            other.Document.Current.ReadForDisplay(mapped, one, state);
            if (state[0] == ByteState.Valid)
            {
                states.Add(Loc.Format("Compare_State_Other", one[0].ToString("X2", CultureInfo.InvariantCulture)));
            }
        }

        return states;
    }

    /// <summary>要約 (ANA-06 の仕様 6) の行。比較方式、比較したバイト数、種類別の件数、異なるバイト数、一致率、時間、打ち切ったウィンドウ。</summary>
    public IReadOnlyList<(string Label, string Value, string Id)> Summary
    {
        get
        {
            if (Result is not { } r)
            {
                return [];
            }

            CultureInfo culture = CultureInfo.CurrentCulture;
            string method = r.Method == CompareMethod.Simple ? Loc.Get("Compare_Method_Simple") : Loc.Get("Compare_Method_InsertDeleteApprox");
            var rows = new List<(string, string, string)>
            {
                (Loc.Get("Compare_Summary_Method"), method, "method"),
                (Loc.Get("Compare_Summary_Bytes"), Loc.Format("Compare_Summary_BytesValue", r.Left.Length.ToString("N0", culture), r.Right.Length.ToString("N0", culture)), "bytes"),
                (Loc.Get("Compare_Summary_Counts"), Loc.Format("Compare_Summary_CountsValue",
                    r.CountOf(DiffKind.Changed).ToString("N0", culture), r.CountOf(DiffKind.Inserted).ToString("N0", culture),
                    r.CountOf(DiffKind.Deleted).ToString("N0", culture), r.CountOf(DiffKind.Unreadable).ToString("N0", culture)), "counts"),
                (Loc.Get("Compare_Summary_Different"), r.DifferentBytes.ToString("N0", culture), "different"),
                (Loc.Get("Compare_Summary_MatchRate"), r.MatchPercent.ToString("N2", culture) + "%", "matchRate"),
                (Loc.Get("Compare_Summary_Elapsed"), IsRunning ? Loc.Get("Compare_Summary_Running") : r.Elapsed.TotalSeconds.ToString("N2", culture), "elapsed"),
            };
            if (r.Method == CompareMethod.InsertDelete)
            {
                rows.Add((Loc.Get("Compare_Summary_Aborted"), r.AbortedWindows.ToString("N0", culture), "aborted"));
            }

            return rows;
        }
    }

    // ---- 差分の一覧の絞り込みと並べ替え (ANA-06 の仕様 3・4) ----

    public DiffKindFilter KindFilter { get; private set; } = DiffKindFilter.All;

    public long MinLength { get; private set; }

    public DiffSortOrder SortOrder { get; private set; } = DiffSortOrder.Number;

    /// <summary>一覧の並べ替え・絞り込みの計算中 (100 万件を超える場合はバックグラウンドで行う)。</summary>
    public bool IsListBusy { get; private set; }

    /// <summary>一覧の行数。</summary>
    public long ListCount => _listView?.LongLength ?? Result?.Diffs.Count ?? 0;

    /// <summary>一覧の行 <paramref name="row"/> の差分の番号。</summary>
    public long ListIndex(long row) => _listView is { } view ? view[row] : row;

    /// <summary>一覧の中で差分の番号 <paramref name="index"/> の行 (なければ -1)。</summary>
    public long RowOf(long index)
    {
        if (_listView is not { } view)
        {
            return index;
        }

        for (long i = 0; i < view.LongLength; i++)
        {
            if (view[i] == index)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>絞り込みと並べ替えを変える。件数が多い場合はバックグラウンドで作り、できたら <see cref="ResultChanged"/>。</summary>
    public async Task SetListViewAsync(DiffKindFilter kinds, long minLength, DiffSortOrder order)
    {
        KindFilter = kinds;
        MinLength = Math.Max(0, minLength);
        SortOrder = order;
        await RebuildListAsync();
    }

    private async Task RebuildListAsync()
    {
        _listCts?.Cancel();
        if (Result is not { } r || (KindFilter == DiffKindFilter.All && MinLength == 0 && SortOrder == DiffSortOrder.Number))
        {
            _listView = null;
            RaiseResultChanged();
            return;
        }

        var cts = new CancellationTokenSource();
        _listCts = cts;
        DiffKindFilter kinds = KindFilter;
        long min = MinLength;
        DiffSortOrder order = SortOrder;
        IsListBusy = r.Diffs.Count > 1_000_000;
        RaiseResultChanged();
        long[] view = await Task.Run(() =>
        {
            var list = new List<long>();
            var lengths = new List<long>();
            long count = r.Diffs.Count;
            for (long i = 0; i < count; i++)
            {
                if ((i & 0xFFFF) == 0)
                {
                    cts.Token.ThrowIfCancellationRequested();
                }

                DiffRange d = r.Diffs[i];
                if (((int)kinds & (1 << (int)d.Kind)) != 0 && d.MaxLength >= min)
                {
                    list.Add(i);
                    lengths.Add(d.MaxLength);
                }
            }

            long[] indices = [.. list];
            if (order != DiffSortOrder.Number)
            {
                long[] keys = [.. lengths];
                Array.Sort(keys, indices);
                if (order == DiffSortOrder.LengthDescending)
                {
                    Array.Reverse(indices);
                }
            }

            return indices;
        }, cts.Token).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null!, TaskScheduler.Default);
        if (cts.IsCancellationRequested || view is null || !ReferenceEquals(Result, r))
        {
            return;
        }

        _listView = view;
        IsListBusy = false;
        RaiseResultChanged();
    }

    // ---- マージ (ANA-07) ----

    /// <summary>書き込み先が読み取り専用なら理由 (メニューのツールチップ。仕様 6・「エラー」)、書き込めるなら null。</summary>
    public string? CopyBlockedReason(MergeDirection direction)
    {
        CompareSideViewModel target = direction == MergeDirection.ToRight ? Right : Left;
        if (Result is null || IsRunning)
        {
            return Loc.Get("Compare_NoResult");
        }

        if (target.IsClosed)
        {
            return Loc.Get(target.IsRight ? "Compare_RightClosed" : "Compare_LeftClosed");
        }

        return target.Document.IsReadOnly || target.OwnsDocument
            ? Loc.Get(direction == MergeDirection.ToRight ? "Compare_RightReadOnly" : "Compare_LeftReadOnly")
            : null;
    }

    /// <summary>
    /// 差分をコピーする。<paramref name="indices"/> が null なら現在の差分 (カーソルのある差分)。結果は (コピーした数、長さが変わるため
    /// 飛ばした数)。1 万件を超える場合は長時間処理として処理センターに出し、キャンセルできる (仕様の「巨大ファイル」)。
    /// </summary>
    public async Task<MergeOutcome?> CopyAsync(MergeDirection direction, IReadOnlyCollection<long>? indices, bool all = false)
    {
        if (Result is not { } r || CopyBlockedReason(direction) is not null)
        {
            return null;
        }

        IReadOnlyCollection<long> targets;
        if (all)
        {
            targets = [];
        }
        else if (indices is not null)
        {
            targets = indices;
        }
        else if (CurrentDiffAtCursor() is { } current)
        {
            targets = [current];
        }
        else
        {
            return new MergeOutcome(0, 0);
        }

        _merging = true;
        try
        {
            Document? left = Left.OwnsDocument ? null : Left.Document;
            Document? right = Right.OwnsDocument ? null : Right.Document;
            long count = all ? r.Diffs.Count : targets.Count;
            MergeOutcome outcome;
            if (count > DiffMerger.LongRunningThreshold)
            {
                // UI スレッドで編集する (ドキュメントの変更の通知は UI スレッドで受ける)。処理センターには進捗とキャンセルだけを出す。
                var done = new TaskCompletionSource<MergeOutcome>();
                await _operations.RunAsync(Loc.Get("Compare_MergeOperationName"), OperationKind.ReadOnly, this, null, async op =>
                {
                    _queue.TryEnqueue(async () =>
                    {
                        try
                        {
                            done.SetResult(all
                                ? await DiffMerger.CopyAllAsync(r, left, right, direction, op.CancellationToken, () => Task.Delay(1))
                                : await DiffMerger.CopyAsync(r, left, right, targets, direction, op.CancellationToken, () => Task.Delay(1)));
                        }
                        catch (Exception ex)
                        {
                            done.SetException(ex);
                        }
                    });
                    await done.Task;
                });
                outcome = await done.Task;
            }
            else
            {
                outcome = all
                    ? await DiffMerger.CopyAllAsync(r, left, right, direction)
                    : await DiffMerger.CopyAsync(r, left, right, targets, direction);
            }

            CurrentIndex = -1;
            _listView = null;
            await FinishAsync(r);
            await RebuildListAsync();
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _merging = false;
        }
    }

    /// <summary>フォーカスのある側のカーソルがある差分 (選んでいる差分を優先)。</summary>
    public long? CurrentDiffAtCursor()
    {
        if (Result is not { } r)
        {
            return null;
        }

        if (CurrentIndex >= 0 && CurrentIndex < r.Diffs.Count)
        {
            return CurrentIndex;
        }

        return DiffNavigation.At(r.Diffs, FocusedRight, Focused.Editor.Cursor)?.Index;
    }

    public void Dispose()
    {
        _running?.Cancel();
        _listCts?.Cancel();
        _refresh.Stop();
        foreach (CompareSideViewModel side in new[] { Left, Right })
        {
            side.Editor.Changed -= Editor_Changed;
            side.Document.Changed -= Document_Changed;
            side.Dispose();
        }

        Result?.Dispose();
    }
}
