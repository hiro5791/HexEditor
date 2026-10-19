using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Files;
using HexEditor.Core.FileSystems;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.Tools.Files;
using HexEditor.Core.View;
using HexEditor.Core.Wiping;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App;

/// <summary>
/// 安全な消去 (12 の FOR-30 範囲・ディスク、FOR-31 空き領域・スラック)。消去のロジックは Core (HexEditor.Core.Wiping) が行い、ここは対象の決定・
/// ダイアログ (方式、確認、強い確認)・処理センターの進捗・結果の表示・開いているドキュメントの読み直しを受け持つ。
/// 書き込み保護モード・証拠・監査ログは <see cref="WipeForensics.Hooks"/> (統合で ForensicsService につなぐ) を通す。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>テスト用: 書き込み 1 回ごとの待ち (遅いデータソースの再現。TC-FOR-30-02)。</summary>
    internal static TimeSpan WipeTestWriteDelay { get; set; }

    /// <summary>テスト用: 最後の消去・墨塗りの結果 (<see cref="WipeResult"/>、<see cref="OsFreeSpaceWipeResult"/>、<see cref="RedactionPlan"/>)。</summary>
    internal object? LastWipeResult { get; private set; }

    /// <summary>テスト用: 最後の消去の処理 (処理センターの進捗)。</summary>
    internal LongRunningOperation? LastWipeOperation { get; private set; }

    /// <summary>テスト用: 最後のスラックの消去で対象外にしたファイルの数。</summary>
    internal SlackSkipCounts? LastSlackSkipped { get; private set; }

    /// <summary>テスト用: 最後に開いた消去の確認ダイアログ。</summary>
    internal ContentDialog? LastWipeConfirmDialog { get; private set; }

    private void RegisterWipeCommands()
    {
        Commands.Register("forensics.wipe.selection", () => ShowWipeSelectionAsync(), WipeSelectionState);
        Commands.Register("forensics.wipe.disk", () => ShowWipeDiskAsync(), WipeDiskState);
        Commands.Register("forensics.wipe.freeSpace", () => ShowWipeFreeSpaceAsync(), WipeFreeSpaceState);
        Commands.Register("forensics.wipe.slack", () => ShowWipeSlackAsync(), WipeSlackState);
        Commands.Register("forensics.fs.redact", () => ShowRedactDialogAsync(), RedactState);
        EventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(Commands.NotifyStatesChanged);
        WipeForensics.Hooks.WriteBlockChanged += changed;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                WipeForensics.Hooks.WriteBlockChanged -= changed;
            }
        };
    }

    // ---- 対象 ----

    /// <summary>
    /// 消去の書き込み先 (データエンジンを通さない)。<see cref="Root"/> はデータソースを持つドキュメント (パーティションのタブなら親のディスク)、
    /// <see cref="BaseOffset"/> は選んだタブの先頭の Root 上の位置。
    /// </summary>
    internal sealed record WipeDocumentTarget(DocumentViewModel Root, long BaseOffset, string Name, DeviceByteSource? Device, string? FilePath, long FileBase,
        long SourceLength, int SectorSize)
    {
        public string Identity => Root.Document.Source.Identity;

        public string? Path => Device?.Path ?? FilePath;
    }

    /// <summary>ドキュメントの消去の書き込み先 (直接書き換えられないデータソース・変換ビューなら null)。</summary>
    internal static WipeDocumentTarget? ResolveWipeTarget(DocumentViewModel d)
    {
        DocumentViewModel root = d.LinkParent ?? d;
        long baseOffset = d.LinkParent is not null ? d.Document.LinkStart : 0;
        if (root.Document.ViewTransform is not null || d.Document.ViewTransform is not null || root.Document.IsDisposed)
        {
            return null;
        }

        return root.Document.Source switch
        {
            DeviceByteSource device => new WipeDocumentTarget(root, baseOffset, DeviceTargetName(device), device, null, 0, device.Length, device.LogicalSectorSize),
            DiskImageByteSource image => new WipeDocumentTarget(root, baseOffset, image.DisplayName, null, image.Path, image.File.RangeStart, image.Length, 1),
            FileByteSource file => new WipeDocumentTarget(root, baseOffset, file.DisplayName, null, file.Path, file.RangeStart, file.Length, 1),
            _ => null,
        };
    }

    private static string DeviceTargetName(DeviceByteSource device) =>
        device.Info.Disk is { } disk && device.Info.RangeStart == 0 && device.Length >= (disk.Size ?? device.Length)
            ? Loc.Format("Wipe_DiskName", disk.Number)
            : device.DisplayName;

    /// <summary>同じデータソースを開いているドキュメント (すべてのウィンドウ)。消去の前の未保存の確認と、消去の後の読み直しに使う。</summary>
    private static List<DocumentViewModel> SameSourceDocuments(DocumentViewModel root) =>
        [.. WindowManager.Windows.SelectMany(w => w.Vm.Documents)
            .Where(d => d.LinkParent is null && !d.Document.IsDisposed && string.Equals(d.Document.Source.Identity, root.Document.Source.Identity, StringComparison.Ordinal))
            .Append(root).Distinct()];

    private CommandState WriteBlockState() =>
        WipeForensics.Hooks.IsWriteBlockEnabled ? CommandState.Unavailable(Loc.Get("WriteBlock_Enabled")) : CommandState.Available;

    private CommandState TargetState(DocumentViewModel d)
    {
        if (ResolveWipeTarget(d) is not { } target)
        {
            return CommandState.Unavailable(Loc.Get("Wipe_Unsupported"));
        }

        return WipeForensics.Hooks.IsEvidence(target.Identity, target.Path) ? CommandState.Unavailable(Loc.Get("Wipe_Evidence")) : CommandState.Available;
    }

    private CommandState WipeSelectionState() =>
        WriteBlockState() is { Enabled: false } blocked ? blocked
        : NeedsDocument(d => d.Editor.HasSelection ? null : Loc.Get("Wipe_NoSelection")) is { Enabled: false } state ? state
        : TargetState(Vm.Selected!);

    private CommandState WipeDiskState() =>
        WriteBlockState() is { Enabled: false } blocked ? blocked
        : NeedsDocument(d => IsWholeDisk(d) ? null : Loc.Get("Wipe_NotDisk")) is { Enabled: false } state ? state
        : TargetState(Vm.Selected!);

    private CommandState WipeFreeSpaceState() => WriteBlockState();

    private CommandState WipeSlackState() =>
        WriteBlockState() is { Enabled: false } blocked ? blocked
        : NeedsSession() is { Enabled: false } state ? state
        : TargetState(Vm.Selected!);

    private CommandState RedactState()
    {
        if (NeedsFsEntry() is { Enabled: false } state)
        {
            return state;
        }

        // 書き込み保護モードではドキュメントが読み取り専用になるため実行できない (仕様 6。ドキュメント単位の例外を設定したものはできる)。
        return Vm.Selected!.Document.IsReadOnly
            ? CommandState.Unavailable(WipeForensics.Hooks.IsWriteBlockEnabled ? Loc.Get("WriteBlock_Enabled") : Loc.Get("Redact_ReadOnly"))
            : CommandState.Available;
    }

    /// <summary>物理ディスク全体を開いたタブか (ディスク全体の消去の対象)。</summary>
    private static bool IsWholeDisk(DocumentViewModel d) =>
        d.LinkParent is null && d.Device is { Info.Disk: { } disk } device && device.Info.RangeStart == 0 && device.Length >= (disk.Size ?? device.Length);

    // ---- Hex ビューの右クリックメニュー (FOR-30 の「呼び出し」) ----

    private void ExtendHexViewWipeMenu(MenuFlyout menu)
    {
        const string Id = "HexViewMenu_WipeSelection";
        if (menu.Items.FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == Id) is not MenuFlyoutItem item)
        {
            item = new MenuFlyoutItem { Text = Loc.Get("HexViewMenu_WipeSelection"), Tag = "Command" };
            AutomationProperties.SetAutomationId(item, Id);
            item.Click += (_, _) => _ = Commands.ExecuteAsync("forensics.wipe.selection");
            menu.Items.Add(item);
        }

        // 選択範囲があるときだけ出す。書き込み保護モードなどで使えないときは無効にして理由をツールチップに出す。
        CommandState state = Commands.StateOf("forensics.wipe.selection");
        item.Visibility = Vm.Selected?.Editor.HasSelection == true ? Visibility.Visible : Visibility.Collapsed;
        item.IsEnabled = state.Enabled;
        ToolTipService.SetToolTip(item, state.Enabled ? null : state.Reason);
        AutomationProperties.SetHelpText(item, state.Enabled ? string.Empty : state.Reason ?? string.Empty);
    }

    // ---- 方式の入力 (TOOL-17 の仕様 2 と同じ) ----

    private sealed class MethodInputs
    {
        public required RadioButton Zero { get; init; }
        public required RadioButton Random { get; init; }
        public required RadioButton Dod { get; init; }
        public required RadioButton Custom { get; init; }
        public required TextBox Passes { get; init; }
        public required TextBox Patterns { get; init; }
        public required TextBlock Error { get; init; }

        public IEnumerable<UIElement> Elements => [Zero, Random, Dod, Custom, Passes, Patterns, Error];

        public ShredMethod? Method(out string? problem)
        {
            problem = null;
            if (Zero.IsChecked == true)
            {
                return ShredMethod.ZeroOnce;
            }

            if (Random.IsChecked == true)
            {
                return ShredMethod.RandomOnce;
            }

            if (Dod.IsChecked == true)
            {
                return ShredMethod.Dod;
            }

            if (!int.TryParse(Passes.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n is < 1 or > ShredMethod.MaxCustomPasses)
            {
                problem = Loc.Get("Shred_Error_Passes");
                return null;
            }

            string[] lines = Patterns.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = new List<ShredPass>();
            for (int i = 0; i < n; i++)
            {
                string line = lines.Length == 0 ? "00" : lines[Math.Min(i, lines.Length - 1)];
                if (ShredMethod.ParsePass(line) is not { } pass)
                {
                    problem = Loc.Format("Shred_Error_Pattern", line);
                    return null;
                }

                result.Add(pass);
            }

            return ShredMethod.Custom(result);
        }
    }

    private static MethodInputs CreateMethodInputs(string prefix)
    {
        string group = prefix + "Method";
        var inputs = new MethodInputs
        {
            Zero = DialogParts.Radio(prefix + "_Zero", Loc.Get("Shred_Method_Zero"), group, true),
            Random = DialogParts.Radio(prefix + "_Random", Loc.Get("Shred_Method_Random"), group, false),
            Dod = DialogParts.Radio(prefix + "_Dod", Loc.Get("Shred_Method_Dod"), group, false),
            Custom = DialogParts.Radio(prefix + "_Custom", Loc.Get("Shred_Method_Custom"), group, false),
            Passes = DialogParts.Field(prefix + "_Passes", Loc.Get("Shred_Passes"), "3"),
            Patterns = DialogParts.Field(prefix + "_Patterns", Loc.Get("Shred_Patterns"), "00\nFF\nrandom"),
            Error = ToolParts.ErrorText(prefix + "_Error"),
        };
        inputs.Patterns.AcceptsReturn = true;
        inputs.Patterns.TextWrapping = TextWrapping.NoWrap;
        inputs.Patterns.MinHeight = 70;
        return inputs;
    }

    private static TextBlock Strong(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };

    private static TextBlock Line(StackPanel panel, string id, string text, bool strong = false)
    {
        TextBlock block = strong ? Strong(text) : new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(block, id);
        panel.Children.Add(block);
        return block;
    }

    private static string WipeNumber(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string WipeOffset(long value) => "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>所要時間の表示 (秒・分・時間)。</summary>
    internal static string WipeDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? Loc.Format("Wipe_Hours", t.TotalHours.ToString("0.0", CultureInfo.CurrentCulture))
        : t.TotalMinutes >= 1 ? Loc.Format("Wipe_Minutes", (int)Math.Ceiling(t.TotalMinutes))
        : Loc.Format("Wipe_Seconds", Math.Max(1, (int)Math.Ceiling(t.TotalSeconds)));

    private static string MethodSummary(ShredMethod method) => Loc.Format("Wipe_Confirm_Method", MethodName(method), method.Passes.Count);

    // ---- 範囲の消去 (FOR-30) ----

    private async Task ShowWipeSelectionAsync()
    {
        if (Vm.Selected is not { } d || !WipeSelectionState().Enabled || ResolveWipeTarget(d) is not { } target)
        {
            return;
        }

        var context = new EditorExpressionContext(d.Editor);
        TextBox start = DialogParts.Field("Wipe_Start", Loc.Get("Wipe_Start"), WipeOffset(d.Editor.SelectionStart));
        TextBox length = DialogParts.Field("Wipe_Length", Loc.Get("Wipe_Length"), WipeOffset(d.Editor.SelectionLength));
        MethodInputs methods = CreateMethodInputs("Wipe");
        CheckBox verify = DialogParts.Check("Wipe_Verify", Loc.Get("Wipe_Verify"), true);
        TextBlock error = ToolParts.ErrorText("Wipe_RangeError");
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        Line(body, "Wipe_TargetText", Loc.Format("Wipe_Target", target.Name));
        body.Children.Add(start);
        body.Children.Add(length);
        body.Children.Add(error);
        body.Children.Add(Strong(Loc.Get("Shred_Method")));
        foreach (UIElement element in methods.Elements)
        {
            body.Children.Add(element);
        }

        body.Children.Add(verify);
        ContentDialog dialog = DialogParts.Dialog(Root, "WipeDialog", Loc.Get("Wipe_Title"), new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Wipe_Next"));

        (long Start, long Length)? Range(out string? problem)
        {
            problem = null;
            if (!DialogParts.TryEvaluate(start.Text, context, out long s, out ExpressionException? e1)
                || !DialogParts.TryEvaluate(length.Text, context, out long n, out ExpressionException? e2))
            {
                problem = Loc.Get("Wipe_Error_Range");
                return null;
            }

            if (s < 0 || n <= 0 || s > d.Document.Length - n)
            {
                problem = Loc.Get("Wipe_Error_Range");
                return null;
            }

            return (s, n);
        }

        void Update()
        {
            methods.Passes.IsEnabled = methods.Patterns.IsEnabled = methods.Custom.IsChecked == true;
            ShredMethod? method = methods.Method(out string? problem);
            ToolParts.ShowError(methods.Error, null, problem);
            ToolParts.ShowError(error, null, Range(out string? rangeProblem) is null ? rangeProblem : null);
            dialog.IsPrimaryButtonEnabled = method is not null && rangeProblem is null && !WipeForensics.Hooks.IsWriteBlockEnabled;
        }

        foreach (RadioButton radio in new[] { methods.Zero, methods.Random, methods.Dod, methods.Custom })
        {
            radio.Checked += (_, _) => Update();
        }

        methods.Passes.TextChanged += (_, _) => Update();
        methods.Patterns.TextChanged += (_, _) => Update();
        start.TextChanged += (_, _) => Update();
        length.TextChanged += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || methods.Method(out _) is not { } chosen || Range(out _) is not { } range)
        {
            return;
        }

        await WipeRangeAfterDialogAsync(d, target, new WipeRange(target.BaseOffset + range.Start, range.Length), chosen, verify.IsChecked == true, wholeDisk: false);
    }

    private async Task ShowWipeDiskAsync()
    {
        if (Vm.Selected is not { } d || !WipeDiskState().Enabled || ResolveWipeTarget(d) is not { } target)
        {
            return;
        }

        MethodInputs methods = CreateMethodInputs("WipeDisk");
        CheckBox verify = DialogParts.Check("WipeDisk_Verify", Loc.Get("Wipe_Verify"), true);
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        Line(body, "WipeDisk_TargetText", Loc.Format("Wipe_Target", target.Name));
        body.Children.Add(Strong(Loc.Get("Shred_Method")));
        foreach (UIElement element in methods.Elements)
        {
            body.Children.Add(element);
        }

        body.Children.Add(verify);
        ContentDialog dialog = DialogParts.Dialog(Root, "WipeDiskDialog", Loc.Get("Wipe_Title"), new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Wipe_Next"));
        void Update()
        {
            methods.Passes.IsEnabled = methods.Patterns.IsEnabled = methods.Custom.IsChecked == true;
            ShredMethod? method = methods.Method(out string? problem);
            ToolParts.ShowError(methods.Error, null, problem);
            dialog.IsPrimaryButtonEnabled = method is not null && !WipeForensics.Hooks.IsWriteBlockEnabled;
        }

        foreach (RadioButton radio in new[] { methods.Zero, methods.Random, methods.Dod, methods.Custom })
        {
            radio.Checked += (_, _) => Update();
        }

        methods.Passes.TextChanged += (_, _) => Update();
        methods.Patterns.TextChanged += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || methods.Method(out _) is not { } chosen)
        {
            return;
        }

        await WipeRangeAfterDialogAsync(d, target, new WipeRange(0, target.SourceLength), chosen, verify.IsChecked == true, wholeDisk: true);
    }

    /// <summary>
    /// 範囲・ディスク全体の消去の確認の後の流れ (仕様 4〜9): 未保存の編集の確認 → セクタ境界に広げる → システムディスク・ロックできないボリュームの拒否 →
    /// 確認ダイアログ (ディスク全体なら強い確認) → 長時間処理 → 読み直し → 結果。<paramref name="range"/> は Root 上の位置。
    /// </summary>
    internal async Task<WipeResult?> WipeRangeAfterDialogAsync(DocumentViewModel d, WipeDocumentTarget target, WipeRange range, ShredMethod method, bool verify,
        bool wholeDisk)
    {
        if (!await EnsureNoUnsavedEditsForWipeAsync(target.Root))
        {
            return null;
        }

        WipeRange widened = target.Device is not null ? RangeWipe.Widen(range, target.SectorSize, target.SourceLength) : range;
        IReadOnlyList<VolumeDeviceInfo> volumes = [];
        DiskInfo? disk = target.Device?.Info.Disk;
        if (target.Device is { } device)
        {
            DeviceCatalog catalog = App.Devices.EnumerateDevices();
            volumes = WipeVolumeLocks.Overlapping(device, catalog, [(widened.Offset, widened.Length)], out IReadOnlyList<VolumeDeviceInfo> blocked);
            if (blocked.Count > 0 || RangeWipe.IsSystemDisk(disk, catalog) || device.Info.Volume is { IsInUseByWindows: true })
            {
                RecordRefusal(target, WipeKind.Range, WipeRefusal.SystemDisk);
                await ShowMessageAsync(Loc.Get("Wipe_Title"), Loc.Get("Wipe_SystemDisk"), "WipeRefusedDialog");
                return null;
            }
        }

        if (!await ConfirmRangeWipeAsync(target, range, widened, method, verify, wholeDisk, volumes, disk))
        {
            return null;
        }

        var job = new WipeJob
        {
            Kind = wholeDisk ? WipeKind.Disk : WipeKind.Range,
            TargetName = target.Name,
            Identity = target.Identity,
            Path = target.Path,
            Ranges = [widened],
            TotalBytes = widened.Length,
            Method = method,
            Verify = verify,
            Details = new Dictionary<string, string>
            {
                ["offset"] = widened.Offset.ToString(CultureInfo.InvariantCulture),
                ["length"] = widened.Length.ToString(CultureInfo.InvariantCulture),
                ["startLba"] = target.Device is not null ? ((target.Device!.Info.RangeStart + widened.Offset) / target.SectorSize).ToString(CultureInfo.InvariantCulture) : string.Empty,
                ["volumes"] = string.Join(";", volumes.Select(v => v.Name)),
            },
        };
        WipeResult? result = await RunWipeJobAsync(target, job, volumes);
        if (result is not null)
        {
            await ShowWipeResultAsync(result, Loc.Format("Wipe_Result_Done", WipeNumber(result.TotalBytes), WipeDuration(result.Elapsed)), null);
        }

        return result;
    }

    /// <summary>未保存の編集の確認 (仕様 7): 同じデータソースのドキュメントに未保存の編集があれば「保存」「破棄」を選ばせる。</summary>
    private async Task<bool> EnsureNoUnsavedEditsForWipeAsync(DocumentViewModel root)
    {
        List<DocumentViewModel> modified = [.. SameSourceDocuments(root).Where(x => x.Document.IsModified)];
        if (modified.Count == 0)
        {
            return true;
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "WipeUnsavedDialog", Loc.Get("Wipe_Unsaved_Title"),
            new TextBlock { Text = Loc.Get("Wipe_Unsaved_Body"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, Loc.Get("Wipe_Unsaved_Save"),
            Loc.Get("Wipe_Unsaved_Discard"));
        dialog.DefaultButton = ContentDialogButton.Close;
        switch (await ShowEditDialogAsync(dialog))
        {
            case ContentDialogResult.Primary:
                foreach (DocumentViewModel x in modified)
                {
                    MainWindow owner = WindowManager.Windows.FirstOrDefault(w => w.Vm.Documents.Contains(x)) ?? this;
                    if (!await owner.SaveAsync(x, saveAs: false) || x.Document.IsModified)
                    {
                        return false;
                    }
                }

                return true;
            case ContentDialogResult.Secondary:
                foreach (DocumentViewModel x in modified)
                {
                    x.Document.DiscardChanges();
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 確認ダイアログ (共通の前提「破壊的な操作の確認」、仕様 4・5): 対象、範囲 (開始 LBA・セクタ数・バイト数)、方式と回数、所要時間の目安、
    /// 取り消せないこと、SSD の限界。ディスク全体は強い確認 (「ディスク N」を入力するまで実行ボタンを無効にし、既定のフォーカスは「キャンセル」)。
    /// </summary>
    private async Task<bool> ConfirmRangeWipeAsync(WipeDocumentTarget target, WipeRange requested, WipeRange range, ShredMethod method, bool verify, bool wholeDisk,
        IReadOnlyList<VolumeDeviceInfo> volumes, DiskInfo? disk)
    {
        var body = new StackPanel { Spacing = 8, MaxWidth = 480 };
        Line(body, "WipeConfirm_Target", Loc.Format("Wipe_Confirm_Target", target.Name));
        if (disk is not null)
        {
            Line(body, "WipeConfirm_Disk", Loc.Format("Wipe_Confirm_Disk", disk.Number, disk.Model ?? Loc.Get("Common_Unknown"),
                disk.SerialNumber ?? Loc.Get("Common_Unknown"), WipeNumber(disk.Size ?? target.SourceLength)));
            IEnumerable<VolumeDeviceInfo> onDisk = App.Devices.EnumerateDevices().Volumes.Where(v => v.DiskNumber == disk.Number);
            string names = string.Join(", ", onDisk.Select(v => string.IsNullOrEmpty(v.Label) ? v.Name : $"{v.Name} ({v.Label})"));
            if (names.Length > 0)
            {
                Line(body, "WipeConfirm_Volumes", Loc.Format("Wipe_Confirm_Volumes", names));
            }
        }

        if (target.Device is not null)
        {
            Line(body, "WipeConfirm_Range", Loc.Format("Wipe_Confirm_Range", WipeNumber((target.Device.Info.RangeStart + range.Offset) / target.SectorSize),
                WipeNumber(range.Length / target.SectorSize), WipeNumber(range.Length)));
            if (range != requested)
            {
                Line(body, "WipeConfirm_Widened", Loc.Get("Wipe_Confirm_Widened"));
            }
        }
        else
        {
            Line(body, "WipeConfirm_Range", Loc.Format("Wipe_Confirm_RangeFile", WipeOffset(range.Offset), WipeNumber(range.Length)));
            Line(body, "WipeConfirm_Direct", Loc.Get("Wipe_Confirm_Direct"));
        }

        Line(body, "WipeConfirm_Method", MethodSummary(method));
        if (verify)
        {
            Line(body, "WipeConfirm_Verify", Loc.Get("Wipe_Confirm_Verify"));
        }

        Line(body, "WipeConfirm_Estimate", Loc.Format("Wipe_Confirm_Estimate", WipeDuration(WipeEngine.Estimate(range.Length, method.Passes.Count, verify))));
        if (volumes.Count > 0)
        {
            Line(body, "WipeConfirm_Lock", Loc.Format("Wipe_Confirm_Lock", string.Join(", ", volumes.Select(v => v.Name))));
        }

        if (RangeWipe.IsFlashMedia(disk))
        {
            Line(body, "WipeConfirm_Ssd", Loc.Get("Wipe_Confirm_Ssd"));
        }

        Line(body, "WipeConfirm_Irreversible", Loc.Get("Wipe_Confirm_Irreversible"), strong: true);
        string primary = wholeDisk ? Loc.Format("Wipe_Confirm_DiskButton", target.Name) : Loc.Format("Wipe_Confirm_Bytes", WipeNumber(range.Length));
        return await ConfirmDestructiveAsync("WipeConfirmDialog", Loc.Get("Wipe_Confirm_Title"), body, primary, wholeDisk ? target.Name : null);
    }

    /// <summary>
    /// 破壊的な操作の確認ダイアログ。<paramref name="typeToConfirm"/> があれば強い確認 (その文字を入力するまで実行ボタンを無効にする)。
    /// どちらも既定のボタンとフォーカスは「キャンセル」(Enter だけでは実行されない)。
    /// </summary>
    private async Task<bool> ConfirmDestructiveAsync(string automationId, string title, StackPanel body, string primary, string? typeToConfirm)
    {
        TextBox? input = null;
        if (typeToConfirm is not null)
        {
            Line(body, automationId + "_Prompt", Loc.Format("Wipe_Strong_Prompt", typeToConfirm));
            input = DialogParts.Field(automationId + "_Input", Loc.Get("Wipe_Strong_Field"), string.Empty, monospace: false);
            body.Children.Add(input);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, automationId, title, new ScrollViewer { Content = body, MaxHeight = 520 }, primary);
        dialog.DefaultButton = ContentDialogButton.Close;
        if (input is not null)
        {
            dialog.IsPrimaryButtonEnabled = false;
            input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = string.Equals(input.Text.Trim(), typeToConfirm, StringComparison.Ordinal);
        }

        dialog.Opened += (_, _) =>
        {
            // 既定のフォーカスを「キャンセル」に置く (共通の前提の 2)。
            if (FindDescendant<Button>(dialog, b => b.Name == "CloseButton") is { } close)
            {
                close.Focus(FocusState.Programmatic);
            }
        };
        LastWipeConfirmDialog = dialog;
        try
        {
            return await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary
                && (typeToConfirm is null || string.Equals(input!.Text.Trim(), typeToConfirm, StringComparison.Ordinal));
        }
        finally
        {
            LastWipeConfirmDialog = null;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        int seen = 0;
        while (pending.Count > 0 && seen++ < 10_000)
        {
            DependencyObject current = pending.Pop();
            if (current is T found && match(found))
            {
                return found;
            }

            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int i = 0; i < count; i++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }

        return null;
    }

    private void RecordRefusal(WipeDocumentTarget target, WipeKind kind, WipeRefusal refusal, string? detail = null) =>
        WipeForensics.Hooks.Audit(WipeAuditOperations.Wipe, target.Name, WipeAuditOutcome.Refused, new Dictionary<string, string>
        {
            ["kind"] = kind.ToString(),
            ["refusal"] = refusal.ToString(),
            ["detail"] = detail ?? string.Empty,
        });

    /// <summary>
    /// 消去を長時間処理として行う (仕様 6・9): デバイスは読み書きで開き直し、重なるボリュームをロックしてから書く。ファイルはデータエンジンを通さずに
    /// 直接書き換える (自分の書き込みは外部変更として扱わない)。書いた後に同じデータソースのドキュメントのキャッシュを捨てて読み直す。
    /// </summary>
    private async Task<WipeResult?> RunWipeJobAsync(WipeDocumentTarget target, WipeJob job, IReadOnlyList<VolumeDeviceInfo> volumes)
    {
        if (target.Device is not null && !await ReopenDeviceForWritingAsync(target.Root))
        {
            return null;
        }

        List<DocumentViewModel> documents = SameSourceDocuments(target.Root);
        var suspended = new List<(MainViewModel Vm, WatchedFile Watch)>();
        foreach (DocumentViewModel x in documents)
        {
            if (x.Watch is { } watch && WindowManager.Windows.FirstOrDefault(w => w.Vm.Documents.Contains(x)) is { } owner && owner.Vm.ExternalChanges is { } monitor)
            {
                monitor.Suspend(watch);
                suspended.Add((owner.Vm, watch));
            }
        }

        Func<WipeWriteError, WipeErrorChoice> onError = WriteErrorChoice();
        int lastPass = -1;
        bool lastVerify = false;
        try
        {
            return await Vm.Operations.RunAsync(Loc.Format("Wipe_Operation", target.Name), OperationKind.WritesExternal, target.Root.Document, job.TotalBytes,
                op =>
                {
                    LastWipeOperation = op;
                    WipeResult result = WipeRunner.Run(job, () => OpenWipeTarget(target),
                        target.Device is { } device && volumes.Count > 0 ? () => WipeVolumeLocks.Acquire(device, volumes, ConfirmDismountSync) : null,
                        op, onError, p =>
                        {
                            if (p.Pass != lastPass || p.Verifying != lastVerify)
                            {
                                lastPass = p.Pass;
                                lastVerify = p.Verifying;
                                op.ReportDetail(p.Verifying ? Loc.Get("Wipe_Progress_Verify") : Loc.Format("Wipe_Progress_Pass", p.Pass, p.Passes));
                            }
                        });
                    LastWipeResult = result;
                    return Task.FromResult(result);
                },
                locked =>
                {
                    foreach (DocumentViewModel x in documents)
                    {
                        x.Document.SetEditLock(locked);
                    }
                });
        }
        catch (WipeRefusedException ex)
        {
            await ShowMessageAsync(Loc.Get("Wipe_Title"), RefusalMessage(ex.Reason, ex.Detail), "WipeRefusedDialog");
            return null;
        }
        catch (VolumeLockException ex)
        {
            await ShowMessageAsync(Loc.Get("Wipe_Title"), Loc.Format("Wipe_LockFailed", ex.Volume), "WipeRefusedDialog");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DeviceException)
        {
            ShowNotice(Loc.Format("Wipe_Failed", ex.Message), InfoBarSeverity.Error, target.Root);
            return null;
        }
        finally
        {
            AfterDirectWrite(documents, suspended);
        }
    }

    /// <summary>直接書き込みの後: 同じデータソースのドキュメントのキャッシュを捨てて読み直し、外部変更の監視の基準を新しい内容にする。</summary>
    private void AfterDirectWrite(List<DocumentViewModel> documents, List<(MainViewModel Vm, WatchedFile Watch)> suspended)
    {
        foreach (DocumentViewModel x in documents.Where(x => !x.Document.IsDisposed))
        {
            x.Document.RefreshFromSource();
            if (FileSystemDocumentState.Find(x.Document)?.Session is { } session)
            {
                // ファイルシステムの解析の読み込みのキャッシュも古いので、同じ範囲で開き直す。
                MainWindow owner = WindowManager.Windows.FirstOrDefault(w => w.Vm.Documents.Contains(x)) ?? this;
                _ = owner.ShowFileSystemAsync(x, session.VolumeOffset, session.VolumeLength);
            }
        }

        foreach ((MainViewModel vm, WatchedFile watch) in suspended)
        {
            vm.ExternalChanges?.Rebase(watch, FileStamp.FromPath(watch.Path) ?? watch.Baseline);
        }
    }

    private static IWipeTarget OpenWipeTarget(WipeDocumentTarget target)
    {
        IWipeTarget inner = target.Device is { } device
            ? new DeviceWipeTarget(device)
            : new FileWipeTarget(target.FilePath!, target.FileBase, target.SourceLength);
        return WipeTestWriteDelay > TimeSpan.Zero ? new DelayedWipeTarget(inner, WipeTestWriteDelay) : inner;
    }

    /// <summary>テスト用: 書き込みのたびに待つ (遅いデータソースの再現)。</summary>
    private sealed class DelayedWipeTarget(IWipeTarget inner, TimeSpan delay) : IWipeTarget
    {
        public string Name => inner.Name;
        public long Length => inner.Length;
        public int SectorSize => inner.SectorSize;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            Thread.Sleep(delay);
            inner.Write(offset, data);
        }

        public void Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);
        public void Flush() => inner.Flush();
        public void Dispose() => inner.Dispose();
    }

    /// <summary>テスト用: OS 経由の消去の書き込みのたびに待つ (キャンセルのテスト)。</summary>
    private sealed class DelayedFreeSpaceVolume(IFreeSpaceVolume inner, TimeSpan delay) : IFreeSpaceVolume
    {
        public string Root => inner.Root;
        public string FileSystem => inner.FileSystem;
        public long MaxFileSize => inner.MaxFileSize;
        public long FreeBytes() => inner.FreeBytes();
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public Stream CreateFile(string path) => new DelayedStream(inner.CreateFile(path), delay);
        public Stream OpenFile(string path) => new DelayedStream(inner.OpenFile(path), delay);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public void DeleteDirectory(string path) => inner.DeleteDirectory(path);
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public bool IsDiskFull(IOException exception) => inner.IsDiskFull(exception);

        private sealed class DelayedStream(Stream stream, TimeSpan delay) : Stream
        {
            public override bool CanRead => stream.CanRead;
            public override bool CanSeek => stream.CanSeek;
            public override bool CanWrite => stream.CanWrite;
            public override long Length => stream.Length;

            public override long Position
            {
                get => stream.Position;
                set => stream.Position = value;
            }

            public override void Flush() => stream.Flush();
            public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
            public override void SetLength(long value) => stream.SetLength(value);

            public override void Write(byte[] buffer, int offset, int count)
            {
                Thread.Sleep(delay);
                stream.Write(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    stream.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// 書き込みエラーのときの選択 (仕様「エラー」): 最初のエラーで「飛ばして続ける」「中止」を UI スレッドで尋ねる。続けると以後のエラーは記録して続ける。
    /// </summary>
    private Func<WipeWriteError, WipeErrorChoice> WriteErrorChoice()
    {
        WipeErrorChoice? remembered = null;
        return error =>
        {
            if (remembered is { } choice)
            {
                return choice;
            }

            WipeErrorChoice result = WipeErrorChoice.Abort;
            using var done = new ManualResetEventSlim();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    ContentDialog dialog = DialogParts.Dialog(Root, "WipeWriteErrorDialog", Loc.Get("Wipe_WriteError_Title"),
                        new TextBlock { Text = Loc.Format("Wipe_WriteError_Body", WipeOffset(error.Offset), error.Message), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                        Loc.Get("Wipe_WriteError_Continue"));
                    dialog.CloseButtonText = Loc.Get("Wipe_WriteError_Abort");
                    dialog.DefaultButton = ContentDialogButton.Close;
                    result = await dialog.ShowQueuedAsync() == ContentDialogResult.Primary ? WipeErrorChoice.Continue : WipeErrorChoice.Abort;
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait(TimeSpan.FromMinutes(30));
            remembered = result;
            return result;
        };
    }

    /// <summary>
    /// 処理センターのキャンセルの確認の文 (仕様「巨大ファイル・長時間処理」)。統合の注記: 処理センターがキャンセルの確認を持つ版
    /// (<c>LongRunningOperation.ConfirmCancel</c>) では、消去の処理にこの確認を設定する。
    /// </summary>
    internal Task<bool> ConfirmWipeCancelAsync() =>
        ConfirmAsync(Loc.Get("Wipe_Title"), Loc.Get("Wipe_CancelConfirm"), Loc.Get("Wipe_WriteError_Abort"), "WipeCancelDialog");

    private static string RefusalMessage(WipeRefusal refusal, string? detail) => refusal switch
    {
        WipeRefusal.WriteBlock => Loc.Get("WriteBlock_Enabled"),
        WipeRefusal.Evidence => Loc.Get("Wipe_Evidence"),
        WipeRefusal.SystemDisk => Loc.Get("Wipe_SystemDisk"),
        WipeRefusal.Inconsistent => Loc.Format("WipeFree_Inconsistent", detail ?? string.Empty),
        WipeRefusal.UnsupportedFileSystem => Loc.Get("WipeFree_Unsupported"),
        WipeRefusal.UnsavedEdits => Loc.Get("WipeFree_UnsavedEdits"),
        WipeRefusal.PolicyDenied => Loc.Format("Wipe_Refused_Policy", detail ?? string.Empty),
        _ => Loc.Get("Wipe_Unsupported"),
    };

    /// <summary>結果の表示 (仕様「エラー」: 書けなかった範囲・検証の不一致、キャンセルした位置)。</summary>
    private async Task ShowWipeResultAsync(WipeResult result, string summary, string? extra)
    {
        if (result.Succeeded && extra is null)
        {
            ShowNotice(summary + (result.Verified ? " " + Loc.Get("Wipe_Result_Verified") : string.Empty), InfoBarSeverity.Success);
            return;
        }

        var body = new StackPanel { Spacing = 8, MaxWidth = 480 };
        if (result.Cancelled)
        {
            Line(body, "WipeResult_Cancelled", result.StoppedAt < 0
                ? Loc.Get("Wipe_Result_CancelledVerify")
                : Loc.Format("Wipe_Result_Cancelled", WipeOffset(result.StoppedAt), result.StoppedPass));
        }
        else if (result.Aborted)
        {
            Line(body, "WipeResult_Aborted", Loc.Format("Wipe_Result_Aborted", WipeOffset(result.StoppedAt)));
        }
        else
        {
            Line(body, "WipeResult_Summary", summary);
        }

        if (extra is not null)
        {
            Line(body, "WipeResult_Extra", extra);
        }

        if (result.Unwritten.Count > 0)
        {
            Line(body, "WipeResult_Unwritten", Loc.Format("Wipe_Result_Unwritten", result.Unwritten.Count) + Environment.NewLine
                + string.Join(Environment.NewLine, result.Unwritten.Take(50).Select(r => $"{WipeOffset(r.Offset)} +{WipeNumber(r.Length)}")));
        }

        if (result.Mismatches.Count > 0)
        {
            Line(body, "WipeResult_Mismatches", Loc.Format("Wipe_Result_Mismatch", result.Mismatches.Count) + Environment.NewLine
                + string.Join(Environment.NewLine, result.Mismatches.Take(50).Select(r => $"{WipeOffset(r.Offset)} +{WipeNumber(r.Length)}")));
        }
        else if (result.Verified && !result.Cancelled && !result.Aborted)
        {
            Line(body, "WipeResult_Verified", Loc.Get("Wipe_Result_Verified"));
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "WipeResultDialog", Loc.Get("Wipe_Result_Title"), new ScrollViewer { Content = body, MaxHeight = 480 },
            Loc.Get("Common_Close"));
        dialog.CloseButtonText = string.Empty;
        await ShowEditDialogAsync(dialog);
    }

    // ---- 空き領域 (FOR-31) ----

    private async Task ShowWipeFreeSpaceAsync()
    {
        if (!WipeFreeSpaceState().Enabled)
        {
            return;
        }

        DocumentViewModel? d = Vm.Selected;
        FileSystemSession? session = d is null ? null : StateOf(d)?.Session;
        WipeDocumentTarget? target = d is null ? null : ResolveWipeTarget(d);
        bool directPossible = session is not null && target is not null && FileSystemWipe.IsSupported(session.FileSystem);
        DriveInfo[] drives = [.. MountedVolumes()];
        RadioButton os = DialogParts.Radio("WipeFree_Os", Loc.Get("WipeFree_Os"), "WipeFreeHow", drives.Length > 0 || !directPossible);
        RadioButton direct = DialogParts.Radio("WipeFree_Direct", Loc.Get("WipeFree_Direct"), "WipeFreeHow", drives.Length == 0 && directPossible);
        direct.IsEnabled = directPossible;
        ComboBox volume = DialogParts.Combo("WipeFree_Volume", Loc.Get("WipeFree_Volume"), drives.Select(VolumeText), 0);
        TextBlock note = ToolParts.ErrorText("WipeFree_Note");
        MethodInputs methods = CreateMethodInputs("WipeFree");
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        body.Children.Add(Strong(Loc.Get("WipeFree_HowTo")));
        body.Children.Add(os);
        body.Children.Add(volume);
        body.Children.Add(direct);
        body.Children.Add(note);
        body.Children.Add(Strong(Loc.Get("Shred_Method")));
        foreach (UIElement element in methods.Elements)
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "WipeFreeDialog", Loc.Get("WipeFree_Title"), new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Wipe_Next"));
        void Update()
        {
            methods.Passes.IsEnabled = methods.Patterns.IsEnabled = methods.Custom.IsChecked == true;
            volume.IsEnabled = os.IsChecked == true;
            ShredMethod? method = methods.Method(out string? problem);
            ToolParts.ShowError(methods.Error, null, problem);
            ToolParts.ShowError(note, null, !directPossible ? Loc.Get(session is null ? "WipeFree_NoSession" : "WipeFree_Unsupported")
                : drives.Length == 0 ? Loc.Get("WipeFree_NoVolumes") : null);
            dialog.IsPrimaryButtonEnabled = method is not null && (os.IsChecked == true ? drives.Length > 0 && volume.SelectedIndex >= 0 : directPossible);
        }

        foreach (RadioButton radio in new[] { methods.Zero, methods.Random, methods.Dod, methods.Custom, os, direct })
        {
            radio.Checked += (_, _) => Update();
        }

        methods.Passes.TextChanged += (_, _) => Update();
        methods.Patterns.TextChanged += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || methods.Method(out _) is not { } chosen)
        {
            return;
        }

        if (os.IsChecked == true)
        {
            await WipeFreeSpaceOsAsync(new WindowsFreeSpaceVolume(drives[volume.SelectedIndex].RootDirectory.FullName), drives[volume.SelectedIndex], chosen);
        }
        else
        {
            await WipeFileSystemDirectAsync(d!, target!, session!, chosen, slack: false);
        }
    }

    /// <summary>OS 経由の消去の対象にできるマウント中のボリューム (固定・取り外し可能で準備ができているもの)。一覧を取るだけで書き込まない。</summary>
    private static IEnumerable<DriveInfo> MountedVolumes()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            bool ok;
            try
            {
                ok = drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable;
            }
            catch (IOException)
            {
                ok = false;
            }

            if (ok)
            {
                yield return drive;
            }
        }
    }

    private static string VolumeText(DriveInfo drive)
    {
        try
        {
            return string.IsNullOrEmpty(drive.VolumeLabel) ? $"{drive.Name} ({drive.DriveFormat})" : $"{drive.Name} {drive.VolumeLabel} ({drive.DriveFormat})";
        }
        catch (IOException)
        {
            return drive.Name;
        }
    }

    /// <summary>OS 経由の空き領域の消去 (仕様 1): 確認 → 長時間処理 → 結果 (上書きした容量、所要時間、削除できなかった一時ファイル)。</summary>
    internal async Task<OsFreeSpaceWipeResult?> WipeFreeSpaceOsAsync(IFreeSpaceVolume volume, DriveInfo? drive, ShredMethod method)
    {
        string name = drive is not null ? VolumeText(drive) : volume.Root;
        long free = volume.FreeBytes();
        var body = new StackPanel { Spacing = 8, MaxWidth = 480 };
        Line(body, "WipeFreeConfirm_Volume", Loc.Format("WipeFree_Confirm_Volume", name));
        Line(body, "WipeFreeConfirm_Bytes", Loc.Format("WipeFree_Confirm_Bytes", WipeNumber(free)));
        Line(body, "WipeFreeConfirm_How", Loc.Get("WipeFree_Confirm_HowOs"));
        Line(body, "WipeFreeConfirm_Method", MethodSummary(method));
        Line(body, "WipeFreeConfirm_Estimate", Loc.Format("Wipe_Confirm_Estimate", WipeDuration(WipeEngine.Estimate(free, method.Passes.Count, false))));
        Line(body, "WipeFreeConfirm_OsNote", Loc.Get("WipeFree_Confirm_OsNote"));
        Line(body, "WipeFreeConfirm_Irreversible", Loc.Get("Wipe_Confirm_Irreversible"), strong: true);
        if (!await ConfirmDestructiveAsync("WipeFreeConfirmDialog", Loc.Get("WipeFree_Title"), body, Loc.Get("WipeFree_Button"), null))
        {
            return null;
        }

        int lastPass = -1;
        if (WipeTestWriteDelay > TimeSpan.Zero)
        {
            volume = new DelayedFreeSpaceVolume(volume, WipeTestWriteDelay);
        }

        try
        {
            OsFreeSpaceWipeResult result = await Vm.Operations.RunAsync(Loc.Format("Wipe_Operation", name), OperationKind.WritesExternal, null, free * method.Passes.Count,
                op =>
                {
                    LastWipeOperation = op;
                    OsFreeSpaceWipeResult r = WipeRunner.RunOs(volume, name, "volume:" + volume.Root.ToUpperInvariant(), method, op, p =>
                    {
                        if (p.Pass != lastPass)
                        {
                            lastPass = p.Pass;
                            op.ReportDetail(Loc.Format("Wipe_Progress_Pass", p.Pass, p.Passes));
                        }
                    });
                    LastWipeResult = r;
                    return Task.FromResult(r);
                });
            var lines = new List<string>();
            if (result.Leftovers.Count > 0)
            {
                lines.Add(Loc.Get("WipeFree_Leftovers") + Environment.NewLine + string.Join(Environment.NewLine, result.Leftovers.Take(50)));
            }

            if (result.Error is { } error)
            {
                lines.Add(Loc.Format("Wipe_Failed", error));
            }

            string summary = result.Cancelled ? Loc.Get("WipeFree_Cancelled")
                : Loc.Format("WipeFree_Result", WipeNumber(result.BytesOverwritten), WipeDuration(result.Elapsed));
            if (lines.Count == 0 && !result.Cancelled)
            {
                ShowNotice(summary, InfoBarSeverity.Success);
            }
            else
            {
                var resultBody = new StackPanel { Spacing = 8, MaxWidth = 480 };
                Line(resultBody, "WipeResult_Summary", summary);
                foreach (string line in lines)
                {
                    Line(resultBody, "WipeResult_Extra", line);
                }

                ContentDialog dialog = DialogParts.Dialog(Root, "WipeResultDialog", Loc.Get("Wipe_Result_Title"), new ScrollViewer { Content = resultBody, MaxHeight = 480 },
                    Loc.Get("Common_Close"));
                dialog.CloseButtonText = string.Empty;
                await ShowEditDialogAsync(dialog);
            }

            return result;
        }
        catch (WipeRefusedException ex)
        {
            await ShowMessageAsync(Loc.Get("WipeFree_Title"), RefusalMessage(ex.Reason, ex.Detail), "WipeRefusedDialog");
            return null;
        }
    }

    // ---- スラック (FOR-31) ----

    private async Task ShowWipeSlackAsync()
    {
        if (Vm.Selected is not { } d || !WipeSlackState().Enabled || StateOf(d)?.Session is not { } session || ResolveWipeTarget(d) is not { } target)
        {
            return;
        }

        MethodInputs methods = CreateMethodInputs("WipeSlack");
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        Line(body, "WipeSlack_TargetText", Loc.Format("Wipe_Target", target.Name));
        body.Children.Add(Strong(Loc.Get("Shred_Method")));
        foreach (UIElement element in methods.Elements)
        {
            body.Children.Add(element);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "WipeSlackDialog", Loc.Get("WipeSlack_Title"), new ScrollViewer { Content = body, MaxHeight = 560 }, Loc.Get("Wipe_Next"));
        void Update()
        {
            methods.Passes.IsEnabled = methods.Patterns.IsEnabled = methods.Custom.IsChecked == true;
            ShredMethod? method = methods.Method(out string? problem);
            ToolParts.ShowError(methods.Error, null, problem);
            dialog.IsPrimaryButtonEnabled = method is not null;
        }

        foreach (RadioButton radio in new[] { methods.Zero, methods.Random, methods.Dod, methods.Custom })
        {
            radio.Checked += (_, _) => Update();
        }

        methods.Passes.TextChanged += (_, _) => Update();
        methods.Patterns.TextChanged += (_, _) => Update();
        Update();
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || methods.Method(out _) is not { } chosen)
        {
            return;
        }

        await WipeFileSystemDirectAsync(d, target, session, chosen, slack: true);
    }

    /// <summary>
    /// 直接書き込みの空き領域・スラックの消去 (仕様 1・2・4): 未保存の編集の拒否 → 不整合の検査 (dirty フラグなど。あれば「chkdsk で修復してから」) →
    /// 範囲を求める (長時間処理) → 確認 → 消去 (ボリュームをロック) → 結果 (上書きした容量、対象外のファイルの数、所要時間)。
    /// </summary>
    internal async Task<WipeResult?> WipeFileSystemDirectAsync(DocumentViewModel d, WipeDocumentTarget target, FileSystemSession session, ShredMethod method, bool slack)
    {
        WipeKind kind = slack ? WipeKind.Slack : WipeKind.FreeSpaceDirect;
        string title = Loc.Get(slack ? "WipeSlack_Title" : "WipeFree_Title");
        if (SameSourceDocuments(target.Root).Any(x => x.Document.IsModified))
        {
            RecordRefusal(target, kind, WipeRefusal.UnsavedEdits);
            await ShowMessageAsync(title, Loc.Get("WipeFree_UnsavedEdits"), "WipeRefusedDialog");
            return null;
        }

        IFileSystem fs = session.FileSystem;
        long volumeOffset = target.BaseOffset + session.VolumeOffset;
        try
        {
            await Task.Run(() => FileSystemWipe.EnsureCanWriteDirect(fs, target.Name, kind));
        }
        catch (WipeRefusedException ex)
        {
            await ShowMessageAsync(title, RefusalMessage(ex.Reason, ex.Detail), "WipeRefusedDialog");
            return null;
        }

        // 範囲を求める (スラックはファイルの一覧の走査、空き領域は割り当て情報の走査)。
        SlackWipePlan? plan = null;
        long bytes;
        try
        {
            if (slack)
            {
                plan = await Vm.Operations.RunAsync(Loc.Get("WipeSlack_Planning"), OperationKind.ReadOnly, d.Document, null,
                    op => Task.FromResult(FileSystemWipe.PlanSlack(fs, volumeOffset, OperationProgress(op), op.CancellationToken)));
                bytes = plan.TotalBytes;
                LastSlackSkipped = plan.Skipped;
            }
            else
            {
                bytes = await Vm.Operations.RunAsync(Loc.Get("WipeFree_Calculating"), OperationKind.ReadOnly, d.Document, null,
                    op => Task.FromResult(FileSystemWipe.FreeBytes(fs, volumeOffset, op.CancellationToken)));
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        IReadOnlyList<VolumeDeviceInfo> volumes = [];
        DiskInfo? disk = target.Device?.Info.Disk;
        if (target.Device is { } device)
        {
            DeviceCatalog catalog = App.Devices.EnumerateDevices();
            volumes = WipeVolumeLocks.Overlapping(device, catalog, [(volumeOffset, session.VolumeLength)], out IReadOnlyList<VolumeDeviceInfo> blocked);
            if (blocked.Count > 0 || RangeWipe.IsSystemDisk(disk, catalog) || device.Info.Volume is { IsInUseByWindows: true })
            {
                RecordRefusal(target, kind, WipeRefusal.SystemDisk);
                await ShowMessageAsync(title, Loc.Get("Wipe_SystemDisk"), "WipeRefusedDialog");
                return null;
            }
        }

        var body = new StackPanel { Spacing = 8, MaxWidth = 480 };
        string volumeName = $"{target.Name} ({FileSystemDetector.DisplayName(fs.Kind)}{(fs.Summary.Label.Length > 0 ? ", " + fs.Summary.Label : string.Empty)})";
        Line(body, "WipeFreeConfirm_Volume", Loc.Format("WipeFree_Confirm_Volume", volumeName));
        if (slack)
        {
            Line(body, "WipeFreeConfirm_Bytes", Loc.Format("WipeSlack_Confirm_Files", WipeNumber(plan!.Files), WipeNumber(bytes)));
            SlackSkipCounts s = plan.Skipped;
            Line(body, "WipeFreeConfirm_Skipped", Loc.Format("Wipe_Result_Skipped", s.Total, s.Compressed, s.Sparse, s.Resident, s.Inconsistent));
        }
        else
        {
            Line(body, "WipeFreeConfirm_Bytes", Loc.Format("WipeFree_Confirm_Bytes", WipeNumber(bytes)));
            Line(body, "WipeFreeConfirm_How", Loc.Get("WipeFree_Confirm_HowDirect"));
        }

        Line(body, "WipeFreeConfirm_Method", MethodSummary(method));
        Line(body, "WipeFreeConfirm_Estimate", Loc.Format("Wipe_Confirm_Estimate", WipeDuration(WipeEngine.Estimate(bytes, method.Passes.Count, false))));
        if (volumes.Count > 0)
        {
            Line(body, "WipeConfirm_Lock", Loc.Format("Wipe_Confirm_Lock", string.Join(", ", volumes.Select(v => v.Name))));
        }

        if (RangeWipe.IsFlashMedia(disk))
        {
            Line(body, "WipeConfirm_Ssd", Loc.Get("Wipe_Confirm_Ssd"));
        }

        Line(body, "WipeFreeConfirm_DirectNote", Loc.Get("WipeFree_Confirm_DirectNote"));
        Line(body, "WipeFreeConfirm_Irreversible", Loc.Get("Wipe_Confirm_Irreversible"), strong: true);
        if (!await ConfirmDestructiveAsync("WipeFreeConfirmDialog", title, body, Loc.Get(slack ? "WipeSlack_Button" : "WipeFree_Button"), null))
        {
            return null;
        }

        var job = new WipeJob
        {
            Kind = kind,
            TargetName = target.Name,
            Identity = target.Identity,
            Path = target.Path,
            Ranges = slack ? plan!.Ranges : FileSystemWipe.FreeRanges(fs, volumeOffset),
            TotalBytes = bytes,
            Method = method,
            Verify = false,
            ChunkAlignment = slack ? 1 : (int)Math.Min(fs.ClusterSize, WipeEngine.ChunkSize),
            Details = new Dictionary<string, string>
            {
                ["fileSystem"] = fs.Kind.ToString(),
                ["volumeOffset"] = volumeOffset.ToString(CultureInfo.InvariantCulture),
                ["skipped"] = plan?.Skipped.Total.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            },
        };
        WipeResult? result = await RunWipeJobAsync(target, job, volumes);
        if (result is not null)
        {
            string summary = Loc.Format(slack ? "WipeSlack_Result" : "WipeFree_Result", WipeNumber(result.TotalBytes), WipeDuration(result.Elapsed));
            string? extra = plan is { } p2
                ? Loc.Format("Wipe_Result_Skipped", p2.Skipped.Total, p2.Skipped.Compressed, p2.Skipped.Sparse, p2.Skipped.Resident, p2.Skipped.Inconsistent)
                : null;
            await ShowWipeResultAsync(result, summary, extra);
        }

        return result;
    }
}
