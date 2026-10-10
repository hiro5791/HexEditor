using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Coloring;
using HexEditor.Core.Operations;
using HexEditor.Core.View;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>位置マネージャ (INSP-31)・色付けルール (INSP-33)・凡例 (INSP-34) のパネルのつなぎ込み。</summary>
public sealed partial class MainWindow
{
    private bool _annotationPanelsQueued;

    private PositionManagerPanel CreatePositionManagerPanel()
    {
        var panel = new PositionManagerPanel(_positionVm!);
        panel.GoToRequested += (_, b) =>
        {
            if (Editor is { } editor)
            {
                // 移動はジャンプ履歴に記録する (INSP-26 の仕様 4 と同じ)。範囲は選択しない (カーソルに追従する選択を保つ)。
                editor.GoTo(Math.Min(b.Start, editor.Layout.MaxCursor));
            }
        };
        panel.ExportRequested += (_, html) => _ = ExportPositionNotesAsync(html, null);
        return panel;
    }

    private ColoringRulesPanel CreateColoringRulesPanel() => new(_coloringVm!);

    private LegendPanel CreateLegendPanel()
    {
        _legendVm!.Current = CurrentAnnotations;
        _legendVm.VisibleRange = VisibleRange;
        _legendVm.ShapesOnly = () => SelectedView() is { } v && !UseRuleColors(v);
        var panel = new LegendPanel(_legendVm);
        panel.NavigateRequested += (_, e) => _ = NavigateRuleAsync(e.Item, e.Forward);
        panel.CountAllRequested += (_, _) => _ = CountAllRulesAsync();
        return panel;
    }

    /// <summary>表示範囲 [開始, 終了)。</summary>
    private (long Start, long End)? VisibleRange()
    {
        if (Editor is not { } editor)
        {
            return null;
        }

        long start = Math.Max(0, editor.Layout.RowStart(editor.TopRow));
        long end = Math.Min(editor.Document.Length, editor.Layout.RowStart(editor.TopRow + editor.VisibleRows));
        return (start, end);
    }

    /// <summary>パネルの表示の変化を状態に反映する (SyncAnnotationPanels から呼ぶ)。</summary>
    private void SyncPhase2Panels()
    {
        if (_positionVm is null)
        {
            return;
        }

        bool position = IsPanelShown(PositionManagerPanelId) && Vm.Selected is not null;
        if (_positionVm.IsActive != position)
        {
            _positionVm.IsActive = position;
            _positionVm.Rebuild();
        }

        bool legend = IsPanelShown(LegendPanelId) && Vm.Selected is not null;
        if (_legendVm!.IsActive != legend)
        {
            _legendVm.IsActive = legend;
            _legendVm.Refresh();
        }
    }

    /// <summary>ドキュメントを切り替えた (AttachAnnotationsToSelected から呼ぶ)。</summary>
    private void AttachPhase2Panels(DocumentAnnotations? annotations)
    {
        if (annotations is not null)
        {
            CompileColoring(annotations);
        }

        _positionVm?.Attach(annotations);
        _coloringVm?.Attach(annotations);
        _legendVm?.Refresh();
    }

    /// <summary>カーソル・表示範囲が変わった: 位置マネージャの追従と凡例の件数を更新する (同じフレームの変更はまとめる)。</summary>
    private void QueueAnnotationPanelsRefresh()
    {
        if (_annotationPanelsQueued || _positionVm is null || !_positionVm.IsActive && !_legendVm!.IsActive)
        {
            return;
        }

        _annotationPanelsQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _annotationPanelsQueued = false;
            if (_positionVm.IsActive && _positionVm.FollowCursor && Editor is { } editor && PositionManagerView is { } panel)
            {
                // カーソルを含むブックマークを一覧で選ぶ (INSP-31 の仕様 2。エディタは動かさない)。
                if (_positionVm.AtCursor(editor.Cursor) is { } at && _positionVm.Selected?.Bookmark != at)
                {
                    panel.SelectQuietly(at);
                }
            }

            _legendVm!.Refresh();
        });
    }

    /// <summary>位置マネージャの内容を Markdown / HTML に書き出す (INSP-31 の仕様 6)。</summary>
    internal async Task ExportPositionNotesAsync(bool html, string? path)
    {
        if (_positionVm is null || CurrentAnnotations() is not { } a)
        {
            return;
        }

        string name = (a.Document.FilePath is { } file ? Path.GetFileNameWithoutExtension(file) : "notes") + (html ? ".html" : ".md");
        path ??= await PickNotesPathAsync(name, html);
        if (path is null)
        {
            return;
        }

        bool wasActive = _positionVm.IsActive;
        _positionVm.IsActive = true;
        _positionVm.Rebuild();
        string text = _positionVm.ExportText(html);
        _positionVm.IsActive = wasActive;
        try
        {
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(false));
            ShowNotice(Loc.Format("PositionManager_Exported", Path.GetFileName(path)), InfoBarSeverity.Success, Vm.Selected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 書き出しの失敗は理由を InfoBar に表示する (INSP-31 の「エラー」)。
            ShowNotice(Loc.Format("PositionManager_ExportError", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error, Vm.Selected);
        }
    }

    private async Task<string?> PickNotesPathAsync(string suggestedName, bool html)
    {
        if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
        {
            return chosen;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = suggestedName, SettingsIdentifier = "HexEditor.PositionNotes" };
        picker.FileTypeChoices.Add(Loc.Get(html ? "FileType_Html" : "FileType_Markdown"), [html ? ".html" : ".md"]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    // ---- 凡例 (INSP-34 の仕様 3) ----

    /// <summary>「次へ」「前へ」: そのルールに一致する次 / 前の位置に移動する (検索エンジンを使う。進捗とキャンセルを出す)。</summary>
    internal async Task NavigateRuleAsync(LegendItem item, bool forward)
    {
        if (item.Rule is not { } rule || Editor is not { } editor)
        {
            return;
        }

        Core.Engine.DocumentSnapshot snapshot = editor.Document.Current;
        long from = editor.Cursor;
        long? found;
        try
        {
            found = await Vm.Operations.RunAsync(Loc.Get("Legend_Searching"), OperationKind.ReadOnly, editor.Document, snapshot.Length,
                op => Task.FromResult(ColoringEngine.FindNext(snapshot, rule, from, forward, op.CancellationToken)));
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (found is { } offset)
        {
            editor.GoTo(Math.Min(offset, editor.Layout.MaxCursor));
        }
        else
        {
            ShowStatusMessage(Loc.Get(forward ? "Legend_NoNext" : "Legend_NoPrevious"));
        }
    }

    /// <summary>「全体の件数を数える」(長時間処理): ドキュメント全体での各ルールの件数。</summary>
    internal async Task CountAllRulesAsync()
    {
        if (_legendVm is null || Editor is not { } editor)
        {
            return;
        }

        Core.Engine.DocumentSnapshot snapshot = editor.Document.Current;
        List<LegendItem> rules = [.. _legendVm.Items.Where(i => i.Rule is not null)];
        try
        {
            long[] totals = await Vm.Operations.RunAsync(Loc.Get("Legend_Counting"), OperationKind.ReadOnly, editor.Document, snapshot.Length * Math.Max(1, rules.Count),
                op =>
                {
                    long[] result = new long[rules.Count];
                    for (int i = 0; i < rules.Count; i++)
                    {
                        int index = i;
                        result[i] = ColoringEngine.CountAll(snapshot, rules[i].Rule!,
                            new Progress<double>(p => op.Report((long)((index + p) * snapshot.Length))), op.CancellationToken);
                    }

                    return Task.FromResult(result);
                });
            for (int i = 0; i < rules.Count; i++)
            {
                rules[i].TotalText = LegendViewModel.TotalText(totals[i]);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
