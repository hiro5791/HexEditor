using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Notifications;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// データ > データ演算 (EDIT-31〜EDIT-37)。ダイアログの中身は <see cref="DataOperationPanel"/>、計算は Core の <see cref="DataOperationRunner"/> と
/// <see cref="BitShifter"/>。対象範囲が 4 MiB を超える演算は長時間処理 (処理センター) として実行し、結果は作り終えてから 1 つの編集グループで反映する。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>最後に実行したデータ演算 (「前回のデータ演算を繰り返す」。アプリ全体で 1 つ。EDIT-31 の仕様 11)。</summary>
    private static DataOperationRequest? s_lastDataOperation;

    /// <summary>選択範囲 (マルチ選択なら各範囲。選択がなければ空)。</summary>
    private IReadOnlyList<TargetRange> SelectionRangesOf(DocumentViewModel doc)
    {
        // マルチ選択・矩形選択は各要素 (オフセット順。EDIT-31 の仕様 9)。
        if (doc.Editor.HasMultipleRanges)
        {
            return [.. doc.Editor.SelectedRanges.Where(r => r.Length > 0).Select(r => new TargetRange(r.Start, r.Length))];
        }

        EditorState editor = doc.Editor;
        return editor.HasSelection ? [new TargetRange(editor.SelectionStart, editor.SelectionLength)] : [];
    }

    /// <summary>
    /// 要素の一覧を作る操作 (データ演算、文字コード変換など) の前に、矩形の行数が上限を超えていないかを確かめる。超えていれば一覧を作らずに
    /// InfoBar で知らせて false (EDIT-06 の仕様 6、EDIT-17 の仕様 6)。
    /// </summary>
    private bool RangeListAllowed(DocumentViewModel doc, bool changesLength)
    {
        if (doc.Editor.RectangleListLimitExceeded(changesLength) is not { } limit)
        {
            return true;
        }

        ShowNotice(Loc.Format("Notice_RectangleListLimit", doc.Editor.SelectedRangeCount.ToString("N0", CultureInfo.CurrentCulture),
            limit.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Warning, doc);
        return false;
    }

    /// <summary>コマンド ID の演算名の部分 (<c>data.op.byteSwap16</c>)。</summary>
    internal static string DataOperationCommandId(DataOperationKind kind) =>
        "data.op." + char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    private void RegisterDataCommands()
    {
        Commands.Register("data.operation", () => DataOperationAsync(null), () => NeedsEditable(DataTargetReason));
        Commands.Register("data.repeatOperation", RepeatDataOperationAsync, () => NeedsEditable(d =>
            s_lastDataOperation is null ? Loc.Get("DataOp_NoPrevious") : !d.Editor.HasSelection ? Loc.Get("Command_NoSelection") : null));

        // コマンドパレット「データ演算: <演算名>」(演算ごとのコマンド)。並べ替えは選択範囲にそのまま実行する (EDIT-35 の「呼び出し」)。
        foreach (DataOperationKind kind in Enum.GetValues<DataOperationKind>())
        {
            Commands.Register(DataOperationCommandId(kind), () => DataOperationAsync(kind), () => NeedsEditable(DataTargetReason));
        }

        RegisterTextConversionCommands();
    }

    private static string? DataTargetReason(DocumentViewModel doc) => doc.Document.Length > 0 ? null : Loc.Get("Command_EmptyDocument");

    /// <summary>閉じたときにデータ演算の項目を外すようにしたメニュー。</summary>
    private readonly HashSet<MenuFlyout> _dataMenuCleanup = [];

    /// <summary>
    /// 右クリックメニューの「データ演算」(演算の種類のサブメニュー付き。EDIT-31 の「呼び出し」)。項目が多いので、メニューを開くたびに作り、
    /// 閉じたら外す (開いていない間もメニューの項目があると、キーを押すたびのキーボードアクセラレータの探索が遅くなるため)。
    /// </summary>
    private void ExtendHexViewDataMenu(MenuFlyout menu)
    {
        const string id = "HexViewMenu_DataOperation";
        if (!menu.IsOpen && !_dataMenuCleanup.Contains(menu))
        {
            // Hex ビューの読み込み時の呼び出し (まだ開いていない) では作らない。
            _dataMenuCleanup.Add(menu);
            menu.Closed += (_, _) =>
            {
                foreach (MenuFlyoutItemBase item in menu.Items.Where(i => AutomationProperties.GetAutomationId(i) is "HexViewMenu_DataOperation"
                    or "HexViewMenu_data.convertEncoding").ToList())
                {
                    menu.Items.Remove(item);
                }
            };
            return;
        }

        if (menu.Items.FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == id) is not MenuFlyoutSubItem sub)
        {
            sub = new MenuFlyoutSubItem { Text = Loc.Get("HexViewMenu_DataOperation") };
            AutomationProperties.SetAutomationId(sub, id);
            var all = new MenuFlyoutItem { Text = Loc.Get("Cmd_data_operation") };
            AutomationProperties.SetAutomationId(all, id + "_All");
            all.Click += (_, _) => _ = Commands.ExecuteAsync("data.operation");
            sub.Items.Add(all);
            sub.Items.Add(new MenuFlyoutSeparator());
            foreach (DataOperationCategory category in Enum.GetValues<DataOperationCategory>())
            {
                var categoryMenu = new MenuFlyoutSubItem { Text = Loc.Get("DataOp_Category_" + category) };
                AutomationProperties.SetAutomationId(categoryMenu, id + "_" + category);
                foreach (DataOperationKind kind in Enum.GetValues<DataOperationKind>().Where(k => DataOperationSpec.CategoryOf(k) == category))
                {
                    var item = new MenuFlyoutItem { Text = Loc.Get("DataOp_Kind_" + kind) };
                    AutomationProperties.SetAutomationId(item, id + "_" + kind);
                    string command = DataOperationCommandId(kind);
                    item.Click += (_, _) => _ = Commands.ExecuteAsync(command);
                    categoryMenu.Items.Add(item);
                }

                sub.Items.Add(categoryMenu);
            }

            int index = menu.Items.ToList().FindIndex(i => AutomationProperties.GetAutomationId(i) == "HexViewMenu_edit.fill");
            menu.Items.Insert(index < 0 ? menu.Items.Count : index + 1, sub);
            ExtendHexViewTextMenu(menu, menu.Items.IndexOf(sub) + 1);
        }

        sub.IsEnabled = Commands.StateOf("data.operation").Enabled;
    }

    // ---- 実行 ----

    /// <summary>データ演算ダイアログを開く。並べ替えのコマンドは選択範囲があればダイアログを開かずに実行する。</summary>
    private async Task DataOperationAsync(DataOperationKind? kind)
    {
        if (Vm.Selected is not { } doc || !EnsureEditable(doc) || !RangeListAllowed(doc, changesLength: false))
        {
            return;
        }

        IReadOnlyList<TargetRange> selection = SelectionRangesOf(doc);
        if (kind is { } k && DataOperationSpec.CategoryOf(k) == DataOperationCategory.Reorder && selection.Count > 0)
        {
            await RunDataOperationAsync(doc, new DataOperationRequest(new DataOperationSpec { Kind = k }, selection, null));
            return;
        }

        if (await ShowDataOperationDialogAsync(doc, selection, kind) is { } request)
        {
            await RunDataOperationAsync(doc, request);
        }

        FocusEditor();
    }

    private async Task<DataOperationRequest?> ShowDataOperationDialogAsync(DocumentViewModel doc, IReadOnlyList<TargetRange> selection,
        DataOperationKind? kind)
    {
        var panel = new DataOperationPanel(doc.Editor, selection, kind, doc.Editor.View.BigEndian);
        ContentDialog dialog = DialogParts.Dialog(Root, "DataOperationDialog", Loc.Get("DataOp_Title"),
            new ScrollViewer { Content = panel, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) }, Loc.Get("DataOp_Run"));
        DataOperationRequest? request = null;
        void Validate()
        {
            request = panel.TryGetRequest();
            dialog.IsPrimaryButtonEnabled = request is not null;
        }

        panel.Changed += (_, _) => Validate();
        Validate();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || request is null)
        {
            return null;
        }

        panel.Save();
        return request;
    }

    /// <summary>「前回のデータ演算を繰り返す」: 最後の演算と設定を、今の選択範囲にダイアログを開かずに実行する (EDIT-31 の仕様 11)。</summary>
    private async Task RepeatDataOperationAsync()
    {
        if (Vm.Selected is not { } doc || s_lastDataOperation is not { } last || !EnsureEditable(doc)
            || !RangeListAllowed(doc, changesLength: last.Spec.Category == DataOperationCategory.BitInsertDelete))
        {
            return;
        }

        IReadOnlyList<TargetRange> selection = SelectionRangesOf(doc);
        if (selection.Count == 0)
        {
            ShowNotice(Loc.Get("Command_NoSelection"), InfoBarSeverity.Warning, doc);
            return;
        }

        DataOperationSpec spec = last.Spec;
        if (spec.Category == DataOperationCategory.BitInsertDelete)
        {
            spec = spec with { BitOffset = selection[0].Offset };
        }

        await RunDataOperationAsync(doc, last with { Spec = spec, Ranges = selection });
    }

    /// <summary>演算を実行する。キャンセル・失敗したらドキュメントは変えない (共通の約束 2)。</summary>
    private async Task RunDataOperationAsync(DocumentViewModel doc, DataOperationRequest request)
    {
        DataOperationSpec spec = request.Spec;
        if (request.KeySource is { } keySource)
        {
            if (await ReadDataOperationKeyAsync(doc, keySource) is not { } key)
            {
                return;
            }

            spec = spec with { Key = key };
        }

        s_lastDataOperation = request with { Spec = spec, KeySource = null };
        if (spec.Category == DataOperationCategory.BitInsertDelete)
        {
            await RunBitShiftAsync(doc, spec, request.Ranges);
            return;
        }

        DataOperationError error = DataOperationRunner.Validate(request.Ranges, spec);
        if (error != DataOperationError.None)
        {
            ShowNotice(Loc.Get("DataOp_Error_" + error), InfoBarSeverity.Error, doc);
            return;
        }

        // 値が変わらない演算は実行しない (EDIT-31 の「巨大ファイル」5)。
        if (spec.IsIdentity())
        {
            ShowNotice(Loc.Get("DataOp_Unchanged"), InfoBarSeverity.Informational, doc);
            return;
        }

        DataOperationRunner runner = DataOperationRunner.For(doc.Document, TestHooks.Volumes ?? SystemVolumeInfoProvider.Instance);
        DocumentSnapshot snapshot = doc.Document.Current;
        IReadOnlyList<TargetRange> ranges = request.Ranges;
        string name = Loc.Format("Operation_DataOperation", Loc.Get("DataOp_Kind_" + spec.Kind));
        DataOperationResult? result = await RunTransformAsync(doc, name, DataOperationRunner.TotalBytes(ranges),
            DataOperationRunner.IsLongRunning(ranges), op => runner.Run(snapshot, ranges, spec, op));
        if (result is null)
        {
            return;
        }

        if (!result.Stats.Changed)
        {
            TransformApplier.DisposeAll(result.Replacements);
            ShowNotice(Loc.Get("DataOp_Unchanged"), InfoBarSeverity.Informational, doc);
            return;
        }

        if (!ApplyEdit(doc, () => TransformApplier.Apply(doc.Document, result.Replacements, "データ演算")))
        {
            TransformApplier.DisposeAll(result.Replacements);
            return;
        }

        ShowDataOperationSummary(doc, spec, result.Stats);
    }

    /// <summary>完了時の件数 (EDIT-31 の仕様 8、EDIT-32 の仕様 3・4、EDIT-36 の仕様 3)。</summary>
    private void ShowDataOperationSummary(DocumentViewModel doc, DataOperationSpec spec, DataOperationStats stats)
    {
        var parts = new List<string>();
        if (spec.Category == DataOperationCategory.Arithmetic)
        {
            if (stats.Overflows > 0)
            {
                parts.Add(Loc.Format("DataOp_Result_Overflows", stats.Overflows));
            }

            if (stats.NaNs > 0 || stats.Infinities > 0)
            {
                parts.Add(Loc.Format("DataOp_Result_NaN", stats.NaNs, stats.Infinities));
            }

            if (stats.ZeroSkipped > 0)
            {
                parts.Add(Loc.Format("DataOp_Result_ZeroSkipped", stats.ZeroSkipped));
            }
        }

        if (spec.Category == DataOperationCategory.Clamp)
        {
            parts.Add(Loc.Format("DataOp_Result_Changed", stats.ChangedElements));
        }

        if (stats.TrailingBytes > 0)
        {
            parts.Add(Loc.Format("DataOp_TrailingNote", stats.TrailingBytes));
        }

        if (parts.Count > 0)
        {
            ShowNotice(string.Join(" ", parts), InfoBarSeverity.Informational, doc,
                undo: new NotificationAction(Loc.Get("Common_Undo"), () => doc.Editor.Undo()));
        }
    }

    /// <summary>
    /// 変換の結果を作る。<paramref name="longRunning"/> なら処理センターに登録し (進捗・処理速度・残り時間・キャンセル)、作っている間は
    /// ドキュメントの編集を止める。キャンセル・失敗したら null (作りかけの一時ファイルは消える)。
    /// </summary>
    private async Task<T?> RunTransformAsync<T>(DocumentViewModel doc, string name, long total, bool longRunning, Func<LongRunningOperation?, T> work)
        where T : class
    {
        try
        {
            if (!longRunning)
            {
                return work(null);
            }

            return await Vm.Operations.RunAsync(name, OperationKind.ModifiesDocument, doc.Document, total,
                op => Task.FromResult(work(op)), locked => doc.Document.SetEditLock(locked));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (TempSpaceException ex)
        {
            ShowNotice(Loc.Format("DataOp_Error_TempSpace", GigaBytes(ex.Required), GigaBytes(ex.Available)), InfoBarSeverity.Error, doc);
            return null;
        }
        catch (DataReadException ex)
        {
            ShowNotice(Loc.Format("DataOp_Error_Read", StatusFormat.Hex(ex.Offset)), InfoBarSeverity.Error, doc);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Fill_Error_File", ex.Message), InfoBarSeverity.Error, doc);
            return null;
        }
    }

    /// <summary>ファイル・クリップボードの鍵を読む (1 バイト〜16 MiB。EDIT-33 の仕様 2 と「エラー」)。読めなければ null。</summary>
    private async Task<byte[]?> ReadDataOperationKeyAsync(DocumentViewModel doc, FillSpec source)
    {
        try
        {
            byte[]? key = null;
            if (source.Kind == FillKind.File)
            {
                using var stream = new FileStream(source.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long offset = Math.Min(source.FileOffset, stream.Length);
                long length = Math.Min(source.FileLength ?? long.MaxValue, stream.Length - offset);
                if (length > DataOperationSpec.MaxKeyLength)
                {
                    ShowNotice(Loc.Get("DataOp_Error_KeyTooLong"), InfoBarSeverity.Error, doc);
                    return null;
                }

                key = new byte[length];
                stream.Position = offset;
                await stream.ReadExactlyAsync(key);
            }
            else
            {
                (byte[]? bytes, Core.Sources.IByteSource? clip) = await _clipboard.ReadForFillAsync(doc.Editor);
                if (clip is not null)
                {
                    if (clip.Length > DataOperationSpec.MaxKeyLength)
                    {
                        ShowNotice(Loc.Get("DataOp_Error_KeyTooLong"), InfoBarSeverity.Error, doc);
                        return null;
                    }

                    bytes = new byte[clip.Length];
                    clip.Read(0, bytes);
                }

                key = bytes;
                if (key is { Length: > DataOperationSpec.MaxKeyLength })
                {
                    ShowNotice(Loc.Get("DataOp_Error_KeyTooLong"), InfoBarSeverity.Error, doc);
                    return null;
                }
            }

            if (key is not { Length: > 0 })
            {
                ShowNotice(Loc.Get("DataOp_Error_KeyEmpty"), InfoBarSeverity.Error, doc);
                return null;
            }

            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice(Loc.Format("Fill_Error_File", ex.Message), InfoBarSeverity.Error, doc);
            return null;
        }
    }

    // ---- ビットの挿入・削除 (EDIT-37) ----

    private async Task RunBitShiftAsync(DocumentViewModel doc, DataOperationSpec spec, IReadOnlyList<TargetRange> ranges)
    {
        Document document = doc.Document;
        TargetRange? region = ranges.FirstOrDefault(r => r.Offset <= spec.BitOffset && spec.BitOffset < r.End);
        TargetRange? selection = region is { Length: > 0 } ? region : null;
        BitShiftInfo info = BitShifter.Analyze(document.Length, document.CanResize, selection, spec);
        if (info.Error != DataOperationError.None)
        {
            ShowNotice(Loc.Get("DataOp_Error_" + info.Error), InfoBarSeverity.Error, doc);
            return;
        }

        // 1 GiB を超えて書き直す場合は確認する (EDIT-37 の「巨大ファイル」)。
        if (info.RewriteBytes > BitShifter.ConfirmLimit)
        {
            string size = GigaBytes(info.RewriteBytes);
            ContentDialog confirm = DialogParts.Dialog(Root, "BitShiftConfirmDialog", Loc.Get("DataOp_Title"),
                new TextBlock { Text = Loc.Format("DataOp_RewriteConfirm", size), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                Loc.Get("DataOp_RewriteRun"));
            if (await ShowEditDialogAsync(confirm) != ContentDialogResult.Primary)
            {
                return;
            }
        }

        BitShifter shifter = BitShifter.For(document, TestHooks.Volumes ?? SystemVolumeInfoProvider.Instance);
        DocumentSnapshot snapshot = document.Current;
        bool canResize = document.CanResize;
        string name = Loc.Format("Operation_DataOperation", Loc.Get("DataOp_Kind_" + spec.Kind));
        BitEditPlan? plan = await RunTransformAsync(doc, name, info.RewriteBytes, info.RewriteBytes > DataOperationRunner.InPlaceLimit,
            op => shifter.Build(snapshot, canResize, selection, spec, op));
        if (plan is null)
        {
            return;
        }

        if (!ApplyEdit(doc, () => plan.Apply(document, spec.Kind == DataOperationKind.InsertBits ? "ビットの挿入" : "ビットの削除")))
        {
            plan.Dispose();
        }
    }
}
