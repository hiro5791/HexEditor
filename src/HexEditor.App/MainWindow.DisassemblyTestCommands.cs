#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Disassembly;
using HexEditor.Core.Disassembly.Images;
using HexEditor.Core.Inspector;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace HexEditor.App;

/// <summary>逆アセンブル (ANA-23〜ANA-30、INSP-16) のテスト用の命令 ("disasm")。</summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleDisassemblyTestCommandsAsync(string cmd, JsonObject request) => cmd switch
    {
        "disasm" => await TestDisassemblyAsync(request),
        _ => null,
    };

    /// <summary>
    /// "disasm": {action: show / state / setArch / setEndian / setSlice / setBase / goTo / selectRow / key / f12 / references / pattern / assemble /
    /// address / entry / msil / method / symbols / symbol / format / menu / export / layout / demangle / inspector / inspectorBranch}。
    /// </summary>
    private async Task<JsonObject> TestDisassemblyAsync(JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        DisassemblyViewModel vm = DisasmVm;
        var result = new JsonObject();
        DisasmRow Row() => vm.Rows[(int)TestHookSettings.ReadLong(request["index"], 0)];
        switch (action)
        {
            case "show":
                ShowPanel(DisassemblyPanelId, focus: request["focus"]?.GetValue<bool>() ?? false);
                await vm.WhenImageReadyAsync();
                _disasmPanel?.UpdateLayout();
                break;
            case "setArch":
                vm.SetArchitecture(request["id"]!.GetValue<string>());
                _disasmPanel?.FormatChanged();
                break;
            case "setEndian":
                vm.SetEndian(request["big"]?.GetValue<bool>());
                break;
            case "setSlice":
                await vm.SetSliceAsync(vm.Image.Slices[(int)TestHookSettings.ReadLong(request["slice"], 0)]);
                break;
            case "setBase":
                vm.SetBaseAddress(request["base"] is { } b ? (ulong)TestHookSettings.ReadLong(b, 0) : null);
                break;
            case "goTo":
                vm.GoToOffset(TestHookSettings.ReadLong(request["offset"], 0), request["activate"]?.GetValue<bool>() ?? false);
                break;
            case "selectRow":
                vm.Select(Row());
                break;
            case "key":
                _disasmPanel?.ListingView.Focus(FocusState.Keyboard);
                _disasmPanel?.ListingView.HandleKey(Enum.Parse<Windows.System.VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true));
                break;
            case "f12":
                // F12 のコマンド (templates.goToDefinition) を、パネル (panel: true) か Hex ビューにフォーカスを置いて実行する。
                if (request["panel"]?.GetValue<bool>() == true)
                {
                    _disasmPanel?.ListingView.Focus(FocusState.Keyboard);
                }
                else
                {
                    FocusEditor();
                }

                await Commands.ExecuteAsync("templates.goToDefinition");
                break;
            case "references":
                await FindDisasmReferencesAsync(Row());
                result["references"] = new JsonArray([.. LastDisasmReferences.Select(r => (JsonNode?)new JsonObject
                {
                    ["address"] = (long)r.Address,
                    ["offset"] = r.Offset,
                    ["text"] = r.Text,
                    ["kind"] = r.Kind.ToString(),
                })]);
                break;
            case "pattern":
            {
                string? error = await (_disasmPanel?.FindPatternForTestAsync(request["text"]!.GetValue<string>(), request["regex"]?.GetValue<bool>() ?? false,
                    (int)TestHookSettings.ReadLong(request["direction"], 0)) ?? Task.FromResult<string?>(null));
                result["error"] = error;
                result["matches"] = new JsonArray([.. LastDisasmPatternMatches.Select(m => (JsonNode?)new JsonObject
                {
                    ["address"] = (long)m.Address,
                    ["offset"] = m.Offset,
                    ["text"] = m.Text,
                })]);
                break;
            }

            case "assemble":
            {
                ShowAssembleFlyout(Row());
                if (PendingAssemble is { } state)
                {
                    if (request["text"]?.GetValue<string>() is { } text)
                    {
                        // TextChanged は後で届くので、ここで解釈し直す。
                        state.Input.Text = text;
                        UpdateAssemble(state);
                    }

                    if (request["fill"]?.GetValue<string>() == "keep")
                    {
                        state.FillNop.IsChecked = false;
                    }

                    result["bytes"] = state.BytesText.Text;
                    result["warning"] = state.Warning.Text;
                    result["canWrite"] = state.Write.IsEnabled;
                    result["length"] = state.Result?.Bytes.Length;
                    result["hex"] = state.Result is { } r ? Convert.ToHexString(r.Bytes) : null;
                    if (request["write"]?.GetValue<bool>() == true && state.Write.IsEnabled)
                    {
                        WriteAssembled(state);
                        result["written"] = true;
                    }
                    else if (request["close"]?.GetValue<bool>() != false)
                    {
                        state.Flyout.Hide();
                    }
                }

                break;
            }

            case "address":
            {
                if (Editor is { } editor)
                {
                    (long? offset, string info) = ResolveVirtualAddress(request["text"]!.GetValue<string>(), editor);
                    result["offset"] = offset;
                    result["info"] = info;
                    if (offset is { } o && request["go"]?.GetValue<bool>() == true)
                    {
                        editor.GoTo(o);
                        vm.GoToOffset(o);
                    }
                }

                break;
            }

            case "entry":
                await GoToEntryPointAsync();
                break;
            case "msil":
                await DisassembleDotNetAsync();
                break;
            case "method":
                if (_disasmPanel is { } p)
                {
                    await p.SelectMethodForTestAsync(request["type"]!.GetValue<string>(), request["name"]!.GetValue<string>());
                }

                break;
            case "native":
                result["shown"] = _disasmPanel?.ShowNativeCode() ?? false;
                break;
            case "symbols":
                _disasmPanel?.FilterSymbols(request["filter"]?.GetValue<string>() ?? string.Empty);
                result["symbols"] = new JsonArray([.. vm.SymbolsMatching(request["filter"]?.GetValue<string>() ?? string.Empty)
                    .Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["address"] = (long)s.Symbol.Address, ["kind"] = s.Symbol.Kind.ToString() })]);
                break;
            case "symbol":
                if (_disasmPanel is { } sp && vm.SymbolsMatching(request["name"]!.GetValue<string>()).FirstOrDefault() is { } symbol)
                {
                    sp.ShowSymbol(symbol);
                }

                break;
            case "format":
            {
                DisasmFormat f = vm.Format;
                f = f with
                {
                    Syntax = request["syntax"]?.GetValue<string>() is { } syntax ? syntax switch { "att" => DisasmSyntax.Att, "masm" => DisasmSyntax.Masm, _ => DisasmSyntax.Intel } : f.Syntax,
                    HexStyle = request["hexStyle"]?.GetValue<string>() is { } hs ? hs == "suffix" ? HexStyle.Suffix : HexStyle.Prefix : f.HexStyle,
                    UppercaseHex = request["uppercaseHex"]?.GetValue<bool>() ?? f.UppercaseHex,
                    MaxBytes = (int)TestHookSettings.ReadLong(request["maxBytes"], f.MaxBytes),
                    AddressColumn = request["addressColumn"]?.GetValue<string>() is { } ac
                        ? ac switch { "offset" => AddressColumn.Offset, "both" => AddressColumn.Both, _ => AddressColumn.Virtual }
                        : f.AddressColumn,
                };
                SaveDisasmFormat(f);
                break;
            }

            case "menu":
                if (_disasmPanel is { } mp)
                {
                    result["items"] = new JsonArray([.. mp.RowMenuItems(Row()).Select(i => (JsonNode?)new JsonObject { ["id"] = i.Id, ["enabled"] = i.Enabled })]);
                }

                break;
            case "export":
                TestExportPath = request["path"]!.GetValue<string>();
                try
                {
                    await ExportDisassemblyAsync();
                }
                finally
                {
                    TestExportPath = null;
                }

                break;
            case "layout":
                if (_disasmPanel is { } lp)
                {
                    lp.UpdateLayout();
                    result["listingFlowDirection"] = lp.ListingView.FlowDirection.ToString();
                    result["toolbarFlowDirection"] = lp.ToolbarElement.FlowDirection.ToString();
                    int first = vm.Rows.ToList().FindIndex(r => r.Kind == ListingLineKind.Instruction);
                    if (first >= 0 && lp.ListingView.RowElement(first) is { } row0 && row0.XamlRoot is not null)
                    {
                        // 行の中の列の画面上の横位置 (アドレス列とニーモニック列)。
                        var texts = new List<Microsoft.UI.Xaml.Controls.TextBlock>();
                        CollectTextBlocks(row0, texts);
                        Microsoft.UI.Xaml.Controls.TextBlock? address = texts.FirstOrDefault(t => t.Text.Length > 0);
                        Microsoft.UI.Xaml.Controls.TextBlock? mnemonic = texts.FirstOrDefault(t => t.FontWeight.Weight >= 600 && t.Text.Length > 0);
                        if (address is not null && mnemonic is not null)
                        {
                            result["addressLeft"] = address.TransformToVisual(null).TransformPoint(new Point(0, 0)).X;
                            result["mnemonicLeft"] = mnemonic.TransformToVisual(null).TransformPoint(new Point(0, 0)).X;
                        }
                    }
                }

                break;
            case "demangle":
                OpenDemangle(fromSelection: request["fromSelection"]?.GetValue<bool>() ?? false);
                if (request["text"]?.GetValue<string>() is { } input)
                {
                    _demanglePage!.SetInput(input);
                }

                if (_demanglePage is { } page)
                {
                    await page.ConvertNowAsync();
                }

                result["output"] = _demanglePage?.OutputText;
                break;
            case "inspector":
            {
                ShowPanel("inspector", focus: false);
                InspectorLayout layout = _inspectorVm.Layout;
                string current = layout.Rows.First(r => InspectorTypes.IsDisasm(r.TypeId)).TypeId;
                layout = layout.WithVisible(current, true);
                if (request["arch"]?.GetValue<string>() is { } arch)
                {
                    layout = layout.WithDisasmArchitecture(current, arch);
                }

                _inspectorVm.SetLayout(layout);
                _inspectorVm.Refresh();
                InspectorItemViewModel? item = _inspectorVm.Items.FirstOrDefault(i => i.Kind == InspectorItemKind.Row && InspectorTypes.IsDisasm(i.TypeId));
                result["name"] = item?.Name;
                result["value"] = item?.Value;
                result["branchTarget"] = item?.BranchTarget;
                break;
            }

            case "inspectorBranch":
            {
                InspectorItemViewModel? item = _inspectorVm.Items.FirstOrDefault(i => i.Kind == InspectorItemKind.Row && InspectorTypes.IsDisasm(i.TypeId));
                if (item?.BranchTarget is { } target)
                {
                    GoToInspectorBranch(target);
                    result["moved"] = true;
                }

                break;
            }

            case "state":
                break;
            default:
                throw new ArgumentException($"Unknown disasm action: {action}");
        }

        AddDisassemblyState(result);
        return result;
    }

    private static void CollectTextBlocks(DependencyObject root, List<Microsoft.UI.Xaml.Controls.TextBlock> list)
    {
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.TextBlock t && t.Visibility == Visibility.Visible)
            {
                list.Add(t);
            }

            CollectTextBlocks(child, list);
        }
    }

    private void AddDisassemblyState(JsonObject result)
    {
        DisassemblyViewModel vm = DisasmVm;
        result["open"] = IsDisassemblyPanelOpen;
        result["architecture"] = vm.ArchitectureId;
        result["imageFormat"] = vm.Image.Format.ToString();
        result["imageBase"] = (long)vm.Image.ImageBase;
        result["entryPoint"] = vm.Image.EntryPoint is { } e ? (long)e : null;
        result["isDotNet"] = vm.Image.IsDotNet;
        result["slices"] = new JsonArray([.. vm.Image.Slices.Select(s => (JsonNode?)s.Name)]);
        result["selectedOffset"] = vm.SelectedOffset;
        result["follow"] = vm.FollowCursor;
        result["labelsScanned"] = vm.LabelsScanned;
        result["lastStatus"] = LastDisasmStatus;
        result["engineError"] = vm.EngineError;
        result["mostlyData"] = vm.MostlyData;
        result["method"] = vm.CurrentMethod?.Name;
        result["format"] = new JsonObject
        {
            ["syntax"] = DisasmSettings.SyntaxValue(vm.Format.Syntax),
            ["hexStyle"] = vm.Format.HexStyle == HexStyle.Suffix ? "suffix" : "prefix",
            ["uppercaseHex"] = vm.Format.UppercaseHex,
            ["maxBytes"] = vm.Format.MaxBytes,
            ["addressColumn"] = DisasmSettings.AddressColumnValue(vm.Format.AddressColumn),
        };
        result["settings"] = new JsonObject([.. DisasmSettings.Keys.Select(k => new KeyValuePair<string, JsonNode?>(k, App.Settings.GetNode(k)?.DeepClone()))]);
        DisasmRow? selected = vm.SelectedRow;
        result["rows"] = new JsonArray([.. vm.Rows.Select((r, i) => (JsonNode?)new JsonObject
        {
            ["index"] = i,
            ["kind"] = r.Kind.ToString(),
            ["offset"] = r.Offset,
            ["length"] = r.Length,
            ["address"] = r.Address,
            ["bytes"] = r.Bytes,
            ["text"] = r.Instruction,
            ["comment"] = r.Comment,
            ["label"] = r.Label,
            ["target"] = r.TargetOffset,
            ["selected"] = selected is not null && r == selected,
        })]);
        if (_disasmPanel is { } panel)
        {
            var t = panel.ToolbarState;
            result["toolbar"] = new JsonObject
            {
                ["architecture"] = t.Architecture,
                ["mode"] = t.Mode,
                ["endian"] = t.Endian,
                ["base"] = t.Base,
                ["syntax"] = t.Syntax,
                ["follow"] = t.Follow,
                ["sliceVisible"] = t.SliceVisible,
                ["families"] = new JsonArray([.. t.Families.Select(f => (JsonNode?)f)]),
            };
            var bars = panel.BarsState;
            result["bars"] = new JsonObject { ["engine"] = bars.Engine, ["data"] = bars.Data, ["header"] = bars.Header, ["dotNet"] = bars.DotNet };
            result["arrows"] = panel.ListingView.ArrowCount;
        }

        if (Editor is { } editor)
        {
            result["cursor"] = editor.Cursor;
            result["selectionStart"] = editor.HasSelection ? editor.SelectionStart : null;
            result["selectionLength"] = editor.HasSelection ? editor.SelectionLength : 0;
        }
    }
}
#endif
