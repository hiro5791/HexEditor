using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Compare;
using HexEditor.Core.Compare.Structure;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.App.ViewModels;

/// <summary>
/// 比較タブの構造の比較 (ANA-31・ANA-32)。バイトの比較と並行して、左右に同じテンプレートを当てはめた結果をフィールドの単位で比べる。
/// 結果は構造の差分パネル (下のパネル「構造の差分」) に出す。
/// </summary>
public sealed partial class CompareSessionViewModel
{
    /// <summary>設定のキー。</summary>
    public const string StructEnabledKey = "compare.structure.enabled";

    public const string StructIncludeHiddenKey = "compare.structure.includeHidden";

    public const string StructMaxFieldsKey = "compare.structure.maxFields";

    private Task _structTask = Task.CompletedTask;
    private int _structMaxFieldsOverride;

    /// <summary>左右の今の位置の差を保つ同期スクロール (構造の差分へ移動した後。ANA-32 の仕様 4)。左 − 右。</summary>
    private long? _structDelta;

    /// <summary>構造でも比較する (比較タブのツールバーの「構造で比較」。既定は設定 <c>compare.structure.enabled</c>)。</summary>
    [ObservableProperty]
    public partial bool StructureEnabled { get; set; } = App.Settings?.GetBool(StructEnabledKey, true) ?? true;

    /// <summary>次 / 前の差分の対象を構造にする (比較タブのツールバーの「移動の対象: バイト / 構造」。既定はバイト)。</summary>
    [ObservableProperty]
    public partial bool NavigateStructure { get; set; }

    /// <summary>利用者が選んだテンプレート (「構造の比較に使うテンプレートを選ぶ」。ANA-31 の仕様 2 の 1)。</summary>
    public StructTemplateChoice? StructUserChoice { get; set; }

    /// <summary>今の構造の比較の結果 (比較中は途中の結果。テンプレートが決まらないときは理由だけを持つ結果)。</summary>
    public StructCompareResult? StructResult { get; private set; }

    /// <summary>構造の差分パネルで選んでいる行。</summary>
    public StructDiffNode? StructCurrent { get; private set; }

    /// <summary>左右のドキュメントのテンプレートの適用 (TPL-20・TPL-26。テンプレートの決め方の 2 に使う)。</summary>
    internal Func<CompareSideViewModel, IReadOnlyList<TemplateApplicationInfo>>? ApplicationsOf { get; init; }

    /// <summary>構造の比較の結果が変わった (比較中は 0.5 秒ごと、終わったとき)。</summary>
    public event EventHandler? StructResultChanged;

    /// <summary>
    /// 構造の比較が使えない理由 (プロセスメモリのスナップショットどうしなど。ANA-31 の仕様 1)。使えるなら null。
    /// </summary>
    public string? StructUnavailableReason =>
        Result is { } r && (CompareData.RegionsOf(r.Left.Data) is not null || CompareData.RegionsOf(r.Right.Data) is not null)
        || LeftSpec.Kind == CompareSourceKind.Snapshot || RightSpec.Kind == CompareSourceKind.Snapshot
            ? Loc.Get("Compare_Struct_Unsupported") : null;

    /// <summary>ツールバーのテンプレートの名前のボタンの文字。</summary>
    public string StructTemplateLabel => StructResult is { Choice.IsNone: false } r && r.TemplateName.Length > 0
        ? r.TemplateName : Loc.Get("Compare_Struct_ChooseTemplate");

    partial void OnStructureEnabledChanged(bool value) => _ = RestartStructureAsync();

    /// <summary>テンプレートと開始オフセットを選んで構造の比較をやり直す。</summary>
    public Task SetStructTemplateAsync(StructTemplateChoice? choice)
    {
        StructUserChoice = choice;
        if (!StructureEnabled)
        {
            StructureEnabled = true; // 変更の通知で比較し直す
            return _structTask;
        }

        return RestartStructureAsync();
    }

    /// <summary>「上限を上げて再比較」: フィールドの数の上限を設定の最大値まで上げてやり直す (ANA-31 の「巨大ファイル」)。</summary>
    public Task RaiseStructLimitAsync()
    {
        _structMaxFieldsOverride = StructCompareOptions.MaxMaxFields;
        return RestartStructureAsync();
    }

    /// <summary>構造の比較だけをやり直す (テンプレートの保存、対応付けの方法の変更、トグル)。</summary>
    public Task RestartStructureAsync()
    {
        if (Result is not { } bytes || Left.IsClosed || Right.IsClosed)
        {
            return Task.CompletedTask;
        }

        _structTask = RunStructureAsync(Left.Document.Current, Right.Document.Current, bytes, _runTask);
        return _structTask;
    }

    /// <summary>使っているテンプレートのファイルが保存された: 構造の比較をやり直す (ANA-31 の仕様 9)。</summary>
    public void TemplateSaved(string path)
    {
        if (StructResult is { Choice: { IsNone: false } c } && string.Equals(c.TemplatePath, path, StringComparison.OrdinalIgnoreCase))
        {
            _ = RestartStructureAsync();
        }
    }

    /// <summary>
    /// 構造の比較を始める。バイトの比較 (<paramref name="byteTask"/>) と並行して行い、両方が終わったら構造の外の差分 (仕様 8) を求める。
    /// </summary>
    private async Task RunStructureAsync(DocumentSnapshot leftSnapshot, DocumentSnapshot rightSnapshot, CompareResult bytes, Task byteTask)
    {
        StructResult?.Dispose();
        StructResult = null;
        StructCurrent = null;
        RaiseStructChanged();
        if (!StructureEnabled)
        {
            return;
        }

        var leftInfo = new StructCompareSideInfo(Left.Name, Left.FilePath ?? Left.Owner?.FilePath, bytes.Left.Start);
        var rightInfo = new StructCompareSideInfo(Right.Name, Right.FilePath ?? Right.Owner?.FilePath, bytes.Right.Start);
        if (StructUnavailableReason is { } reason)
        {
            StructResult = StructCompareResult.Empty(StructCompareState.Unsupported, StructTemplateChoice.None, leftInfo, rightInfo, reason);
            RaiseStructChanged();
            return;
        }

        TemplateDataSource leftData = TemplateDataSource.FromSnapshot(leftSnapshot, leftInfo.Path ?? Left.Name);
        TemplateDataSource rightData = TemplateDataSource.FromSnapshot(rightSnapshot, rightInfo.Path ?? Right.Name);
        IReadOnlyList<TemplateApplicationInfo> leftApps = ApplicationsOf?.Invoke(Left) ?? [];
        IReadOnlyList<TemplateApplicationInfo> rightApps = ApplicationsOf?.Invoke(Right) ?? [];
        StructTemplateChoice? user = StructUserChoice;
        StructTemplateChoice choice;
        TemplateProgram? program = null;
        StructCompareState failure = StructCompareState.NoTemplate;
        try
        {
            (choice, program, failure) = await Task.Run(() =>
            {
                StructTemplateChoice c = StructTemplateResolver.Resolve(user,
                    new StructResolveSide(leftData, leftInfo.Path ?? Left.Name, bytes.Left.Start, leftApps),
                    new StructResolveSide(rightData, rightInfo.Path ?? Right.Name, bytes.Right.Start, rightApps), TemplateService.Catalog);
                if (c.IsNone)
                {
                    return (c, (TemplateProgram?)null, StructCompareState.NoTemplate);
                }

                try
                {
                    return (c, TemplateService.Load(c.TemplatePath), StructCompareState.Running);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (c, null, StructCompareState.TemplateMissing);
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            choice = StructTemplateChoice.None;
            failure = StructCompareState.Failed;
        }

        if (!ReferenceEquals(Result, bytes))
        {
            return; // 比較し直した
        }

        if (program is null)
        {
            StructResult = StructCompareResult.Empty(failure, choice, leftInfo, rightInfo);
            RaiseStructChanged();
            return;
        }

        int maxFields = _structMaxFieldsOverride > 0 ? _structMaxFieldsOverride
            : App.Settings?.GetInt(StructMaxFieldsKey, StructCompareOptions.DefaultMaxFields) ?? StructCompareOptions.DefaultMaxFields;
        var options = new StructCompareOptions
        {
            MaxFields = maxFields,
            IncludeHidden = App.Settings?.GetBool(StructIncludeHiddenKey, false) ?? false,
            Rules = StructArrayRules.FromJson(App.Settings?.GetNode(StructArrayRules.SettingKey)),
            Extensions = TemplateService.ApplyOptions().Extensions,
            TempDirectory = TemplateService.ApplyOptions().TempDirectory,
            Display = TemplateDisplayContext.Current,
        };
        StructCompareResult result = StructComparer.Start(program, choice,
            new StructCompareSide(leftData, choice.LeftStart, bytes.Left.Start, bytes.Left.Length, Left.Name, leftInfo.Path),
            new StructCompareSide(rightData, choice.RightStart, bytes.Right.Start, bytes.Right.Length, Right.Name, rightInfo.Path), options);
        StructResult = result;
        RaiseStructChanged();
        _refresh.Start();

        // 処理センター: 「構造の比較」(比べたフィールドの数と左の評価位置)。キャンセルすると、それまでの差分を残して止まる。
        Task operation = _operations.RunAsync(Loc.Get("Compare_StructOperationName"), OperationKind.ReadOnly, result, Math.Max(1, result.LeftLength), async op =>
        {
            using CancellationTokenRegistration link = op.CancellationToken.Register(result.Cancel);
            while (!result.Completion.IsCompleted)
            {
                op.Report(Math.Clamp(result.LeftPosition, 0, result.LeftLength));
                op.ReportDetail(Loc.Format("Compare_Struct_Fields", result.LeftFields.ToString("N0", CultureInfo.CurrentCulture)));
                await Task.WhenAny(result.Completion, Task.Delay(250));
            }
        });
        await result.Completion;
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await byteTask;
        }
        catch (Exception)
        {
            // バイトの比較の失敗は比較タブが知らせる
        }

        if (ReferenceEquals(StructResult, result) && bytes.State == CompareState.Completed)
        {
            await Task.Run(() => result.ComputeOutside(bytes.Diffs.Enumerate()));
        }

        if (ReferenceEquals(StructResult, result))
        {
            RaiseStructChanged();
        }
    }

    private void RaiseStructChanged() => StructResultChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 構造の差分の行を選ぶ (ANA-32 の仕様 4): 左右の Hex ビューでそれぞれの範囲を選ぶ。追加の行の左 (削除の行の右) は入るはずの位置に
    /// カーソルを置く (選択はしない)。移動はジャンプ履歴に記録し、その後の同期スクロールは左右の今の位置の差を保つ。
    /// </summary>
    public void SelectStructDiff(StructDiffNode node)
    {
        StructCurrent = node;
        Sync(() =>
        {
            Place(Left, node.HasLeft, node.LeftOffset, node.LeftSize);
            Place(Right, node.HasRight, node.RightOffset, node.RightSize);
        });
        _structDelta = node.LeftOffset - node.RightOffset;
        OnPropertyChanged(nameof(StatusText));
        ViewChanged?.Invoke(this, EventArgs.Empty);

        static void Place(CompareSideViewModel side, bool present, long offset, long size)
        {
            if (side.IsClosed)
            {
                return;
            }

            long at = Math.Clamp(offset, 0, side.Editor.Layout.MaxCursor);
            if (present && size > 0)
            {
                side.Editor.SelectMatch(at, Math.Min(size, side.Document.Length - at));
            }
            else
            {
                side.Editor.GoTo(at);
            }
        }
    }

    /// <summary>次 / 前の構造の差分 (表示中の葉の行の順。ANA-32 の仕様 5) を選ぶ。結果は折り返したかどうか。差分がなければ null。</summary>
    public bool? MoveStruct(StructDiffView view, bool next)
    {
        if (view.Step(StructCurrent, next) is not { } step)
        {
            return null;
        }

        view.Reveal(step.Node);
        SelectStructDiff(step.Node);
        return step.Wrapped;
    }

    /// <summary>同期スクロールの対応する位置: 構造の差分へ移動した後は左右の差を保つ (ANA-32 の仕様 4)。</summary>
    private long MapForSync(bool fromRight, long cursor) =>
        _structDelta is { } delta ? (fromRight ? cursor + delta : cursor - delta) : DiffNavigation.Map(Result!, fromRight, cursor);

    private void ResetStructDelta() => _structDelta = null;

    private void DisposeStructure()
    {
        StructResult?.Dispose();
        StructResult = null;
    }
}
