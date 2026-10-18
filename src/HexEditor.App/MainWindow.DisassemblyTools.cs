using System.Globalization;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Disassembly;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HexEditor.App;

/// <summary>
/// インプレースアセンブル (06 の ANA-29) のフライアウトと、デマングル (ANA-30) のページ。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>開いているアセンブルのフライアウトの状態 (テスト用の命令が操作する)。</summary>
    internal sealed class AssembleState
    {
        public required DisasmRow Row { get; init; }

        public required TextBox Input { get; init; }

        public required TextBlock BytesText { get; init; }

        public required TextBlock Warning { get; init; }

        public required RadioButton FillNop { get; init; }

        public required Button Write { get; init; }

        public required Flyout Flyout { get; init; }

        public AssembleResult? Result { get; set; }

        public string? Error { get; set; }
    }

    internal AssembleState? PendingAssemble { get; private set; }

    /// <summary>
    /// 行の位置に開く編集欄 (ANA-29 の画面)。入力に合わせて機械語のバイト列と長さを表示し (仕様 2)、短ければ NOP で埋めるか選ばせ (仕様 3)、
    /// 長ければ上書きされる命令を警告する (仕様 4)。書き込みは上書きで、Undo 1 回分 (仕様 5)。
    /// </summary>
    private void ShowAssembleFlyout(DisasmRow row)
    {
        if (row.Native?.Instruction is not { } original || Vm.Selected is not { } doc || !DisasmVm.Architecture.CanAssemble)
        {
            return;
        }

        if (doc.Document.IsReadOnly)
        {
            ShowStatusMessage(Loc.Get("Command_ReadOnly"));
            return;
        }

        var input = new TextBox
        {
            Text = original.Text,
            FontFamily = DialogParts.Mono,
            FlowDirection = FlowDirection.LeftToRight,
            MinWidth = 320,
            Header = Loc.Get("Disasm_AssembleInput"),
        };
        AutomationProperties.SetAutomationId(input, "Disasm_AssembleInput");
        TextBlock bytes = DialogParts.Caption("Disasm_AssembleBytes", monospace: true);
        TextBlock warning = DialogParts.Caption("Disasm_AssembleWarning");
        warning.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        RadioButton nop = DialogParts.Radio("Disasm_AssembleFillNop", Loc.Get("Disasm_AssembleFillNop"), "disasmFill", true);
        RadioButton keep = DialogParts.Radio("Disasm_AssembleKeep", Loc.Get("Disasm_AssembleKeep"), "disasmFill", false);
        var write = new Button { Content = Loc.Get("Disasm_AssembleWrite"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetAutomationId(write, "Disasm_AssembleWrite");
        var cancel = new Button { Content = Loc.Get("Common_Cancel") };
        AutomationProperties.SetAutomationId(cancel, "Disasm_AssembleCancel");
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    input, bytes, warning, nop, keep,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { write, cancel } },
                },
            },
        };
        var state = new AssembleState { Row = row, Input = input, BytesText = bytes, Warning = warning, FillNop = nop, Write = write, Flyout = flyout };
        PendingAssemble = state;
        input.TextChanged += (_, _) => UpdateAssemble(state);
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && write.IsEnabled)
            {
                WriteAssembled(state);
                e.Handled = true;
            }
        };
        write.Click += (_, _) => WriteAssembled(state);
        cancel.Click += (_, _) => flyout.Hide();
        flyout.Closed += (_, _) =>
        {
            if (PendingAssemble == state)
            {
                PendingAssemble = null;
            }
        };
        UpdateAssemble(state);
        FrameworkElement? anchor = _disasmPanel?.ListingView;
        if (anchor?.XamlRoot is not null)
        {
            int index = DisasmVm.Rows.ToList().IndexOf(row);
            FrameworkElement? element = index >= 0 ? _disasmPanel!.ListingView.RowElement(index) : null;
            flyout.ShowAt(element ?? anchor, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        }
    }

    private void UpdateAssemble(AssembleState state)
    {
        DisasmInstruction original = state.Row.Native!.Instruction!;
        state.Result = null;
        state.Error = null;
        state.Warning.Text = string.Empty;
        try
        {
            // アドレスはその行の仮想アドレス (相対分岐の計算。仕様 2)。
            AssembleResult result = X86Assembler.Assemble(state.Input.Text, original.Address, DisasmVm.Architecture.Bitness, DisasmVm.Format);
            state.Result = result;
            state.BytesText.Text = Loc.Format("Disasm_AssembleResult", DialogParts.Hex(result.Bytes), result.Bytes.Length);
            if (result.Bytes.Length > original.Length && DisasmVm.Listing is { } listing)
            {
                // 長い場合は上書きされる後ろの命令を一覧で警告する (仕様 4)。
                var overwritten = new List<string>();
                long end = state.Row.Offset + result.Bytes.Length;
                foreach (ListingLine l in listing.Lines(state.Row.Offset + original.Length, 32))
                {
                    if (l.Offset >= end)
                    {
                        break;
                    }

                    if (l.Kind == ListingLineKind.Instruction)
                    {
                        overwritten.Add(l.Instruction!.Text);
                    }
                }

                state.Warning.Text = Loc.Format("Disasm_AssembleOverwrites", string.Join("; ", overwritten));
            }

            state.FillNop.IsEnabled = result.Bytes.Length < original.Length;
            state.Write.IsEnabled = result.Bytes.Length > 0 && state.Row.Offset + result.Bytes.Length <= (Vm.Selected?.Document.Length ?? 0);
        }
        catch (AssembleException ex)
        {
            // アセンブルできない (構文エラー、そのアーキテクチャにない命令): 入力欄の下に理由を出し、書き込めなくする (「エラー」)。
            state.Error = Loc.Format("Disasm_AssembleError", Loc.Get("Disasm_AssembleError_" + ex.Reason), ex.Detail);
            state.BytesText.Text = state.Error;
            state.Write.IsEnabled = false;
        }
    }

    private void WriteAssembled(AssembleState state)
    {
        if (state.Result is not { } result || Vm.Selected is not { } doc)
        {
            return;
        }

        int originalLength = state.Row.Native!.Instruction!.Length;
        byte[] data = result.Bytes;
        if (data.Length < originalLength && state.FillNop.IsChecked == true)
        {
            // 余りを NOP (0x90) で埋める (仕様 3 の既定)。
            data = [.. data, .. Enumerable.Repeat((byte)0x90, originalLength - data.Length)];
        }

        // 挿入モードでも上書きで書き込む (命令の位置をずらさない。仕様 4)。1 回の上書きなので Undo 1 回分 (仕様 5)。
        doc.Document.Overwrite(state.Row.Offset, data, Loc.Get("Disasm_UndoAssemble"));
        state.Flyout.Hide();
        PendingAssemble = null;
        DisasmVm.Refresh();
    }

    // ---- デマングル (ANA-30) ----

    public const string DemanglePageId = "demangle";
    private DemanglePage? _demanglePage;

    /// <summary>「デマングル」のページを開く (タブ。モードレス)。選択範囲から開いたら、選択範囲を今の文字コードで文字列にして入れる (仕様 4)。</summary>
    internal void OpenDemangle(bool fromSelection)
    {
        _demanglePage ??= new DemanglePage();
        if (fromSelection && Editor is { HasSelection: true } editor)
        {
            long length = Math.Min(editor.SelectionLength, Core.Disassembly.Demangling.Demangler.MaxLength * 4);
            byte[] bytes = new byte[length];
            int read = editor.Document.Current.Read(editor.SelectionStart, bytes).BytesReturned;
            string text = editor.TextEncoding.Decode(bytes.AsSpan(0, read)).Replace('\0', '\n');
            _demanglePage.SetInput(text);
        }

        ShowToolPage(DemanglePageId, _demanglePage, Loc.Get("ToolPage_Demangle"), "");
        _demanglePage.FocusInput();
    }

    /// <summary>インスペクタの 1 命令の行の「分岐先へ移動」の対象 (起点、相対位置)。分岐命令でなければ null。</summary>
    internal static (long Origin, long Relative)? InspectorBranchOf(InspectorViewModel vm, InspectorItemViewModel? item) =>
        item is { BranchTarget: { } relative } && Core.Inspector.InspectorTypes.IsDisasm(item.TypeId) ? (vm.Origin, relative) : null;
}
