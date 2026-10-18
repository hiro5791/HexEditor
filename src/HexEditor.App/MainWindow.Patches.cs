using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using HexEditor.Core.Formats;
using HexEditor.Core.Operations;
using HexEditor.Core.Patches;
using HexEditor.Platform.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>パッチの差分の取り方 (EDIT-42 の仕様 1)。</summary>
internal enum PatchDiffSource
{
    Saved,
    Tabs,
    History,
}

/// <summary>
/// パッチの作成 (EDIT-42)・適用 (EDIT-43)・自己適用型パッチ (EDIT-44)。差分と形式の処理は Core の <see cref="PatchCreator"/>・<see cref="PatchFiles"/>。
/// 推定・書き出し・変換前の確認はバックグラウンドで行い、ドキュメントへの適用は UI スレッドで 1 つの編集グループとして行う。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>作成ダイアログで選べる形式の順 (自動 = IPS / IPS32 から選ぶ。TOOL-12 の 3)。</summary>
    private static readonly (string Id, PatchFormat? Format)[] PatchFormatChoices =
    [
        ("Auto", null), ("Ips", PatchFormat.Ips), ("Ips32", PatchFormat.Ips32), ("Ups", PatchFormat.Ups), ("Bps", PatchFormat.Bps),
        ("HexPatch", PatchFormat.HexPatch), ("SelfApplying", PatchFormat.SelfApplying),
    ];

    /// <summary>自己適用型パッチの適用プログラムが表示する文字列のキー (アプリのリソースの <c>PatchApplier_</c> + キー。EDIT-44 の仕様 4)。</summary>
    private static readonly string[] ApplierStringKeys =
        ["Title", "NoPatch", "Intro", "SelectTitle", "AllFiles", "Mismatch", "Backup", "Success", "BackupDone", "Failed", "ResultMismatch"];

    /// <summary>テスト用: 開いているパッチの作成・適用ダイアログの状態。</summary>
    private Func<JsonObject>? _patchDialogState;

    internal JsonObject PatchDialogTestModel() => _patchDialogState?.Invoke() ?? new JsonObject { ["open"] = false };

    private void RegisterPatchCommands()
    {
        Commands.Register("data.patch.create", () => CreatePatchAsync(PatchDiffSource.Saved, null, null),
            () => NeedsDocument(d => d.Document.LinkedParent is not null ? Loc.Get("Patch_LinkedView") : null));
        Commands.Register("data.patch.apply", ApplyPatchAsync, () => NeedsEditable());
    }

    private static string FormatName(PatchFormat? format) => Loc.Get("Patch_Format_" + (format?.ToString() ?? "Auto"));

    // ---- 作成 (EDIT-42) ----

    /// <summary>
    /// パッチの作成ダイアログ。<paramref name="historyFrom"/>・<paramref name="historyTo"/> は履歴パネルの「この 2 時点からパッチを作成」で選んだ時点
    /// (現在の経路の番号)。
    /// </summary>
    internal async Task CreatePatchAsync(PatchDiffSource initialSource, int? historyFrom, int? historyTo,
        (DocumentSnapshot From, DocumentSnapshot To, string Label)? historyPair = null)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        EditHistory history = doc.Document.History;
        var others = Vm.Documents.Where(d => !ReferenceEquals(d, doc)).ToList();
        var sourceSaved = DialogParts.Radio("PatchCreate_SourceSaved", Loc.Get("PatchCreate_SourceSaved"), "PatchSource", initialSource == PatchDiffSource.Saved);
        var sourceTabs = DialogParts.Radio("PatchCreate_SourceTabs", Loc.Get("PatchCreate_SourceTabs"), "PatchSource", initialSource == PatchDiffSource.Tabs);
        sourceTabs.IsEnabled = others.Count > 0;
        var sourceHistory = DialogParts.Radio("PatchCreate_SourceHistory", Loc.Get("PatchCreate_SourceHistory"), "PatchSource", initialSource == PatchDiffSource.History);
        sourceHistory.IsEnabled = history.Count > 1;
        var otherTab = DialogParts.Combo("PatchCreate_OtherTab", Loc.Get("PatchCreate_OtherTab"), others.Select(d => d.DisplayName), others.Count > 0 ? 0 : -1);
        IEnumerable<string> points = Enumerable.Range(0, history.Count).Select(i => Loc.Format("History_PointName", i) + " " +
            (i == 0 ? Loc.Get("History_Opened") : EditOperationName(history.Entries[i].Description)));
        var fromPoint = DialogParts.Combo("PatchCreate_FromPoint", Loc.Get("PatchCreate_FromPoint"), points, Math.Clamp(historyFrom ?? 0, 0, history.Count - 1));
        var toPoint = DialogParts.Combo("PatchCreate_ToPoint", Loc.Get("PatchCreate_ToPoint"), points, Math.Clamp(historyTo ?? history.CurrentIndex, 0, history.Count - 1));
        TextBlock pairLabel = DialogParts.Caption("PatchCreate_HistoryPair");
        pairLabel.Text = historyPair?.Label ?? string.Empty;
        if (historyPair is not null)
        {
            sourceHistory.IsEnabled = true;
        }

        var formatRadios = new List<RadioButton>();
        var formatNotes = new List<TextBlock>();
        var formatPanel = new StackPanel { Spacing = 2 };
        foreach ((string id, PatchFormat? _) in PatchFormatChoices)
        {
            var radio = DialogParts.Radio("PatchCreate_Format_" + id, Loc.Get("Patch_Format_" + id), "PatchFormat", id == "Bps");
            TextBlock note = DialogParts.Caption("PatchCreate_FormatNote_" + id);
            note.Margin = new Thickness(28, 0, 0, 4);
            formatRadios.Add(radio);
            formatNotes.Add(note);
            formatPanel.Children.Add(radio);
            formatPanel.Children.Add(note);
        }

        var rle = DialogParts.Check("PatchCreate_Rle", Loc.Get("PatchCreate_Rle"), true);
        var fullHash = DialogParts.Check("PatchCreate_FullHash", Loc.Get("PatchCreate_FullHash"), false);
        var defaultName = DialogParts.Field("PatchCreate_DefaultFileName", Loc.Get("PatchCreate_DefaultFileName"), doc.DisplayName, monospace: false);
        var message = DialogParts.Field("PatchCreate_Message", Loc.Get("PatchCreate_Message"), string.Empty, monospace: false);
        var arch = DialogParts.Combo("PatchCreate_Architecture", Loc.Get("PatchCreate_Architecture"), ["x64", "ARM64"],
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? 1 : 0);
        TextBlock smartScreen = DialogParts.Caption("PatchCreate_SmartScreen");
        smartScreen.Text = Loc.Get("PatchCreate_SmartScreen");
        var selfOptions = new StackPanel { Spacing = 8, Children = { defaultName, message, arch, smartScreen } };
        var pathBox = DialogParts.Field("PatchCreate_Path", Loc.Get("PatchCreate_Path"), string.Empty, monospace: false);
        var browse = new Button { Content = Loc.Get("PatchCreate_Browse"), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(browse, "PatchCreate_Browse");
        TextBlock summary = DialogParts.Caption("PatchCreate_Summary");
        TextBlock warning = DialogParts.Caption("PatchCreate_Warning");
        warning.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];

        var content = new StackPanel { Spacing = 8, MinWidth = 480 };
        content.Children.Add(new TextBlock { Text = Loc.Get("PatchCreate_Source"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        content.Children.Add(sourceSaved);
        content.Children.Add(sourceTabs);
        content.Children.Add(otherTab);
        content.Children.Add(sourceHistory);
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { fromPoint, toPoint } });
        content.Children.Add(pairLabel);
        content.Children.Add(new TextBlock { Text = Loc.Get("PatchCreate_Format"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        content.Children.Add(formatPanel);
        content.Children.Add(rle);
        content.Children.Add(fullHash);
        content.Children.Add(selfOptions);
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pathBox, browse } });
        content.Children.Add(summary);
        content.Children.Add(warning);
        ContentDialog dialog = DialogParts.Dialog(Root, "PatchCreateDialog", Loc.Get("PatchCreate_Title"),
            new ScrollViewer { Content = content, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) }, Loc.Get("PatchCreate_Create"));

        PatchCreator? creator = null;
        Dictionary<PatchFormat, PatchEstimate> estimates = [];
        CancellationTokenSource? cts = null;
        bool computing = false;
        string? stubPath = null;

        PatchDiffSource Source() => sourceTabs.IsChecked == true ? PatchDiffSource.Tabs : sourceHistory.IsChecked == true ? PatchDiffSource.History : PatchDiffSource.Saved;
        PatchFormat? SelectedChoice() => PatchFormatChoices[Math.Max(0, formatRadios.FindIndex(r => r.IsChecked == true))].Format;
        PatchFormat? Effective() => SelectedChoice() ?? (creator is null ? null : estimates.GetValueOrDefault(PatchFormat.Ips)?.IsAvailable == true ? PatchFormat.Ips : PatchFormat.Ips32);
        string StubArch() => arch.SelectedIndex == 1 ? "arm64" : "x64";

        void Update()
        {
            otherTab.Visibility = Source() == PatchDiffSource.Tabs ? Visibility.Visible : Visibility.Collapsed;
            fromPoint.Visibility = toPoint.Visibility = Source() == PatchDiffSource.History && historyPair is null ? Visibility.Visible : Visibility.Collapsed;
            pairLabel.Visibility = Source() == PatchDiffSource.History && historyPair is not null ? Visibility.Visible : Visibility.Collapsed;
            PatchFormat? chosen = SelectedChoice();
            PatchFormat? effective = Effective();
            rle.Visibility = effective is PatchFormat.Ips or PatchFormat.Ips32 ? Visibility.Visible : Visibility.Collapsed;
            fullHash.Visibility = effective is PatchFormat.HexPatch or PatchFormat.SelfApplying ? Visibility.Visible : Visibility.Collapsed;
            selfOptions.Visibility = effective == PatchFormat.SelfApplying ? Visibility.Visible : Visibility.Collapsed;
            stubPath = FindPatchStub(StubArch());
            for (int i = 0; i < PatchFormatChoices.Length; i++)
            {
                PatchFormat? f = PatchFormatChoices[i].Format;
                PatchEstimate? e = f is { } ff ? estimates.GetValueOrDefault(ff) : null;
                string note = string.Empty;
                bool enabled = true;
                if (f == PatchFormat.SelfApplying && stubPath is null)
                {
                    enabled = false;
                    note = Loc.Get("Patch_Unavailable_StubMissing");
                }
                else if (e is { IsAvailable: false })
                {
                    enabled = false;
                    note = Loc.Get("Patch_Unavailable_" + e.Unavailable) + " " + Loc.Get("Patch_UseOtherFormats");
                }
                else if (f is null && creator is not null)
                {
                    note = Loc.Format("PatchCreate_AutoChoice", FormatName(Effective()));
                }

                formatRadios[i].IsEnabled = enabled;
                formatNotes[i].Text = note;
                formatNotes[i].Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            PatchEstimate? estimate = effective is { } ef ? estimates.GetValueOrDefault(ef) : null;
            if (computing || estimate is null)
            {
                summary.Text = Loc.Get("PatchCreate_Computing");
                warning.Text = string.Empty;
            }
            else
            {
                long size = effective == PatchFormat.SelfApplying && stubPath is not null ? estimate.EstimatedSize + new FileInfo(stubPath).Length : estimate.EstimatedSize;
                summary.Text = estimate.IsEmpty ? Loc.Get("PatchCreate_NoChanges")
                    : Loc.Format("PatchCreate_SummaryText", estimate.Records.ToString("N0", CultureInfo.CurrentCulture), Size(size));
                warning.Text = estimate.Inefficient ? Loc.Get("PatchCreate_Inefficient") : string.Empty;
            }

            bool selectedEnabled = formatRadios.FirstOrDefault(r => r.IsChecked == true)?.IsEnabled == true;
            dialog.IsPrimaryButtonEnabled = !computing && estimate is { IsAvailable: true, IsEmpty: false } && selectedEnabled && pathBox.Text.Trim().Length > 0
                && (chosen != PatchFormat.SelfApplying || stubPath is not null);
        }

        PatchScript? BuildScript(CancellationToken token)
        {
            switch (Source())
            {
                case PatchDiffSource.Tabs when otherTab.SelectedIndex >= 0:
                    return PatchDiff.Between(others[otherTab.SelectedIndex].Document.DataSnapshot, doc.Document.DataSnapshot, token);
                case PatchDiffSource.History when historyPair is { } pair:
                    return PatchDiff.Between(pair.From, pair.To, token);
                case PatchDiffSource.History:
                    return PatchDiff.Between(history.Entries[Math.Max(0, fromPoint.SelectedIndex)].Snapshot, history.Entries[Math.Max(0, toPoint.SelectedIndex)].Snapshot,
                        token);
                default:
                    return PatchDiff.FromSaved(doc.Document.DataSnapshot);
            }
        }

        async void Recompute()
        {
            cts?.Cancel();
            var mine = new CancellationTokenSource();
            cts = mine;
            computing = true;
            creator = null;
            estimates = [];
            Update();
            try
            {
                (PatchCreator c, Dictionary<PatchFormat, PatchEstimate> e) = await Task.Run(() =>
                {
                    PatchScript script = BuildScript(mine.Token) ?? throw new OperationCanceledException();
                    var made = new PatchCreator(script);
                    var all = new Dictionary<PatchFormat, PatchEstimate>();
                    foreach (PatchFormat f in new[] { PatchFormat.Ips, PatchFormat.Ips32, PatchFormat.Ups, PatchFormat.Bps, PatchFormat.HexPatch, PatchFormat.SelfApplying })
                    {
                        mine.Token.ThrowIfCancellationRequested();
                        all[f] = made.Estimate(f, mine.Token, stubSize: 1);
                    }

                    return (made, all);
                }, mine.Token);
                if (!mine.IsCancellationRequested)
                {
                    creator = c;
                    estimates = e;
                    computing = false;
                    Update();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or Core.Editing.Transforms.DataReadException)
            {
                if (!mine.IsCancellationRequested)
                {
                    computing = false;
                    summary.Text = Loc.Format("PatchCreate_Failed", ex.Message);
                }
            }
        }

        foreach (RadioButton r in new[] { sourceSaved, sourceTabs, sourceHistory })
        {
            r.Checked += (_, _) => Recompute();
        }

        otherTab.SelectionChanged += (_, _) => Recompute();
        fromPoint.SelectionChanged += (_, _) => Recompute();
        toPoint.SelectionChanged += (_, _) => Recompute();
        foreach (RadioButton r in formatRadios)
        {
            r.Checked += (_, _) =>
            {
                // 出力ファイルの拡張子を形式に合わせる。
                if (pathBox.Text.Trim() is { Length: > 0 } p && Effective() is { } f)
                {
                    pathBox.Text = Path.ChangeExtension(p, PatchExtension(f));
                }

                Update();
            };
        }

        arch.SelectionChanged += (_, _) => Update();
        pathBox.TextChanged += (_, _) => Update();
        browse.Click += async (_, _) =>
        {
            PatchFormat f = Effective() ?? PatchFormat.Bps;
            string suggested = Path.GetFileNameWithoutExtension(doc.DisplayName) + PatchExtension(f);
            if (TestHooks.TrySavePicker(suggested, out string? chosen))
            {
                pathBox.Text = chosen ?? pathBox.Text;
                return;
            }

            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.PatchCreate" };
            picker.FileTypeChoices.Add(FormatName(f), [PatchExtension(f)]);
            if ((await picker.PickSaveFileAsync())?.Path is { } path)
            {
                pathBox.Text = path;
            }
        };
        if (doc.FilePath is { } own)
        {
            pathBox.Text = Path.ChangeExtension(own, PatchExtension(PatchFormat.Bps));
        }

        _patchDialogState = () => new JsonObject
        {
            ["open"] = true,
            ["kind"] = "create",
            ["computing"] = computing,
            ["selected"] = PatchFormatChoices[Math.Max(0, formatRadios.FindIndex(r => r.IsChecked == true))].Id,
            ["effective"] = Effective()?.ToString(),
            ["formats"] = new JsonArray([.. PatchFormatChoices.Select((c, i) => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["enabled"] = formatRadios[i].IsEnabled,
                ["note"] = formatNotes[i].Text,
            })]),
            ["summary"] = summary.Text,
            ["warning"] = warning.Text,
            ["records"] = Effective() is { } ef && estimates.GetValueOrDefault(ef) is { } est ? est.Records : null,
            ["smartScreen"] = selfOptions.Visibility == Visibility.Visible ? smartScreen.Text : string.Empty,
            ["canCreate"] = dialog.IsPrimaryButtonEnabled,
        };
        Recompute();
        ContentDialogResult result;
        try
        {
            result = await ShowEditDialogAsync(dialog);
        }
        finally
        {
            _patchDialogState = null;
            cts?.Cancel();
        }

        if (result != ContentDialogResult.Primary || creator is null || Effective() is not { } format)
        {
            return;
        }

        var options = new PatchWriteOptions
        {
            UseRle = rle.IsChecked == true,
            FullHash = fullHash.IsChecked == true,
            DefaultFileName = defaultName.Text.Trim() is { Length: > 0 } dn ? dn : null,
            Message = message.Text.Trim() is { Length: > 0 } m ? m : null,
            StubPath = stubPath,
            ApplierStrings = format == PatchFormat.SelfApplying ? ApplierStrings() : null,
        };
        await WritePatchAsync(doc, creator, format, pathBox.Text.Trim(), options);
    }

    private static string PatchExtension(PatchFormat format) => format switch
    {
        PatchFormat.Ips or PatchFormat.Ips32 => ".ips",
        PatchFormat.Ups => ".ups",
        PatchFormat.Bps => ".bps",
        PatchFormat.HexPatch => ".hexpatch",
        _ => ".exe",
    };

    /// <summary>同梱の適用プログラム (EDIT-44 の仕様 1)。テスト用のビルドでは環境変数 HEXEDITOR_PATCH_STUB_DIR のフォルダも見る。</summary>
    private static string? FindPatchStub(string architecture) =>
        SelfPatchBuilder.FindStub(AppContext.BaseDirectory, architecture)
        ?? (TestHooks.Active && Environment.GetEnvironmentVariable("HEXEDITOR_PATCH_STUB_DIR") is { Length: > 0 } dir ? SelfPatchBuilder.FindStub(dir, architecture) : null);

    /// <summary>適用プログラムが表示する 23 言語の文字列 (アプリのリソースから。訳がなければ英語。EDIT-44 の仕様 4)。</summary>
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ApplierStrings()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        try
        {
            var manager = new ResourceManager();
            ResourceMap map = manager.MainResourceMap.GetSubtree("Resources");
            foreach (DisplayLanguage language in DisplayLanguages.All)
            {
                ResourceContext context = manager.CreateResourceContext();
                context.QualifierValues["Language"] = language.Tag;
                var table = new Dictionary<string, string>();
                foreach (string key in ApplierStringKeys)
                {
                    if (map.TryGetValue("PatchApplier_" + key, context)?.ValueAsString is { Length: > 0 } value)
                    {
                        table[key] = value;
                    }
                }

                result[language.Tag] = table;
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            // 取れなければ今の表示言語だけ。
            result["en"] = ApplierStringKeys.ToDictionary(k => k, k => Loc.Get("PatchApplier_" + k));
        }

        return result;
    }

    private async Task WritePatchAsync(DocumentViewModel doc, PatchCreator creator, PatchFormat format, string path, PatchWriteOptions options)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        long total = creator.Script.SourceLength + creator.Script.TargetLength;
        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Patch_Operation_Create", Path.GetFileName(path)), OperationKind.WritesExternal, null, total, op => Task.Run(() =>
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                {
                    creator.Write(format, output, options, op.Report, op.CancellationToken);
                    output.Flush(flushToDisk: true);
                }

                File.Move(temp, path, overwrite: true);
                return true;
            }));
            ShowStatusMessage(Loc.Format("Patch_Created", Path.GetFileName(path)));
            if (format == PatchFormat.SelfApplying)
            {
                // 署名のない実行ファイルは受け取った人の環境で SmartScreen の警告が出る (EDIT-44 の仕様 3)。
                ShowNotice(Loc.Format("Patch_CreatedSelfApplying", Path.GetFileName(path)), InfoBarSeverity.Informational, doc);
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IpsLimitException or Core.Editing.Transforms.DataReadException)
        {
            TryDeleteFile(temp);
            ShowNotice(Loc.Format("Patch_CreateFailed", ex.Message), InfoBarSeverity.Error, doc);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---- 適用 (EDIT-43) ----

    private async Task ApplyPatchAsync()
    {
        if (Vm.Selected is not { } doc || !EnsureEditable(doc))
        {
            return;
        }

        string? path;
        if (TestHooks.OpenPickerResult("HexEditor.PatchApply") is { } chosen)
        {
            path = chosen.FirstOrDefault();
        }
        else
        {
            var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = "HexEditor.PatchApply" };
            foreach (string ext in new[] { ".ips", ".ups", ".bps", ".hexpatch", "*" })
            {
                picker.FileTypeFilter.Add(ext);
            }

            path = (await picker.PickSingleFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        LoadedPatch patch;
        try
        {
            patch = PatchFiles.Load(path);
        }
        catch (PatchException ex)
        {
            ShowNotice(ex.Error == PatchError.UnknownFormat ? Loc.Get("Patch_Error_UnknownFormat")
                : ex.Record >= 0 ? Loc.Format("Patch_Error_Record", ex.Record + 1) : Loc.Format("Patch_Error_Corrupt", ex.Message), InfoBarSeverity.Error, doc);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Patch_Error_Corrupt", ex.Message), InfoBarSeverity.Error, doc);
            return;
        }

        using (patch)
        {
            await ApplyLoadedPatchAsync(doc, patch, Path.GetFileName(path));
        }
    }

    private async Task ApplyLoadedPatchAsync(DocumentViewModel doc, LoadedPatch patch, string name)
    {
        var reverse = DialogParts.Check("PatchApply_Reverse", Loc.Get("PatchApply_Reverse"), false);
        reverse.Visibility = patch.CanReverse ? Visibility.Visible : Visibility.Collapsed;
        TextBlock info = DialogParts.Caption("PatchApply_Info");
        info.Style = (Style)Application.Current.Resources["BodyTextBlockStyle"];
        var icon = new FontIcon { FontSize = 16 };
        TextBlock verification = DialogParts.Caption("PatchApply_Verification");
        verification.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        var ring = new ProgressRing { IsActive = false, Width = 16, Height = 16 };
        TextBlock warning = DialogParts.Caption("PatchApply_Warning");
        warning.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        var content = new StackPanel { Spacing = 8, MinWidth = 440 };
        content.Children.Add(info);
        content.Children.Add(reverse);
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { ring, icon, verification } });
        content.Children.Add(warning);
        ContentDialog dialog = DialogParts.Dialog(Root, "PatchApplyDialog", Loc.Format("PatchApply_Title", name), content, Loc.Get("PatchApply_Apply"),
            patch.VerificationReadsWholeFile ? Loc.Get("PatchApply_SkipVerification") : null);

        PatchVerification? result = null;
        bool verifying = false;
        CancellationTokenSource? cts = null;
        void ShowInfo()
        {
            bool back = reverse.IsChecked == true;
            long resultLength = patch.ResultLength(doc.Document.Length, back);
            info.Text = Loc.Format("PatchApply_InfoText", FormatName(patch.Format), patch.RecordCount.ToString("N0", CultureInfo.CurrentCulture),
                patch.ChangedBytes.ToString("N0", CultureInfo.CurrentCulture), resultLength.ToString("N0", CultureInfo.CurrentCulture));
            ring.IsActive = verifying;
            ring.Visibility = verifying ? Visibility.Visible : Visibility.Collapsed;

            // 確認の結果は文字とアイコンで示す (EDIT-43 の「画面」)。
            (string glyph, string text) = verifying ? (string.Empty, Loc.Get("PatchApply_Verifying")) : result switch
            {
                PatchVerification.Match => ("", Loc.Get("PatchApply_Match")),
                PatchVerification.Mismatch => ("", Loc.Get("PatchApply_Mismatch")),
                _ => ("", Loc.Get("PatchApply_Unverifiable")),
            };
            icon.Glyph = glyph;
            verification.Text = Loc.Format("PatchApply_VerificationText", text);
            AutomationProperties.SetName(verification, verification.Text);
            bool mismatch = result == PatchVerification.Mismatch;
            warning.Text = mismatch ? Loc.Get("PatchApply_MismatchWarning") : string.Empty;
            dialog.PrimaryButtonText = Loc.Get(mismatch ? "PatchApply_ApplyAnyway" : "PatchApply_Apply");

            // 一致しない場合の既定のボタンは「キャンセル」(EDIT-43 の仕様 3)。
            dialog.DefaultButton = mismatch ? ContentDialogButton.Close : ContentDialogButton.Primary;
            dialog.IsPrimaryButtonEnabled = !verifying;
            dialog.IsSecondaryButtonEnabled = verifying;
        }

        async void Verify()
        {
            cts?.Cancel();
            var mine = new CancellationTokenSource();
            cts = mine;
            bool back = reverse.IsChecked == true;
            DocumentSnapshot snapshot = doc.Document.DataSnapshot;
            verifying = patch.VerificationReadsWholeFile;
            result = null;
            ShowInfo();
            try
            {
                PatchVerification v = patch.VerificationReadsWholeFile
                    ? await Task.Run(() => patch.Verify(snapshot, back, null, mine.Token), mine.Token)
                    : patch.Verify(snapshot, back, null, mine.Token);
                if (!mine.IsCancellationRequested)
                {
                    result = v;
                    verifying = false;
                    ShowInfo();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or Core.Editing.Transforms.DataReadException)
            {
                if (!mine.IsCancellationRequested)
                {
                    verifying = false;
                    result = PatchVerification.Unverifiable;
                    ShowInfo();
                }
            }
        }

        reverse.Checked += (_, _) => Verify();
        reverse.Unchecked += (_, _) => Verify();
        bool skipped = false;
        dialog.SecondaryButtonClick += (_, e) =>
        {
            // 「確認を省略して適用」(EDIT-43 の「巨大ファイル」)。
            skipped = true;
            cts?.Cancel();
        };
        _patchDialogState = () => new JsonObject
        {
            ["open"] = true,
            ["kind"] = "apply",
            ["format"] = patch.Format.ToString(),
            ["records"] = patch.RecordCount,
            ["changedBytes"] = patch.ChangedBytes,
            ["resultLength"] = patch.ResultLength(doc.Document.Length, reverse.IsChecked == true),
            ["verifying"] = verifying,
            ["verification"] = result?.ToString(),
            ["verificationText"] = verification.Text,
            ["glyph"] = icon.Glyph,
            ["warning"] = warning.Text,
            ["primary"] = dialog.PrimaryButtonText,
            ["defaultButton"] = dialog.DefaultButton.ToString(),
            ["canReverse"] = patch.CanReverse,
        };
        Verify();
        ContentDialogResult answer;
        try
        {
            answer = await ShowEditDialogAsync(dialog);
        }
        finally
        {
            _patchDialogState = null;
            cts?.Cancel();
        }

        if (answer != ContentDialogResult.Primary && !(answer == ContentDialogResult.Secondary && skipped))
        {
            return;
        }

        bool reversed = reverse.IsChecked == true;
        if (patch.ResultLength(doc.Document.Length, reversed) != doc.Document.Length && !doc.Document.CanResize)
        {
            ShowNotice(Loc.Get("Patch_Error_FixedLength"), InfoBarSeverity.Error, doc);
            return;
        }

        HistoryNode before = doc.Document.History.CurrentNode;
        bool applied = false;
        try
        {
            patch.ApplyTo(doc.Document, reversed, verifyResult: false, description: "パッチの適用");
            applied = true;
        }
        catch (PatchException ex)
        {
            ShowNotice(ex.Record >= 0 ? Loc.Format("Patch_Error_Record", ex.Record + 1) : Loc.Format("Patch_Error_Corrupt", ex.Message), InfoBarSeverity.Error, doc);
        }
        catch (Exception ex) when (ex is DocumentReadOnlyException or FixedLengthException or DocumentLockedException or IOException)
        {
            ShowNotice(ex is FixedLengthException ? Loc.Get("Patch_Error_FixedLength") : Loc.Format("Patch_Error_Corrupt", ex.Message), InfoBarSeverity.Error, doc);
        }

        if (!applied)
        {
            if (!ReferenceEquals(doc.Document.History.CurrentNode, before) && doc.Document.History.CanUndo)
            {
                doc.Document.Undo();
            }

            return;
        }

        // 適用後の確認 (UPS・BPS の CRC-32。EDIT-43 の仕様 5): 合わなければ適用を取り消してエラーにする。
        if (patch.Format is PatchFormat.Ups or PatchFormat.Bps or PatchFormat.HexPatch)
        {
            DocumentSnapshot after = doc.Document.DataSnapshot;
            bool ok;
            try
            {
                ok = await Vm.Operations.RunAsync(Loc.Get("Patch_Operation_Verify"), OperationKind.ReadOnly, doc.Document, after.Length,
                    op => Task.Run(() => patch.CheckResult(after, reversed, op.Report, op.CancellationToken)));
            }
            catch (OperationCanceledException)
            {
                ok = true;
            }

            if (!ok)
            {
                if (ReferenceEquals(doc.Document.History.Current, after) && doc.Document.History.CanUndo)
                {
                    doc.Document.Undo();
                }

                ShowNotice(Loc.Get("Patch_Error_ResultMismatch"), InfoBarSeverity.Error, doc);
                return;
            }
        }

        ShowStatusMessage(Loc.Format("Patch_Applied", name));
        FocusEditor();
    }
}
