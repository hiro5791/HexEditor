using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Controls.Tools;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Notifications;
using HexEditor.Core.Operations;
using HexEditor.Core.Panels;
using HexEditor.Core.Tools.Files;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// NTFS 代替データストリーム (ENG-37) の、ドキュメントとして開く処理と、管理画面 (TOOL-21)。管理画面はドッキングできるパネルで、ストリームの一覧
/// (名前・サイズ・先頭の Hex・既知のストリームの説明) と、開く・作成・名前の変更・削除・書き出し・読み込みのボタンを持つ。確認や名前の入力は
/// ContentDialog で行う (パネルなので ContentDialog と重ならない)。書き込み保護モードの間は一覧と「開く」だけができる。
/// </summary>
public sealed partial class MainWindow
{
    public const string AdsPanelId = "ads";

    /// <summary>一覧の 1 行。</summary>
    internal sealed record AdsRow(StreamInfo Info, byte[] Head, string Description);

    private readonly List<object> _adsRows = [];
    private ToolResultTable? _adsTable;
    private string? _adsFile;
    private string _adsSummary = string.Empty;
    private bool _adsSupported = true;
    private readonly Dictionary<string, Button> _adsButtons = [];

    private static void RegisterAdsPanel()
    {
        if (PanelRegistry.Find(AdsPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(AdsPanelId, "Panel_Ads_Title", PanelDock.Bottom, ctx => ((MainWindow)ctx.Window).CreateAdsTable())
            {
                ToggleCommand = "tools.ads.panel",
                RequiresDocument = false,
                CanFloat = false,
            });
        }
    }

    private void RegisterAdsCommands()
    {
        Commands.Register("tools.ads", () => ShowAdsAsync(), AdsManageState);
        Commands.Register("file.ads.open", () => ShowAdsAsync(), AdsOpenState);
    }

    /// <summary>ストリームの対象にするファイル: アクティブなドキュメントのファイル (ストリームのドキュメントならそのファイル)。</summary>
    private string? AdsTargetOfActive()
    {
        string? path = Vm.Selected?.FilePath;
        if (path is null)
        {
            return null;
        }

        string file = AlternateStreams.SplitStreamPath(path)?.File ?? path;
        return File.Exists(file) || Directory.Exists(file) ? file : null;
    }

    private CommandState AdsManageState() =>
        AdsTargetOfActive() is not null || _adsFile is not null ? CommandState.Available : CommandState.Unavailable(Loc.Get("Ads_NoFile"));

    private CommandState AdsOpenState()
    {
        string? target = AdsTargetOfActive();
        if (target is null)
        {
            return CommandState.Unavailable(Loc.Get("Ads_NoFile"));
        }

        // ADS に対応しないファイルシステム (FAT・exFAT など) では無効にし、理由を示す (ENG-37 の仕様 5)。
        return AlternateStreams.IsSupported(target, Volumes) ? CommandState.Available : CommandState.Unavailable(Loc.Get("Ads_Unsupported"));
    }

    /// <summary>管理画面を開く(対象はアクティブなドキュメントのファイル)。</summary>
    internal Task ShowAdsAsync(string? file = null)
    {
        file ??= AdsTargetOfActive() ?? _adsFile;
        ShowPanel(AdsPanelId);
        if (file is not null)
        {
            LoadAds(file);
        }
        else
        {
            RefreshAdsView();
        }

        return Task.CompletedTask;
    }

    private ToolResultTable CreateAdsTable()
    {
        var table = new ToolResultTable("Ads");
        table.Configure(
        [
            new ToolColumn(Loc.Get("Ads_Col_Name"), 220, r => ((AdsRow)r).Info.DisplayName, r => ((AdsRow)r).Info.DisplayName),
            new ToolColumn(Loc.Get("Ads_Col_Size"), 110, r => Bytes(((AdsRow)r).Info.Size), r => ((AdsRow)r).Info.Size),
            new ToolColumn(Loc.Get("Ads_Col_Head"), 260, r => HeadHex(((AdsRow)r).Head)),
            new ToolColumn(Loc.Get("Ads_Col_Description"), 0, r => ((AdsRow)r).Description),
        ],
        [(Loc.Get("Ads_Filter_All"), null)]);
        foreach (object row in _adsRows)
        {
            table.Add(row);
        }

        _adsButtons.Clear();
        AddAdsButton(table, "Choose", "Ads_Choose", () => _ = ChooseAdsFileAsync());
        AddAdsButton(table, "ChooseFolder", "Ads_ChooseFolder", () => _ = ChooseAdsFolderAsync());
        AddAdsButton(table, "Open", "Ads_Open", () => _ = OpenSelectedStreamAsync());
        AddAdsButton(table, "Create", "Ads_Create", () => _ = CreateStreamAsync());
        AddAdsButton(table, "Rename", "Ads_Rename", () => _ = RenameStreamAsync());
        AddAdsButton(table, "Delete", "Ads_Delete", () => _ = DeleteStreamAsync());
        AddAdsButton(table, "Export", "Ads_Export", () => _ = ExportStreamAsync());
        AddAdsButton(table, "Import", "Ads_Import", () => _ = ImportStreamAsync());
        table.RowActivated += (_, _) => _ = OpenSelectedStreamAsync();
        table.SelectionChanged += (_, _) => UpdateAdsButtons();
        table.ExportRequested += async (_, format) => await ExportToolTableAsync(table, "streams", format);
        _adsTable = table;
        RefreshAdsView();
        return table;
    }

    private void AddAdsButton(ToolResultTable table, string name, string textKey, Action click)
    {
        Button button = ToolParts.TextButton("Ads_" + name, textKey, click);
        table.ExtraControls.Children.Add(button);
        _adsButtons[name] = button;
    }

    private static string HeadHex(byte[] head) => head.Length == 0 ? string.Empty : DialogParts.Hex(head);

    private AdsRow? SelectedAdsRow => _adsTable?.SelectedRow as AdsRow;

    private void UpdateAdsButtons()
    {
        if (_adsTable is null)
        {
            return;
        }

        AdsRow? row = SelectedAdsRow;
        bool has = row is not null && _adsFile is not null && _adsSupported;

        // フォルダのストリームは一覧・作成・削除などができるが、ドキュメントとしては開けない (TOOL-21 の仕様 5)。
        bool isFile = _adsFile is not null && File.Exists(_adsFile);
        bool writable = !WriteBlockMode.IsEnabled && _adsSupported && _adsFile is not null;
        bool named = row is { Info.IsDefault: false };
        _adsButtons["Open"].IsEnabled = has && isFile;
        _adsButtons["Create"].IsEnabled = writable;
        _adsButtons["Rename"].IsEnabled = writable && has && named;
        _adsButtons["Delete"].IsEnabled = writable && has && named;
        _adsButtons["Export"].IsEnabled = has;
        _adsButtons["Import"].IsEnabled = writable && has;
        string? block = WriteBlockMode.IsEnabled ? Loc.Get("WriteBlock_Enabled") : null;
        foreach (string name in new[] { "Create", "Rename", "Delete", "Import" })
        {
            ToolTipService.SetToolTip(_adsButtons[name], block);
        }
    }

    private void RefreshAdsView()
    {
        if (_adsTable is null)
        {
            return;
        }

        _adsTable.Clear();
        foreach (object row in _adsRows)
        {
            _adsTable.Add(row);
        }

        _adsTable.SetSummary(_adsSummary);
        UpdateAdsButtons();
    }

    /// <summary>ファイル (フォルダ) のストリームを列挙して一覧に出す。</summary>
    internal void LoadAds(string file)
    {
        _adsFile = Path.GetFullPath(file);
        _adsRows.Clear();
        _adsSupported = AlternateStreams.IsSupported(_adsFile, Volumes);
        if (!_adsSupported)
        {
            _adsSummary = Loc.Format("Ads_SummaryUnsupported", _adsFile);
        }
        else
        {
            try
            {
                foreach (StreamInfo info in AlternateStreams.List(_adsFile))
                {
                    byte[] head = AlternateStreams.ReadHead(_adsFile, info.Name);
                    string? key = AlternateStreams.KnownKey(info.Name);
                    _adsRows.Add(new AdsRow(info, head, key is null ? string.Empty : Loc.Get("Ads_Known_" + key)));
                }

                _adsSummary = Loc.Format("Ads_Summary", _adsFile, _adsRows.Count);
            }
            catch (StreamException ex)
            {
                _adsSupported = ex.Error != StreamError.NotSupported;
                _adsSummary = ex.Error == StreamError.NotSupported ? Loc.Format("Ads_SummaryUnsupported", _adsFile) : Loc.Format("Ads_SummaryError", _adsFile, StreamMessage(ex));
            }
        }

        RefreshAdsView();
    }

    private static string StreamMessage(StreamException ex) => ex.Error switch
    {
        StreamError.InvalidName => Loc.Get("Ads_Error_InvalidName"),
        StreamError.AlreadyExists => Loc.Get("Ads_Error_Exists"),
        StreamError.NotFound => Loc.Get("Ads_Error_NotFound"),
        StreamError.NotSupported => Loc.Get("Ads_Unsupported"),
        StreamError.AccessDenied => Loc.Get("Ads_Error_Access"),
        StreamError.InUse => Loc.Get("Ads_Error_InUse"),
        StreamError.TooLargeOnRefs => Loc.Get("Ads_Error_Refs"),
        StreamError.DefaultStream => Loc.Get("Ads_Error_Default"),
        _ => ex.Message,
    };

    private async Task ChooseAdsFileAsync()
    {
        if ((await PickFilesAsync("HexEditor.AdsFile", multiple: false)).FirstOrDefault() is { } picked)
        {
            LoadAds(picked);
        }
    }

    private async Task ChooseAdsFolderAsync()
    {
        if (await PickFolderToolAsync("HexEditor.AdsFolder") is { } picked)
        {
            LoadAds(picked);
        }
    }

    // ---- 開く (ENG-37) ----

    /// <summary>タブで開いているストリーム (全ウィンドウ)。</summary>
    private static DocumentViewModel? OpenStreamTab(string file, string stream)
    {
        string path = AlternateStreams.PathOf(Path.GetFullPath(file), stream);
        return WindowManager.Windows.SelectMany(w => w.Vm.Documents)
            .FirstOrDefault(d => d.FilePath is { } p && string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    private Task OpenSelectedStreamAsync() => SelectedAdsRow is { } row && _adsFile is not null ? OpenStreamAsync(_adsFile, row.Info.Name) : Task.CompletedTask;

    /// <summary>ストリームを新しいタブで開く(タブの名前は <c>ファイル名:ストリーム名</c>)。</summary>
    internal Task OpenStreamAsync(string file, string stream)
    {
        string path = AlternateStreams.PathOf(Path.GetFullPath(file), stream);
        try
        {
            DocumentViewModel vm = Vm.Open(path);
            Vm.Selected = vm;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Ads_Error_Open", path, ex.Message), InfoBarSeverity.Error);
        }

        return Task.CompletedTask;
    }

    // ---- 作成・名前の変更 ----

    /// <summary>名前の入力ダイアログ: 使えない文字・同名があれば入力欄を赤枠にして、確定を無効にする。名前を返す(キャンセルは null)。</summary>
    private async Task<string?> AskStreamNameAsync(string title, string primary, string initial, string file)
    {
        TextBox box = DialogParts.Field("Ads_NameBox", Loc.Get("Ads_NameHeader"), initial, monospace: false);
        TextBlock hint = ToolParts.ErrorText("Ads_NameError");
        var body = new StackPanel { Spacing = 8, MinWidth = 360 };
        body.Children.Add(box);
        body.Children.Add(hint);
        ContentDialog dialog = DialogParts.Dialog(Root, "AdsNameDialog", title, body, primary);
        var existing = AlternateStreams.List(file).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        void Update()
        {
            string name = box.Text;
            string? problem = name.Length == 0 ? null
                : !AlternateStreams.IsValidName(name) ? Loc.Get("Ads_Error_InvalidName")
                : existing.Contains(name) && !string.Equals(name, initial, StringComparison.Ordinal) ? Loc.Get("Ads_Error_Exists") : null;
            ToolParts.ShowError(hint, box, problem);
            dialog.IsPrimaryButtonEnabled = name.Length > 0 && problem is null && !string.Equals(name, initial, StringComparison.Ordinal);
        }

        box.TextChanged += (_, _) => Update();
        Update();
        return await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary ? box.Text : null;
    }

    private async Task CreateStreamAsync()
    {
        if (_adsFile is not { } file || WriteBlockMode.IsEnabled)
        {
            return;
        }

        if (await AskStreamNameAsync(Loc.Get("Ads_CreateTitle"), Loc.Get("Ads_CreateRun"), string.Empty, file) is { } name)
        {
            await CreateStreamAsync(file, name, open: true);
        }
    }

    /// <summary>空のストリームを作って開く(同名があれば作らない)。</summary>
    internal async Task<bool> CreateStreamAsync(string file, string name, bool open)
    {
        try
        {
            AlternateStreams.Create(file, name);
        }
        catch (StreamException ex)
        {
            ShowNotice(StreamMessage(ex), InfoBarSeverity.Error);
            return false;
        }

        LoadAds(file);
        if (open && File.Exists(file))
        {
            await OpenStreamAsync(file, name);
        }

        return true;
    }

    private bool StreamInUse(string file, string stream)
    {
        if (OpenStreamTab(file, stream) is null)
        {
            return false;
        }

        ShowNotice(Loc.Format("Ads_Error_OpenInTab", AlternateStreams.PathOf(Path.GetFileName(file), stream)), InfoBarSeverity.Warning);
        return true;
    }

    private async Task RenameStreamAsync()
    {
        if (_adsFile is not { } file || SelectedAdsRow is not { Info.IsDefault: false } row || WriteBlockMode.IsEnabled || StreamInUse(file, row.Info.Name))
        {
            return;
        }

        if (await AskStreamNameAsync(Loc.Get("Ads_RenameTitle"), Loc.Get("Ads_RenameRun"), row.Info.Name, file) is { } name)
        {
            await RenameStreamAsync(file, row.Info.Name, name);
        }
    }

    internal async Task<bool> RenameStreamAsync(string file, string stream, string newName)
    {
        if (StreamInUse(file, stream))
        {
            return false;
        }

        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Ads_Operation_Rename", stream), OperationKind.WritesExternal, null, null,
                op => { AlternateStreams.Rename(file, stream, newName, op, op.CancellationToken); return Task.CompletedTask; });
        }
        catch (StreamException ex)
        {
            ShowNotice(StreamMessage(ex), InfoBarSeverity.Error);
            return false;
        }
        catch (OperationCanceledException)
        {
            ShowNotice(Loc.Get("FileOps_Cancelled"), InfoBarSeverity.Informational);
        }

        LoadAds(file);
        return true;
    }

    // ---- 削除 ----

    private async Task DeleteStreamAsync()
    {
        if (_adsFile is not { } file || SelectedAdsRow is not { Info.IsDefault: false } row || WriteBlockMode.IsEnabled || StreamInUse(file, row.Info.Name))
        {
            return;
        }

        var body = new StackPanel { Spacing = 8, MaxWidth = 420 };
        var name = new TextBlock { Text = Loc.Format("Ads_Delete_Body", row.Info.Name, Bytes(row.Info.Size)), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(name, "Ads_DeleteBody");
        var irreversible = new TextBlock { Text = Loc.Get("Ads_Delete_Irreversible"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(irreversible, "Ads_DeleteIrreversible");
        body.Children.Add(name);
        body.Children.Add(irreversible);
        ContentDialog dialog = DialogParts.Dialog(Root, "AdsDeleteDialog", Loc.Get("Ads_Delete_Title"), body, Loc.Get("Ads_Delete_Run"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary)
        {
            return;
        }

        await DeleteStreamAsync(file, row.Info.Name);
    }

    internal Task<bool> DeleteStreamAsync(string file, string stream)
    {
        if (WriteBlockMode.IsEnabled || StreamInUse(file, stream))
        {
            return Task.FromResult(false);
        }

        try
        {
            AlternateStreams.Delete(file, stream);
        }
        catch (StreamException ex)
        {
            ShowNotice(StreamMessage(ex), InfoBarSeverity.Error);
            return Task.FromResult(false);
        }

        LoadAds(file);
        return Task.FromResult(true);
    }

    // ---- 書き出し・読み込み ----

    private async Task ExportStreamAsync()
    {
        if (_adsFile is not { } file || SelectedAdsRow is not { } row)
        {
            return;
        }

        string suggested = (row.Info.IsDefault ? Path.GetFileName(file) : row.Info.Name.Replace(':', '_')) + ".bin";
        string? path;
        if (!TestHooks.TrySavePicker(suggested, out path))
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.AdsExport" };
            picker.FileTypeChoices.Add(Loc.Get("Ads_ExportType"), [".bin"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        await ExportStreamAsync(file, row.Info.Name, path);
    }

    internal async Task<bool> ExportStreamAsync(string file, string stream, string destination)
    {
        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Ads_Operation_Export", stream.Length == 0 ? Path.GetFileName(file) : stream), OperationKind.WritesExternal, null, null,
                op => { AlternateStreams.Export(file, stream, destination, op, op.CancellationToken); return Task.CompletedTask; });
            ShowNotice(Loc.Format("Ads_Exported", Path.GetFileName(destination)), InfoBarSeverity.Success);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowNotice(Loc.Format("Ads_Error_Write", ex.Message), InfoBarSeverity.Error);
            return false;
        }
    }

    private async Task ImportStreamAsync()
    {
        if (_adsFile is not { } file || SelectedAdsRow is not { } row || WriteBlockMode.IsEnabled || StreamInUse(file, row.Info.Name))
        {
            return;
        }

        if (!await ConfirmAsync(Loc.Get("Ads_Import_Title"), Loc.Format("Ads_Import_Body", row.Info.DisplayName), Loc.Get("Ads_Import_Run"), "AdsImportDialog"))
        {
            return;
        }

        if ((await PickFilesAsync("HexEditor.AdsImport", multiple: false)).FirstOrDefault() is { } source)
        {
            await ImportStreamAsync(file, row.Info.Name, source);
        }
    }

    internal async Task<bool> ImportStreamAsync(string file, string stream, string source)
    {
        if (WriteBlockMode.IsEnabled || StreamInUse(file, stream))
        {
            return false;
        }

        try
        {
            await Vm.Operations.RunAsync(Loc.Format("Ads_Operation_Import", stream.Length == 0 ? Path.GetFileName(file) : stream), OperationKind.WritesExternal, null, null,
                op => { AlternateStreams.Import(file, stream, source, op, op.CancellationToken, Volumes); return Task.CompletedTask; });
        }
        catch (StreamException ex)
        {
            ShowNotice(StreamMessage(ex), InfoBarSeverity.Error);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowNotice(Loc.Format("Ads_Error_Write", ex.Message), InfoBarSeverity.Error);
            return false;
        }

        LoadAds(file);
        return true;
    }

    // ---- タブの右クリックメニュー・ファイルを開いたときの通知 ----

    private IEnumerable<TabMenuEntry?> AdsTabMenuEntries(DocumentViewModel doc)
    {
        string? path = doc.FilePath;
        string? file = path is null ? null : AlternateStreams.SplitStreamPath(path)?.File ?? path;
        bool exists = file is not null && File.Exists(file);
        yield return null;
        yield return new TabMenuEntry("TabMenu_Ads", Loc.Get("Tab_Ads"), exists, () => _ = ShowAdsAsync(file));
    }

    /// <summary>
    /// 設定「代替データストリームがあるファイルを開いたときに知らせる」(既定オフ) がオンなら、メイン以外のストリームがあるファイルを開いたときに
    /// 「このファイルには代替データストリームが N 個あります」と「一覧を表示」を出す (ENG-37 の仕様 3)。
    /// </summary>
    private void NotifyAlternateStreams(DocumentViewModel doc)
    {
        if (!App.Settings.GetBool(AdsNoticeSettingKey, false) || doc.FilePath is not { } path || AlternateStreams.IsStreamPath(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            if (!OperatingSystem.IsWindows() || !AlternateStreams.IsSupported(path, Volumes))
            {
                return;
            }

            int count = AlternateStreams.List(path).Count(s => !s.IsDefault);
            if (count > 0)
            {
                ShowNotice(Loc.Format("Ads_Notice", count), InfoBarSeverity.Informational, doc,
                    actions: [new NotificationAction(Loc.Get("Ads_ShowList"), () => _ = ShowAdsAsync(path))]);
            }
        }
        catch (IOException)
        {
        }
    }

    public const string AdsNoticeSettingKey = "files.adsNotice";
}
