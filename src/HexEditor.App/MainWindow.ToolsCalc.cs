using HexEditor.App.Commands;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Hashing;
using HexEditor.Core.Panels;
using HexEditor.Core.Tools;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 電卓と変換・参照ツール (TOOL-22〜TOOL-28) のつなぎ込み: パネルの登録、アクティブな文書・カーソルへの追従、カーソル位置への書き込み、
/// 検索バーへの送信。パネルはウィンドウごとに 1 つ。
/// </summary>
public sealed partial class MainWindow
{
    public const string CalculatorPanelId = "calculator";
    public const string ConverterPanelId = "converter";
    public const string FloatPanelId = "floatviz";
    public const string CharTablePanelId = "chartable";
    public const string ColorPanelId = "colorpicker";
    public const string TimePanelId = "datetime";

    private ToolHost? _toolHost;
    private DocumentViewModel? _toolDocument;
    private bool _toolChangeQueued;

    /// <summary>ツールのパネルが使う口。</summary>
    internal ToolHost Tools => _toolHost ??= CreateToolHost();

    /// <summary>ツールのパネルの登録 (アプリの起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterToolsCalcPanels()
    {
        RegisterToolPanel(CalculatorPanelId, "Panel_Calculator_Title", w => new CalculatorPanel(w.Tools));
        RegisterToolPanel(ConverterPanelId, "Panel_Converter_Title", w => new ConverterPanel(w.Tools));
        RegisterToolPanel(FloatPanelId, "Panel_Float_Title", w => new FloatPanel(w.Tools));
        RegisterToolPanel(CharTablePanelId, "Panel_CharTable_Title", w => new CharTablePanel(w.Tools));
        RegisterToolPanel(ColorPanelId, "Panel_Color_Title", w => new ColorPanel(w.Tools));
        RegisterToolPanel(TimePanelId, "Panel_DateTime_Title", w => new TimePanel(w.Tools));
        RegisterToolsFilesPanels();
    }

    private static void RegisterToolPanel(string id, string titleKey, Func<MainWindow, FrameworkElement> create)
    {
        if (PanelRegistry.Find(id) is null)
        {
            // 文書がなくても開ける(電卓・変換は文書を使わずに使える)。ウィンドウごとに 1 つの部品を使うので、浮動パネルにはしない。
            // メニュー「ツール > 電卓」などは、開くだけ(開いていても閉じない)。表示の切り替えのコマンドは使わない。
            PanelRegistry.Register(new PanelRegistration(id, titleKey, PanelDock.Right, ctx => create((MainWindow)ctx.Window))
            {
                ToggleCommand = "view.panel." + id,
                RequiresDocument = false,
                CanFloat = false,
            });
        }
    }

    private ToolHost CreateToolHost() => new()
    {
        Document = () => Vm.Selected,
        Write = ToolWrite,
        SendToSearch = ToolSendToSearch,
        GoTo = offset => Vm.Selected?.Editor.GoTo(offset),
        CanEdit = () => NeedsEditable().Enabled,
        Notify = (message, error) => ShowNotice(message, error ? InfoBarSeverity.Error : InfoBarSeverity.Informational, Vm.Selected),
    };

    private void RegisterToolsCalcCommands()
    {
        foreach ((string command, string panel) in new[]
        {
            ("tools.calculator", CalculatorPanelId), ("tools.converter", ConverterPanelId), ("tools.floatVisualizer", FloatPanelId),
            ("tools.charTable", CharTablePanelId), ("tools.colorPicker", ColorPanelId), ("tools.dateTime", TimePanelId),
        })
        {
            string captured = panel;
            Commands.Register(command, () => ShowPanel(captured));
        }

        // 電卓のプログラマモード (TOOL-23): 電卓のパネルを開いて、プログラマに切り替える。
        Commands.Register("tools.calculator.programmer", () =>
        {
            ShowPanel(CalculatorPanelId);
            FindPanelContent<CalculatorPanel>(CalculatorPanelId)?.ShowProgrammer();
        });
        RegisterToolsFilesCommands();
    }

    /// <summary>パネルを開き、中身を返す(コマンドとインスペクタの右クリックから開くとき)。</summary>
    internal T? OpenToolPanel<T>(string id, bool focus = false)
        where T : FrameworkElement
    {
        _ = Tools;
        if (focus || !IsPanelShown(id))
        {
            ShowPanel(id, focus);
        }

        return FindPanelContent<T>(id);
    }

    /// <summary>データインスペクタの float / double の行の右クリック「ビジュアライザで開く」(TOOL-25)。</summary>
    internal void ShowFloatVisualizer(FloatLayout layout, UInt128 bits) => OpenToolPanel<FloatPanel>(FloatPanelId, focus: true)?.Show(layout, bits);

    /// <summary>データインスペクタの色の型の行の右クリック「カラーピッカーで開く」(TOOL-27)。</summary>
    internal void ShowColorPicker(ColorValue color) => OpenToolPanel<ColorPanel>(ColorPanelId, focus: true)?.SetColor(color, fromPicker: false);

    /// <summary>データインスペクタの行の右クリック「ビジュアライザで開く」「カラーピッカーで開く」(TOOL-25・TOOL-27)。</summary>
    internal void OpenInspectorRowInTool(InspectorItemViewModel item)
    {
        if (item.Rgba is { } rgba)
        {
            ShowColorPicker(new ColorValue((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba));
            return;
        }

        FloatLayout? layout = item.TypeId switch
        {
            Core.Inspector.InspectorTypes.Float32 => FloatLayout.Single,
            Core.Inspector.InspectorTypes.Float64 => FloatLayout.Double,
            Core.Inspector.InspectorTypes.Half => FloatLayout.Half,
            Core.Inspector.InspectorTypes.BFloat16 => FloatLayout.BFloat16,
            Core.Inspector.InspectorTypes.Float80 => FloatLayout.X87,
            _ => null,
        };
        if (layout is null || Vm.Selected is not { } doc)
        {
            return;
        }

        byte[] bytes = new byte[layout.Bytes];
        if (!doc.Document.Current.Read(_inspectorVm.Origin, bytes).IsComplete)
        {
            return;
        }

        bool big = (_inspectorVm.Endian == Core.Inspector.Endianness.Big) != item.Opposite;
        if (FloatAnalyzer.FromBytes(layout, bytes, big) is { } bits)
        {
            ShowFloatVisualizer(layout, bits);
        }
    }

    // ---- カーソル位置への書き込み ----

    /// <summary>
    /// バイト列をカーソル位置 (選択範囲があればその先頭) に書き込む。Undo 1 回分。<paramref name="followMode"/> なら挿入モードで挿入し、
    /// 書いた後にカーソルを進める (文字コード表からの入力)。そうでなければ上書き (長さが変わらない)。
    /// </summary>
    private bool ToolWrite(byte[] bytes, string undoName, bool followMode)
    {
        if (Vm.Selected is not { } doc)
        {
            ShowNotice(Loc.Get("Command_NoDocument"), InfoBarSeverity.Error);
            return false;
        }

        if (!EnsureEditable(doc))
        {
            return false;
        }

        EditorState editor = doc.Editor;
        long offset = editor.HasSelection ? editor.SelectionStart : editor.Cursor;
        bool insert = followMode && editor.InsertMode && doc.Document.CanResize;
        if (!insert && HashWriteBack.Shortage(offset, bytes.Length, doc.Document.Length) > 0)
        {
            ShowNotice(Loc.Format("Tool_Error_End", doc.Document.Length - offset, bytes.Length), InfoBarSeverity.Error, doc);
            return false;
        }

        bool done = ApplyEdit(doc, () =>
        {
            if (insert)
            {
                doc.Document.Insert(offset, bytes, undoName);
            }
            else
            {
                doc.Document.Overwrite(offset, bytes, undoName);
            }
        });
        if (done && followMode)
        {
            editor.GoTo(Math.Min(offset + bytes.Length, editor.Layout.MaxCursor));
        }

        return done;
    }

    /// <summary>検索バーを開き、Hex の検索条件にする (「検索バーに送る」「この色を検索」)。</summary>
    private void ToolSendToSearch(byte[] bytes)
    {
        if (Editor is null)
        {
            return;
        }

        OpenFindBar(replace: false);
        FindBar.SetHexQuery(bytes);
    }

    // ---- アクティブな文書・カーソルへの追従 ----

    /// <summary>アクティブな文書が変わった (<see cref="WatchSelectedForTitle"/> から呼ぶ)。パネルに知らせ、カーソルの移動も追う。</summary>
    private void OnToolDocumentChanged()
    {
        if (_toolHost is null)
        {
            return;
        }

        if (_toolDocument is not null)
        {
            _toolDocument.EditorChanged -= ToolEditorChanged;
            _toolDocument.Document.Changed -= ToolDocumentContentChanged;
        }

        _toolDocument = Vm.Selected;
        if (_toolDocument is not null)
        {
            _toolDocument.EditorChanged += ToolEditorChanged;
            _toolDocument.Document.Changed += ToolDocumentContentChanged;
        }

        QueueToolChanged();
    }

    private void ToolEditorChanged(object? sender, EventArgs e) => QueueToolChanged();

    private void ToolDocumentContentChanged(object? sender, Core.Engine.DocumentChangedEventArgs e) => QueueToolChanged();

    /// <summary>同じフレームの変化をまとめて、パネルに知らせる。</summary>
    private void QueueToolChanged()
    {
        if (_toolChangeQueued)
        {
            return;
        }

        _toolChangeQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _toolChangeQueued = false;
            _toolHost?.RaiseChanged();
        });
    }

    /// <summary>ウィンドウを閉じるとき、文書のイベントの購読を外す。</summary>
    private void DetachTools()
    {
        if (_toolDocument is not null)
        {
            _toolDocument.EditorChanged -= ToolEditorChanged;
            _toolDocument.Document.Changed -= ToolDocumentContentChanged;
            _toolDocument = null;
        }
    }
}
