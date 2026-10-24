using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.App.ViewModels;
using HexEditor.Core.Expressions;
using HexEditor.Core.Panels;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Inference;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Presentation;
using HexEditor.Core.View;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// テンプレートの推定 (07 の 8 章、TPL-38〜TPL-40) のウィンドウ側: パネル (既定は右)、コマンド、Hex ビューの右クリックメニュー、
/// Hex ビューのプレビュー (VIEW-17 の「テンプレートの範囲の色分け」の層)、「適用」「エディタで開く」「コピー」、パターン統計 (ANA-15) からの呼び出し。
/// <para>
/// ほかの機能から呼ぶ口: コマンド <c>templates.infer</c> を引数 <c>start</c>・<c>length</c>・<c>recordLength</c> (省略可) 付きで実行するか
/// (<c>using (CommandArgs.Begin(...)) await Commands.ExecuteAsync("templates.infer")</c>)、<see cref="OpenTemplateInference"/> を呼ぶ。
/// ファイルマップの繰り返しのレコードの領域 (06 の ANA-34 の 6) はこれで開く。
/// </para>
/// </summary>
public sealed partial class MainWindow : ITemplateInferenceHost
{
    public const string TemplateInferencePanelId = "templateInference";

    /// <summary>表を変えてから下書きを作り直すまでの時間 (TPL-40 の仕様 5)。</summary>
    public static readonly TimeSpan InferenceDraftDelay = TimeSpan.FromMilliseconds(300);

    private TemplateInferencePanel? _inferencePanel;
    private TemplateInferenceSession? _inferenceSession;
    private DispatcherQueueTimer? _inferenceDraftTimer;
    private DocumentViewModel? _inferenceCursorDoc;
    private bool _inferenceSelecting;

    /// <summary>パネルの一覧に登録する (<see cref="RegisterTemplatePanels"/> から呼ぶ)。</summary>
    private static void RegisterTemplateInferencePanel()
    {
        if (PanelRegistry.Find(TemplateInferencePanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(TemplateInferencePanelId, "Panel_TemplateInference_Title", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).InferencePanel)
            {
                CanFloat = false,
            });
        }
    }

    private TemplateInferencePanel InferencePanel
    {
        get
        {
            if (_inferencePanel is null)
            {
                _inferencePanel = new TemplateInferencePanel(this);
                _inferencePanel.Bind(_inferenceSession);
            }

            return _inferencePanel;
        }
    }

    private bool InferencePanelShown => IsPanelShown(TemplateInferencePanelId) && _inferencePanel is { IsLoaded: true };

    /// <summary><see cref="RegisterTemplateCommands"/> から呼ぶ。</summary>
    private void RegisterTemplateInferenceCommands()
    {
        Commands.Register("templates.infer", () => InferFromCommand(), NeedsDocument);
        Commands.Register("templates.inferAgain", () => RunInferenceAsync(keepManual: true),
            () => NeedsDocument(_ => _inferenceSession?.Result?.HasFields == true ? null : Loc.Get("TplInfer_NoResult")));
        Commands.Register("templates.inferApply", ApplyInference,
            () => NeedsDocument(_ => HasInferenceDraft ? null : Loc.Get("TplInfer_NoResult")));
        Commands.Register("templates.inferOpenEditor", OpenInferenceInEditor,
            () => NeedsDocument(_ => HasInferenceDraft ? null : Loc.Get("TplInfer_NoResult")));
        Commands.Register("templates.showInference", () => ShowPanel(TemplateInferencePanelId));

        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                AttachInferenceToSelected();
            }
        };
        AttachInferenceToSelected();

        // ウィンドウを本当に閉じたら、アプリ全体で持つ状態 (ドキュメントごとの推定) への購読を外す
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                if (_inferenceCursorDoc is { } doc)
                {
                    doc.EditorChanged -= InferenceDocument_EditorChanged;
                }

                if (_inferenceSession is { } s)
                {
                    s.TableChanged -= InferenceSession_TableChanged;
                }

                _inferenceDraftTimer?.Stop();
            }
        };
    }

    private bool HasInferenceDraft => _inferenceSession is { Table.Rows.Count: > 0 } s && s.Draft.Length > 0;

    /// <summary>作業中のドキュメントの推定の状態をパネルにつなぐ。</summary>
    private void AttachInferenceToSelected()
    {
        if (_inferenceCursorDoc is { } old)
        {
            old.EditorChanged -= InferenceDocument_EditorChanged;
        }

        if (_inferenceSession is { } previous)
        {
            previous.TableChanged -= InferenceSession_TableChanged;
        }

        DocumentViewModel? doc = Vm.Selected;
        _inferenceCursorDoc = doc;
        _inferenceSession = doc is null ? null : TemplateInferenceSession.For(doc.Document);
        if (doc is not null)
        {
            doc.EditorChanged += InferenceDocument_EditorChanged;
        }

        if (_inferenceSession is { } s)
        {
            s.TableChanged += InferenceSession_TableChanged;
        }

        _inferencePanel?.Bind(_inferenceSession);
        RefreshTemplateHighlights();
    }

    // ───────────── 開く (TPL-38 の「呼び出し」) ─────────────

    /// <summary>コマンド: 引数 (ファイルマップなど) があればその範囲、なければ選択範囲 (なければカーソル位置から 64 KiB)。</summary>
    private Task InferFromCommand()
    {
        if (CommandArgs.Has("start") || CommandArgs.Has("length"))
        {
            long start = CommandArgs.Get<long>("start");
            long length = CommandArgs.Get<long>("length");
            int? k = CommandArgs.Has("recordLength") ? CommandArgs.Get<int>("recordLength") : null;
            return OpenTemplateInferenceAsync(start, length, k);
        }

        return OpenTemplateInferenceAsync(null, null, null);
    }

    /// <summary>
    /// テンプレートの推定のパネルを、範囲とレコード長 (null は「自動」) を入れた状態で開き、推定を始める (ファイルマップの ANA-34・パターン統計の ANA-15 から)。
    /// </summary>
    public void OpenTemplateInference(long start, long length, int? recordLength) => _ = OpenTemplateInferenceAsync(start, length, recordLength);

    private async Task OpenTemplateInferenceAsync(long? start, long? length, int? recordLength)
    {
        if (Vm.Selected is not { } doc || _inferenceSession is not { } s)
        {
            return;
        }

        EditorState editor = doc.Editor;
        if (start is null)
        {
            // 選択範囲 (既定)。なければカーソル位置から 64 KiB (末尾がそれより近ければ末尾まで)。
            if (editor.HasSelection)
            {
                start = editor.SelectionStart;
                length = editor.SelectionLength;
            }
            else
            {
                start = editor.Cursor;
                length = Math.Min(TemplateInference.DefaultRangeLength, doc.Document.Length - editor.Cursor);
            }
        }

        s.StartText = "0x" + start.Value.ToString("X", CultureInfo.InvariantCulture);
        s.LengthText = "0x" + Math.Max(0, length ?? 0).ToString("X", CultureInfo.InvariantCulture);
        s.RecordLengthText = recordLength is int k ? k.ToString(CultureInfo.InvariantCulture) : string.Empty;
        s.RecordCountText = string.Empty;
        s.Configured = true;
        s.Candidates = [];
        ShowPanel(TemplateInferencePanelId, focus: false);
        InferencePanel.Bind(s);

        // 範囲が条件を満たしていれば、ボタンを押さなくても推定を始める (TPL-40 の仕様 2)。キーボードで続けられるよう、結果の表にフォーカスを移す。
        await RunInferenceAsync();
        // (右クリックメニューを閉じたときの Hex ビューへのフォーカスの戻りより後に移す)
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ReferenceEquals(s, _inferenceSession) && s.Result is { } result)
            {
                if (result.HasFields)
                {
                    InferencePanel.FocusTable();
                }
                else if (result.NotFound)
                {
                    InferencePanel.FocusRecordLength();
                }
            }
        });
    }

    // ───────────── 推定 (TPL-38・TPL-39) ─────────────

    public async Task RunInferenceAsync(int? candidate = null, bool keepManual = false)
    {
        if (Vm.Selected is not { } doc || _inferenceSession is not { } s)
        {
            return;
        }

        (TemplateInferenceRequest? request, string? error) = TemplateInferencePanel.ParseRequest(s, InferenceExpressionContext, doc.Document.Length);
        if (request is null || error is not null)
        {
            InferencePanel.Refresh();
            return;
        }

        if (candidate is int k)
        {
            request = request with { RecordLength = k };
        }

        // 入力を変えたら前の推定を取り消して始め直す (TPL-38 の仕様 7)
        s.Cancellation?.Cancel();
        var cts = new CancellationTokenSource();
        s.Cancellation = cts;
        s.Running = true;
        InferencePanel.Refresh();
        Core.Engine.DocumentSnapshot snapshot = doc.Document.Current;
        TemplateInferenceResult result;
        try
        {
            result = await Vm.Operations.RunAsync(Loc.Get("TplInfer_OperationName"), Core.Operations.OperationKind.ReadOnly, doc.Document, null, op =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(op.CancellationToken, cts.Token);
                return Task.FromResult(TemplateInference.Run(snapshot, request, linked.Token));
            });
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(s.Cancellation, cts))
            {
                s.Running = false;
                s.Cancellation = null;
                InferencePanel.Refresh();
            }

            return;
        }

        if (!ReferenceEquals(s.Cancellation, cts))
        {
            return;
        }

        s.Cancellation = null;
        s.Running = false;
        s.Runs++;
        s.Result = result;
        if (candidate is null)
        {
            s.Candidates = result.Candidates;
        }

        s.InternalError = null;
        s.PreviewHidden = false;
        if (result.HasFields)
        {
            s.Table.Load(result, keepManual);
        }
        else
        {
            s.Table.Clear(result);
        }

        RegenerateInferenceDraft(s);
        if (ReferenceEquals(s, _inferenceSession))
        {
            InferencePanel.Refresh();
            if (result.NotFound)
            {
                // 「固定長のレコードが見つかりませんでした」: レコード長の入力を求める (TPL-38 の仕様 4)
                InferencePanel.FocusRecordLength();
            }
        }

        RefreshTemplateHighlights();
        UpdateCommandStates();
    }

    public IExpressionContext? InferenceExpressionContext => Vm.Selected is { } doc ? new EditorExpressionContext(doc.Editor) : null;

    // ───────────── 下書き (TPL-40 の仕様 5) ─────────────

    private void InferenceSession_TableChanged(object? sender, EventArgs e)
    {
        if (sender is not TemplateInferenceSession s)
        {
            return;
        }

        s.TableVersion++;
        s.PreviewHidden = false;
        InferencePanel.RefreshTable();
        RefreshTemplateHighlights();

        // 表を変えたら 300 ms 待ってから下書きを作り直す
        _inferenceDraftTimer ??= CreateInferenceDraftTimer();
        _inferenceDraftTimer.Stop();
        _inferenceDraftTimer.Start();
    }

    private DispatcherQueueTimer CreateInferenceDraftTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = InferenceDraftDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (_inferenceSession is { } s && s.DraftVersion != s.TableVersion)
            {
                RegenerateInferenceDraft(s);
                InferencePanel.Refresh();
                UpdateCommandStates();
            }
        };
        return timer;
    }

    private string InferenceDocumentName(TemplateInferenceSession s) =>
        Vm.Documents.FirstOrDefault(d => d.Document == s.Document) is { } doc ? Path.GetFileName(doc.FilePath ?? doc.DisplayName) : "document";

    /// <summary>下書きを作り、構文チェックにかける (エラーは内部エラーとして記録する。利用者の操作の誤りにはしない)。</summary>
    private void RegenerateInferenceDraft(TemplateInferenceSession s)
    {
        s.DraftVersion = s.TableVersion;
        if (s.Table.Result is not { RecordLength: int k } r || s.Table.Rows.Count == 0)
        {
            s.Draft = string.Empty;
            return;
        }

        var info = new TemplateDraftInfo(InferenceDocumentName(s), r.Start, k, r.RecordCount, DateTime.Now, s.Table.BigEndian);
        s.Draft = TemplateDraftWriter.Write(s.Table.Rows, info, m => Loc.Format(m.Key, m.Args));
        TemplateProgram program = TemplateLoader.LoadText(s.Draft, "inferred.hxt");
        if (program.HasErrors)
        {
            string detail = string.Join("; ", program.Diagnostics.Where(d => d.Severity == TemplateSeverity.Error).Take(3).Select(d => d.ToString()));
            s.InternalError = detail;
            AppLog.Error("Template inference: the generated draft has syntax errors: " + detail);
        }
        else
        {
            s.InternalError = null;
        }
    }

    /// <summary>表の変更を待たずに下書きを作る (「適用」「エディタで開く」「コピー」の前)。</summary>
    private string? CurrentInferenceDraft()
    {
        if (_inferenceSession is not { } s)
        {
            return null;
        }

        if (s.DraftVersion != s.TableVersion)
        {
            _inferenceDraftTimer?.Stop();
            RegenerateInferenceDraft(s);
            InferencePanel.Refresh();
        }

        return s.Draft.Length > 0 && s.Table.Rows.Count > 0 ? s.Draft : null;
    }

    // ───────────── ボタン (TPL-40 の仕様 6) ─────────────

    /// <summary>
    /// 「適用」: 下書きを保存せずに、範囲の開始をオフセットとする固定の適用として適用する (結果ツリーに「(未保存)」)。もう一度押すと、同じ
    /// 適用を新しい下書きで置き換える。
    /// </summary>
    public void ApplyInference()
    {
        if (Vm.Selected is not { } doc || _inferenceSession is not { Table.Result: { } r } s || CurrentInferenceDraft() is not { } draft)
        {
            return;
        }

        string name = InferenceDocumentName(s);
        string path = Path.Combine(TemplateService.DefaultUserFolder, InferredFileName(name));
        TemplateProgram program = TemplateLoader.LoadText(draft, Path.GetFileName(path), TemplateService.DefaultUserFolder, TemplateService.LoadOptions);
        DocumentTemplates templates = DocumentTemplates.For(doc);
        if (s.Applied is { } applied && templates.Entries.Contains(applied))
        {
            applied.Application.SetUnsavedProgram(program);
            ShowPanel(TemplatesPanelId, focus: false);
        }
        else
        {
            s.Applied = AddTemplateApplication(doc, new TemplateApplicationInfo { TemplatePath = path, StartOffset = r.Start }, unsaved: program);
        }

        // プレビューは「適用」したときに消す (TPL-40 の仕様 4)
        s.PreviewHidden = true;
        RefreshTemplateHighlights();
    }

    /// <summary>保存先の既定のファイル名 <c>inferred-&lt;ドキュメントの名前&gt;.hxt</c>。</summary>
    internal static string InferredFileName(string documentName)
    {
        string stem = Path.GetFileNameWithoutExtension(documentName);
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            stem = stem.Replace(c, '_');
        }

        return "inferred-" + stem + ".hxt";
    }

    /// <summary>「エディタで開く」: 下書きを内容とする未保存の新しいテンプレートエディタのタブ (対象は推定したドキュメント)。</summary>
    public void OpenInferenceInEditor()
    {
        if (Vm.Selected is not { } doc || _inferenceSession is not { } s || CurrentInferenceDraft() is not { } draft)
        {
            return;
        }

        TemplateEditorPage page = OpenNewTemplateEditor(draft);
        page.SetSuggestedPath(Path.Combine(TemplateService.DefaultUserFolder, InferredFileName(InferenceDocumentName(s))));
        page.MarkUnsaved();
        page.SelectTarget(doc);
    }

    public void CopyInferenceDraft()
    {
        if (CurrentInferenceDraft() is not { } draft)
        {
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(draft);
        SystemClipboard.SetContent(package);
        ShowStatusMessage(Loc.Get("TplInfer_Copied"));
    }

    // ───────────── Hex ビューとの連動 (TPL-40 の仕様 4) ─────────────

    public void InferencePanelVisibilityChanged()
    {
        RefreshTemplateHighlights();
    }

    /// <summary>表の行を選んだ: カーソルのあるレコード (範囲の外なら最初のレコード) のそのフィールドの範囲を選ぶ。</summary>
    public void InferenceRowSelected(int index)
    {
        if (Vm.Selected is not { } doc || _inferenceSession is not { Table.Result: { RecordLength: int k } r } s || index < 0 || index >= s.Table.Rows.Count)
        {
            return;
        }

        InferenceRow row = s.Table.Rows[index];
        EditorState editor = doc.Editor;
        long cursor = editor.Cursor;
        long record = cursor >= r.Start && cursor < r.Start + (k * r.RecordCount) ? (cursor - r.Start) / k : 0;
        _inferenceSelecting = true;
        try
        {
            editor.Select(r.Start + (record * k) + row.Offset, row.Size);
        }
        finally
        {
            _inferenceSelecting = false;
        }
    }

    /// <summary>Hex ビューのカーソルを範囲の中で動かすと、その位置のフィールドの行を表で選ぶ。</summary>
    private void InferenceDocument_EditorChanged(object? sender, EventArgs e)
    {
        if (_inferenceSelecting || sender is not DocumentViewModel doc || _inferencePanel is null
            || _inferenceSession is not { Table.Result: { RecordLength: int k } r } s || s.Document != doc.Document)
        {
            return;
        }

        long cursor = doc.Editor.Cursor;
        if (cursor < r.Start || cursor >= r.Start + (k * r.RecordCount))
        {
            return;
        }

        int index = s.Table.RowAt((int)((cursor - r.Start) % k));
        _inferencePanel.SelectRowQuietly(index);
    }

    /// <summary>Hex ビューにプレビューを付ける (<see cref="AttachTemplates"/> から呼ぶ)。</summary>
    private void AttachTemplateInference(HexView view) =>
        view.SetHighlightSource("templateInference", (start, end) => InferenceHighlights(view, start, end));

    /// <summary>表示中の行に入るレコードだけのフィールドの範囲 (テンプレートの色分けと同じ色) と、レコードの境目の線。</summary>
    private IEnumerable<HexHighlight> InferenceHighlights(HexView view, long start, long end)
    {
        if (!InferencePanelShown || DocumentOf(view) is not { } doc || TemplateInferenceSession.Find(doc.Document) is not { PreviewHidden: false } s
            || !ReferenceEquals(s, _inferenceSession) || s.Table.Result is not { RecordLength: int k } r || s.Table.Rows.Count == 0)
        {
            yield break;
        }

        long rangeEnd = r.Start + (k * r.RecordCount);
        long from = Math.Max(start, r.Start), to = Math.Min(end, rangeEnd);
        if (from >= to)
        {
            yield break;
        }

        bool hc = view.IsHighContrast;
        IReadOnlyList<InferenceRow> rows = s.Table.Rows;
        for (long rec = (from - r.Start) / k; rec <= (to - 1 - r.Start) / k; rec++)
        {
            long recStart = r.Start + (rec * k);

            // レコードの境目の線 (長さ 0 の範囲は位置に縦線を描く)
            yield return new HexHighlight(recStart, 0, CellLayer.Template, null, TemplateBrushes.Line("TemplateBoundaryBrush", view, hc), null,
                "templateInferenceRecord", Thickness: 2);
            for (int i = 0; i < rows.Count; i++)
            {
                long fieldStart = recStart + rows[i].Offset;
                if (fieldStart >= end || fieldStart + rows[i].Size <= start)
                {
                    continue;
                }

                // ハイコントラストでは塗らずに境目の線をシステム色で描く (TPL-23 の仕様 4)
                yield return hc
                    ? new HexHighlight(fieldStart, rows[i].Size, CellLayer.Template, null, TemplateBrushes.Line("TemplateBoundaryBrush", view, true), null,
                        "templateInference:" + i.ToString(CultureInfo.InvariantCulture))
                    : new HexHighlight(fieldStart, rows[i].Size, CellLayer.Template,
                        TemplateBrushes.Background(TemplateColor.Auto(i), view, TemplateService.Opacity), null, null,
                        "templateInference:" + i.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Hex ビューの右クリックメニュー「選択範囲からテンプレートを推定...」(<see cref="ExtendHexViewTemplateMenu"/> から呼ぶ)。</summary>
    private void ExtendHexViewInferenceMenu(MenuFlyout menu)
    {
        const string Id = "HexViewMenu_TemplateInfer";
        if (!menu.Items.Any(i => AutomationProperties.GetAutomationId(i) == Id))
        {
            var item = new MenuFlyoutItem { Text = Loc.Get("HexView_Menu_TemplateInfer"), Tag = "TemplateInfer" };
            AutomationProperties.SetAutomationId(item, Id);
            item.Click += (_, _) => _ = Commands.ExecuteAsync("templates.infer");
            menu.Items.Add(item);
        }
    }
}
