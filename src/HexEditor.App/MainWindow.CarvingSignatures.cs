using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// カービングのシグネチャの管理 (12 の FOR-18): 左に定義の一覧、右に編集欄。組み込みの定義は編集できず、有効・無効の切り替えと複製だけができる。
/// 入力の誤りは入力欄の赤枠と説明で示し、保存ボタンを無効にする。「この定義で試す」は選択範囲を対象に、見つかった件数と最初の 10 件の位置を示す。
/// </summary>
public sealed partial class MainWindow
{
    private void RegisterCarvingCommands()
    {
        Commands.Register("forensics.carving.signatures", () => ShowSignatureManagerAsync());
        Commands.Register("forensics.carving.run", () => ShowCarvingDialogAsync(), CarvingState);
    }

    private CommandState CarvingState() =>
        Vm.Selected is null ? CommandState.Unavailable(Loc.Get("Command_NoDocument"))
        : _carvingRunning ? CommandState.Unavailable(Loc.Get("Carving_AlreadyRunning"))
        : CommandState.Available;

    private static string SignatureErrorText(SignatureError e) => Loc.TryGet("Sig_Error_" + e.Code) is { } text ? (e.Detail.Length > 0 ? $"{text} ({e.Detail})" : text) : e.Code;

    private async Task ShowSignatureManagerAsync()
    {
        SignatureStore store = ForensicsService.Signatures;
        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, Width = 230, MinHeight = 360, MaxHeight = 520 };
        AutomationProperties.SetAutomationId(list, "Sig_List");
        AutomationProperties.SetName(list, Loc.Get("Sig_List"));

        TextBox name = DialogParts.Field("Sig_Name", Loc.Get("Sig_Name"), string.Empty, monospace: false);
        TextBox extension = DialogParts.Field("Sig_Extension", Loc.Get("Sig_Extension"), string.Empty);
        TextBox headers = DialogParts.Field("Sig_Headers", Loc.Get("Sig_Headers"));
        headers.AcceptsReturn = true;
        headers.TextWrapping = TextWrapping.NoWrap;
        headers.MinHeight = 60;
        TextBox headerOffset = DialogParts.Field("Sig_HeaderOffset", Loc.Get("Sig_HeaderOffset"), "0");
        TextBox footer = DialogParts.Field("Sig_Footer", Loc.Get("Sig_Footer"));
        TextBox footerInclude = DialogParts.Field("Sig_FooterInclude", Loc.Get("Sig_FooterInclude"));
        TextBox maxSize = DialogParts.Field("Sig_MaxSize", Loc.Get("Sig_MaxSize"), "10M");
        TextBlock maxInterpretation = DialogParts.Caption("Sig_MaxSizeInterpretation", monospace: true);
        TextBox minSize = DialogParts.Field("Sig_MinSize", Loc.Get("Sig_MinSize"), "0");
        CheckBox useSize = DialogParts.Check("Sig_UseSizeField", Loc.Get("Sig_UseSizeField"), false);
        TextBox sizeOffset = DialogParts.Field("Sig_SizeOffset", Loc.Get("Sig_SizeOffset"), "0");
        ComboBox sizeBits = DialogParts.Combo("Sig_SizeBits", Loc.Get("Sig_SizeBits"), ["8", "16", "24", "32", "40", "48", "56", "64"], 3);
        ComboBox sizeEndian = DialogParts.Combo("Sig_SizeEndian", Loc.Get("Sig_SizeEndian"), [Loc.Get("Sig_Little"), Loc.Get("Sig_Big")], 0);
        TextBox sizeAdd = DialogParts.Field("Sig_SizeAdd", Loc.Get("Sig_SizeAdd"), "0");
        string[] structureNames = [.. StructureValidators.Names.Order(StringComparer.OrdinalIgnoreCase)];
        TextBox structure = DialogParts.Field("Sig_Structure", Loc.Get("Sig_Structure"), string.Empty);
        TextBlock structureHint = DialogParts.Caption("Sig_StructureHint");
        structureHint.Text = Loc.Format("Sig_StructureHint", string.Join(", ", structureNames));
        structureHint.TextWrapping = TextWrapping.Wrap;
        CheckBox aligned = DialogParts.Check("Sig_Aligned", Loc.Get("Sig_Aligned"), true);
        CheckBox enabled = DialogParts.Check("Sig_Enabled", Loc.Get("Sig_Enabled"), true);
        TextBlock message = DialogParts.Caption("Sig_Message");
        message.TextWrapping = TextWrapping.Wrap;
        TextBlock tryResult = DialogParts.Caption("Sig_TryResult", monospace: true);
        tryResult.TextWrapping = TextWrapping.Wrap;

        Button Make(string id, string key) => new() { Content = Loc.Get(key), Tag = id };
        Button newButton = Make("Sig_New", "Sig_New"), duplicate = Make("Sig_Duplicate", "Sig_Duplicate"), delete = Make("Sig_Delete", "Sig_Delete"), save = Make("Sig_Save", "Sig_Save"),
            import = Make("Sig_Import", "Sig_Import"), export = Make("Sig_Export", "Sig_Export"), tryButton = Make("Sig_Try", "Sig_Try");
        foreach (Button b in new[] { newButton, duplicate, delete, save, import, export, tryButton })
        {
            AutomationProperties.SetAutomationId(b, (string)b.Tag);
        }

        TextBox[] textInputs = [name, extension, headers, headerOffset, footer, footerInclude, maxSize, minSize, sizeOffset, sizeAdd, structure];
        Control[] allInputs = [.. textInputs, useSize, sizeBits, sizeEndian, aligned];
        string? selectedName = null;
        bool isNew = false;
        bool loading = false;

        void Fill(CarvingSignature s)
        {
            loading = true;
            name.Text = s.Name;
            extension.Text = s.Extension;
            headers.Text = string.Join(Environment.NewLine, s.Headers);
            headerOffset.Text = s.HeaderOffset.ToString(CultureInfo.InvariantCulture);
            footer.Text = s.Footer ?? string.Empty;
            footerInclude.Text = s.FooterInclude?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            maxSize.Text = s.MaxSize.ToString(CultureInfo.InvariantCulture);
            minSize.Text = s.MinSize.ToString(CultureInfo.InvariantCulture);
            useSize.IsChecked = s.SizeField is not null;
            SizeFieldRule rule = s.SizeField ?? new SizeFieldRule();
            sizeOffset.Text = rule.Offset.ToString(CultureInfo.InvariantCulture);
            sizeBits.SelectedIndex = Math.Max(0, (rule.Bits / 8) - 1);
            sizeEndian.SelectedIndex = rule.BigEndian ? 1 : 0;
            sizeAdd.Text = rule.Add.ToString(CultureInfo.InvariantCulture);
            structure.Text = s.Structure ?? string.Empty;
            aligned.IsChecked = s.SectorAligned;
            enabled.IsChecked = s.Enabled;
            loading = false;
        }

        // 入力欄の値から定義を作る。数値の誤りは (欄, 理由) で返す。
        CarvingSignature? Gather(List<(Control Field, string Reason)> problems)
        {
            long Number(TextBox box, long fallback, string field)
            {
                if (box.Text.Trim().Length == 0)
                {
                    return fallback;
                }

                if (SizeInput.TryParse(box.Text, out long value))
                {
                    return value;
                }

                problems.Add((box, Loc.Format("Sig_Error_Number", field)));
                return fallback;
            }

            long offset = Number(headerOffset, 0, Loc.Get("Sig_HeaderOffset"));
            long max = Number(maxSize, CarvingSignature.DefaultMaxSize, Loc.Get("Sig_MaxSize"));
            long min = Number(minSize, 0, Loc.Get("Sig_MinSize"));
            long? include = footerInclude.Text.Trim().Length == 0 ? null : Number(footerInclude, 0, Loc.Get("Sig_FooterInclude"));
            SizeFieldRule? rule = null;
            if (useSize.IsChecked == true)
            {
                rule = new SizeFieldRule
                {
                    Offset = Number(sizeOffset, 0, Loc.Get("Sig_SizeOffset")),
                    Bits = (sizeBits.SelectedIndex + 1) * 8,
                    BigEndian = sizeEndian.SelectedIndex == 1,
                    Add = Number(sizeAdd, 0, Loc.Get("Sig_SizeAdd")),
                };
            }

            return new CarvingSignature
            {
                Name = name.Text.Trim(),
                Extension = extension.Text.Trim().TrimStart('.'),
                Headers = [.. headers.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                HeaderOffset = offset,
                Footer = footer.Text.Trim().Length == 0 ? null : footer.Text.Trim(),
                FooterInclude = include,
                MaxSize = max,
                MinSize = min,
                SizeField = rule,
                Structure = structure.Text.Trim().Length == 0 ? null : structure.Text.Trim(),
                SectorAligned = aligned.IsChecked == true,
                Enabled = enabled.IsChecked == true,
            };
        }

        Control FieldOf(string field) => field switch
        {
            "Name" => name,
            "Extension" => extension,
            "Headers" => headers,
            "HeaderOffset" => headerOffset,
            "Footer" => footer,
            "FooterInclude" => footerInclude,
            "MaxSize" or "MinSize" => field == "MaxSize" ? maxSize : minSize,
            _ => useSize.IsChecked == true ? sizeOffset : maxSize,
        };

        CarvingSignature? current = null;

        // 入力のたびに検証する: 赤枠と説明を出し、保存ボタンを無効にする。
        void Validate()
        {
            if (loading)
            {
                return;
            }

            foreach (Control c in textInputs)
            {
                DialogParts.MarkInvalid(c, false);
            }

            bool builtIn = !isNew && selectedName is not null && BuiltInSignatures.IsBuiltInName(selectedName);
            var problems = new List<(Control Field, string Reason)>();
            current = Gather(problems);
            if (!builtIn && current is not null)
            {
                foreach (SignatureError e in current.Validate())
                {
                    problems.Add((FieldOf(e.Field), SignatureErrorText(e)));
                }

                if (current.Name.Length > 0 && !store.IsNameFree(current.Name, isNew ? null : selectedName))
                {
                    problems.Add((name, SignatureErrorText(new SignatureError("Name", "NameInUse"))));
                }
            }

            foreach ((Control field, _) in problems)
            {
                DialogParts.MarkInvalid(field, true);
            }

            message.Text = string.Join(Environment.NewLine, problems.Select(p => p.Reason).Distinct());
            message.Foreground = Controls.Tools.ToolParts.ThemeBrush(problems.Count > 0 ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush");
            save.IsEnabled = !builtIn && problems.Count == 0 && (isNew || selectedName is not null);
            maxInterpretation.Text = SizeInput.TryParse(maxSize.Text, out long m) ? DialogParts.Interpretation(m) : string.Empty;
            bool hasSelection = Vm.Selected?.Editor.HasSelection == true;
            tryButton.IsEnabled = problems.Count == 0 && current is not null && hasSelection;
            ToolTipService.SetToolTip(tryButton, hasSelection ? null : Loc.Get("Sig_Try_NeedSelection"));
        }

        void Select(string? target)
        {
            selectedName = target;
            isNew = false;
            CarvingSignature? s = target is null ? null : store.Find(target);
            bool builtIn = s?.BuiltIn == true;
            foreach (Control c in allInputs)
            {
                c.IsEnabled = s is not null && !builtIn;
            }

            enabled.IsEnabled = s is not null;
            duplicate.IsEnabled = s is not null;
            delete.IsEnabled = s is not null && !builtIn;
            if (s is not null)
            {
                Fill(s);
            }

            tryResult.Text = string.Empty;
            Validate();
        }

        void Reload(string? select)
        {
            loading = true;
            list.Items.Clear();
            foreach (CarvingSignature s in store.All)
            {
                var item = new ListViewItem
                {
                    Content = (s.BuiltIn ? "◇ " : string.Empty) + s.Name + (s.Enabled ? string.Empty : " (" + Loc.Get("Sig_Disabled") + ")"),
                    Tag = s.Name,
                };
                AutomationProperties.SetName(item, s.Name + (s.BuiltIn ? ", " + Loc.Get("Sig_BuiltIn") : string.Empty) + (s.Enabled ? string.Empty : ", " + Loc.Get("Sig_Disabled")));
                AutomationProperties.SetAutomationId(item, "Sig_Item_" + s.Name);
                list.Items.Add(item);
                if (string.Equals(s.Name, select, StringComparison.OrdinalIgnoreCase))
                {
                    list.SelectedItem = item;
                }
            }

            loading = false;
            Select(select);
        }

        list.SelectionChanged += (_, _) =>
        {
            if (!loading && list.SelectedItem is ListViewItem item)
            {
                Select((string)item.Tag);
            }
        };
        foreach (TextBox box in textInputs)
        {
            box.TextChanged += (_, _) => Validate();
        }

        foreach (Control c in new Control[] { useSize, aligned })
        {
            ((ToggleButton)c).Checked += (_, _) => Validate();
            ((ToggleButton)c).Unchecked += (_, _) => Validate();
        }

        sizeBits.SelectionChanged += (_, _) => Validate();
        sizeEndian.SelectionChanged += (_, _) => Validate();
        enabled.Checked += (_, _) => EnabledChanged(true);
        enabled.Unchecked += (_, _) => EnabledChanged(false);

        void EnabledChanged(bool on)
        {
            if (loading || selectedName is null || isNew)
            {
                return;
            }

            // 組み込みの定義でも「有効」だけは切り替えられる。
            store.SetEnabled(selectedName, on);
            Reload(selectedName);
        }

        newButton.Click += (_, _) =>
        {
            list.SelectedItem = null;
            selectedName = null;
            isNew = true;
            foreach (Control c in allInputs)
            {
                c.IsEnabled = true;
            }

            enabled.IsEnabled = true;
            duplicate.IsEnabled = false;
            delete.IsEnabled = false;
            Fill(new CarvingSignature { Name = string.Empty, Extension = string.Empty });
            maxSize.Text = "10M";
            tryResult.Text = string.Empty;
            Validate();
            name.Focus(FocusState.Programmatic);
        };
        duplicate.Click += (_, _) =>
        {
            if (selectedName is not null)
            {
                Reload(store.Duplicate(selectedName).Name);
            }
        };
        delete.Click += (_, _) =>
        {
            if (selectedName is not null && store.Remove(selectedName))
            {
                Reload(null);
            }
        };
        save.Click += (_, _) =>
        {
            var problems = new List<(Control Field, string Reason)>();
            if (Gather(problems) is not { } signature || problems.Count > 0)
            {
                return;
            }

            try
            {
                CarvingSignature saved = isNew ? store.Add(signature) : store.Update(selectedName!, signature);
                Reload(saved.Name);
            }
            catch (SignatureInvalidException ex)
            {
                message.Text = string.Join(Environment.NewLine, ex.Errors.Select(SignatureErrorText));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message.Text = Loc.Format("Sig_Error_Save", ex.Message);
            }
        };
        import.Click += async (_, _) =>
        {
            IReadOnlyList<string> files = await PickFilesAsync("HexEditor.SignatureImport", multiple: false);
            if (files.Count == 0)
            {
                return;
            }

            try
            {
                SignatureImportResult result = store.Import(await File.ReadAllTextAsync(files[0]));
                message.Foreground = Controls.Tools.ToolParts.ThemeBrush("TextFillColorPrimaryBrush");
                message.Text = Loc.Format("Sig_Imported", result.Accepted.Count) + (result.Rejected.Count == 0 ? string.Empty
                    : Environment.NewLine + Loc.Format("Sig_Rejected", result.Rejected.Count) + Environment.NewLine
                        + string.Join(Environment.NewLine, result.Rejected.Select(r => $"{r.Name}: {string.Join("; ", r.Errors.Select(SignatureErrorText))}")));
                Reload(result.Accepted.FirstOrDefault()?.Name ?? selectedName);
                message.Text = message.Text;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SignatureFileException)
            {
                message.Text = Loc.Format("Sig_Error_Import", ex.Message);
            }
        };
        export.Click += async (_, _) =>
        {
            if (await PickForensicsSavePathAsync("carving-signatures.json", "HexEditor.SignatureExport") is not { } target)
            {
                return;
            }

            try
            {
                await File.WriteAllTextAsync(target, store.Export(selectedName is null ? null : [selectedName]));
                message.Text = Loc.Format("Sig_Exported", Path.GetFileName(target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message.Text = Loc.Format("Sig_Error_Save", ex.Message);
            }
        };
        tryButton.Click += async (_, _) =>
        {
            if (current is null || Vm.Selected is not { } doc || !doc.Editor.HasSelection)
            {
                return;
            }

            tryResult.Text = Loc.Get("Sig_Trying");
            tryResult.Text = await TrySignatureAsync(current, doc);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (Button b in new[] { newButton, duplicate, delete })
        {
            buttons.Children.Add(b);
        }

        var buttons2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (Button b in new[] { save, tryButton, import, export })
        {
            buttons2.Children.Add(b);
        }

        var form = new StackPanel { Spacing = 6, MinWidth = 420 };
        foreach (UIElement e in new UIElement[]
        {
            name, extension, headers, headerOffset, footer, footerInclude, maxSize, maxInterpretation, minSize, useSize, sizeOffset, sizeBits, sizeEndian, sizeAdd, structure,
            structureHint, aligned, enabled, message, buttons2, tryResult,
        })
        {
            form.Children.Add(e);
        }

        var left = new StackPanel { Spacing = 6 };
        left.Children.Add(list);
        left.Children.Add(buttons);
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(left);
        var formScroll = new ScrollViewer { Content = form, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(formScroll, 1);
        grid.Children.Add(formScroll);

        Reload(store.All.FirstOrDefault()?.Name);
        ContentDialog dialog = DialogParts.Dialog(Root, "SignatureManagerDialog", Loc.Get("Sig_Title"), grid, string.Empty);
        dialog.PrimaryButtonText = string.Empty;
        dialog.CloseButtonText = Loc.Get("Common_Close");
        dialog.DefaultButton = ContentDialogButton.Close;
        await ShowEditDialogAsync(dialog);
    }

    /// <summary>「この定義で試す」: 選択範囲を走査して、件数と最初の 10 件の位置を返す (長時間処理)。</summary>
    private async Task<string> TrySignatureAsync(CarvingSignature signature, DocumentViewModel doc)
    {
        Core.Engine.DocumentSnapshot snapshot = doc.Document.DataSnapshot;
        SearchRange range = new(doc.Editor.SelectionStart, doc.Editor.SelectionLength);
        try
        {
            CarvingResult result = await Vm.Operations.RunAsync(Loc.Get("Sig_TryOperation"), OperationKind.ReadOnly, doc.Document, range.Length, op => CarvingEngine.RunAsync(
                new SnapshotCarveData(snapshot),
                new CarvingOptions
                {
                    Signatures = [signature with { Enabled = true }],
                    Ranges = [range],
                    Export = false,
                    SectorSize = SectorSizeOf(doc),
                    Resolver = StructureResolver(),
                    Progress = new Progress<CarvingProgress>(p => op.Report(Math.Min(range.Length, p.BytesScanned))),
                },
                op.CancellationToken));
            if (result.Cancelled)
            {
                return string.Empty;
            }

            string positions = string.Join(Environment.NewLine, result.Files.Enumerate().Take(10).Select(f => $"0x{f.Offset:X}  ({f.Length:N0})"));
            return Loc.Format("Sig_TryResult", result.Files.Count) + (positions.Length > 0 ? Environment.NewLine + positions : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return Loc.Format("Sig_Error_Try", ex.Message);
        }
    }

    internal static int SectorSizeOf(DocumentViewModel doc) => doc.Document.Source.LogicalSectorSize is > 1 and var s ? s : 512;

    private static IStructureResolver StructureResolver() =>
        new TemplateStructureResolver(Templates.TemplateService.Catalog);
}
