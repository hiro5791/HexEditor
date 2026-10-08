using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Panels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// ハッシュパネル (ANA-18) の組み込み。パネルの枠 (UI-05) に「hash」として登録する (既定は右)。状態 (<see cref="HashPanelViewModel"/>) は
/// ウィンドウごとに 1 つで、パネルの中身 (浮動パネルとの間を移るたびに作り直す) はそれを共有する。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>ハッシュパネルのパネル ID (表示切り替えのコマンドは <c>view.panel.hash</c>)。</summary>
    public const string HashPanelId = "hash";

    private HashPanelViewModel? _hashVm;
    private HashPanel? _hashPanel;

    /// <summary>ハッシュパネルを表示しているか。</summary>
    public bool IsHashPanelOpen => IsPanelShown(HashPanelId);

    /// <summary>パネルの一覧に登録する (アプリの起動時、ウィンドウを作る前に 1 度呼ぶ)。</summary>
    public static void RegisterHashPanel()
    {
        if (PanelRegistry.Find(HashPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(HashPanelId, "Panel_Hash_Title", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateHashPanel()));
        }
    }

    private void RegisterHashCommands()
    {
        Commands.Register("analysis.hash", ShowHashPanel, NeedsDocument);

        // 「解析: ハッシュ値を照合」(ANA-21): パネルを開き、期待値の入力欄にフォーカスを移す。
        Commands.Register("analysis.hash.verify", () =>
        {
            ShowPanel(HashPanelId);
            SyncHashTarget();
            DispatcherQueue.TryEnqueue(() => _hashPanel?.FocusExpected());
        }, NeedsDocument);

        // 「解析: チェックサムファイルで検証」(ANA-21 の仕様 4): パネルを開き、チェックサムファイルを選ぶ。
        Commands.Register("analysis.hash.verifyFile", async () =>
        {
            ShowPanel(HashPanelId);
            SyncHashTarget();
            await HashVm.VerifyWithFileAsync();
        }, NeedsDocument);

        // 「解析: ハッシュ値をコピー」(ANA-22): 計算済みの結果をコピーする。
        Commands.Register("analysis.hash.copy", () => { HashVm.CopyResults(); }, () =>
            NeedsDocument() is { Enabled: false } state ? state
            : _hashVm is { Rows.Count: > 0 } ? CommandState.Available
            : CommandState.Unavailable(Loc.Get("Hash_NoResults")));
    }

    private HashPanelViewModel HashVm => _hashVm ??= new HashPanelViewModel(Vm.Operations, App.Settings)
    {
        SetClipboardText = text =>
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            SystemClipboard.SetContent(package);
        },
        PickSavePath = PickHashSavePathAsync,
        PickChecksumFile = PickChecksumFileAsync,
    };

    /// <summary>パネルの中身を作る (パネルの枠が必要なときに呼ぶ。何度呼ばれてもよい)。</summary>
    private HashPanel CreateHashPanel()
    {
        var panel = new HashPanel(HashVm);
        AutomationProperties.SetAutomationId(panel, "HashPanel");
        _hashPanel = panel;
        return panel;
    }

    /// <summary>ハッシュパネルを表示する (解析 > ハッシュ、選択範囲の右クリックメニュー「ハッシュを計算」)。計算ボタンにフォーカスを移す。</summary>
    public void ShowHashPanel()
    {
        ShowPanel(HashPanelId);
        SyncHashTarget();
        DispatcherQueue.TryEnqueue(() => _hashPanel?.FocusFirst());
    }

    private void HideHashPanel()
    {
        HidePanel(HashPanelId);
        FocusEditor();
    }

    /// <summary>
    /// パネルの対象を作業中の文書にする (パネルの配置・作業中の文書が変わるたびに呼ぶ)。閉じている間は対象範囲の変化を追わない
    /// (自動で計算しない。結果の履歴はパネルを閉じるまで。ANA-18 の仕様 9)。
    /// </summary>
    private void SyncHashTarget()
    {
        if (_hashVm is not null)
        {
            _hashVm.Target = IsHashPanelOpen ? Vm.Selected : null;
            if (!IsHashPanelOpen)
            {
                _hashVm.ClearHistory();
            }
        }
    }

    private async Task<string?> PickHashSavePathAsync(string suggestedName)
    {
        if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
        {
            return chosen;
        }

        var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggestedName, SettingsIdentifier = "HexEditor.HashSave" };
        picker.FileTypeChoices.Add(Loc.Get("FileType_All"), [Path.GetExtension(suggestedName)]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    private async Task<string?> PickChecksumFileAsync()
    {
        const string settingsIdentifier = "HexEditor.HashVerify";
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new FileOpenPicker(WindowId) { SettingsIdentifier = settingsIdentifier };
        foreach (string ext in new[] { ".md5", ".sha1", ".sha256", ".sha512", ".sfv", "*" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        return (await picker.PickSingleFileAsync())?.Path;
    }

#if HEX_TEST_HOOKS
    /// <summary>
    /// テスト用の命令 "hash": {action: show / hide / state / algorithms / setParameters / compute}。state はパネルの状態 (行、範囲、強調表示など)。
    /// </summary>
    private async Task<System.Text.Json.Nodes.JsonObject> TestHashAsync(System.Text.Json.Nodes.JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        switch (action)
        {
            case "show":
                ShowHashPanel();
                _hashPanel?.UpdateLayout();
                break;
            case "hide":
                HideHashPanel();
                break;
            case "algorithms":
                // {ids: [...]} の組み合わせだけを選ぶ。
                string[] ids = [.. request["ids"]!.AsArray().Select(n => n!.GetValue<string>())];
                foreach (HashAlgorithmItem item in HashVm.Algorithms)
                {
                    item.IsChecked = ids.Contains(item.Id);
                }

                break;
            case "setParameters":
                HashVm.SetParameters(request["id"]!.GetValue<string>(), HashVm.ParametersOf(request["id"]!.GetValue<string>()) with
                {
                    Seed = (ulong)TestHookSettings.ReadLong(request["seed"], 0),
                });
                break;
            case "compute":
                await HashVm.ComputeAsync();
                break;
            case "custom":
                // {start, end?, length?}: 範囲を指定 (06 の 0.1)。end を渡すと「終了 (このバイトを含む)」として指定する。
                HashVm.ChooseTarget(ViewModels.HashTargetKind.Custom);
                HashVm.CustomUsesEnd = request["end"] is not null;
                HashVm.CustomStart = request["start"]?.GetValue<string>() ?? "0";
                HashVm.CustomLength = (request["end"] ?? request["length"])?.GetValue<string>() ?? string.Empty;
                break;
            case "expected":
                HashVm.ExpectedText = request["text"]?.GetValue<string>() ?? string.Empty;
                break;
        }

        var state = new System.Text.Json.Nodes.JsonObject
        {
            ["open"] = IsHashPanelOpen,
            ["location"] = PanelLocation(HashPanelId),
        };
        if (_hashVm is { } vm)
        {
            state["range"] = vm.RangeText;
            state["computing"] = vm.IsComputing;
            state["highlighted"] = vm.ComputeHighlighted;
            state["stale"] = vm.IsStale;
            state["status"] = vm.StatusText;
            state["rows"] = new System.Text.Json.Nodes.JsonArray([.. vm.Rows.Select(r => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = r.Id,
                ["name"] = r.Name,
                ["value"] = r.Value,
                ["match"] = r.Match?.ToString(),
            })]);
            state["verifyMessage"] = vm.VerifyMessage;
            state["expected"] = vm.ExpectedText;
            state["checked"] = new System.Text.Json.Nodes.JsonArray([.. vm.Algorithms.Where(a => a.IsChecked).Select(a => (System.Text.Json.Nodes.JsonNode?)a.Id)]);
        }

        return state;
    }
#endif
}
