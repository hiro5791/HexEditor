using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// ハッシュパネル (ANA-18) の組み込み。パネルの置き場所 (パネルの枠) ができるまでは、文書の領域の右に仮に置く。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>仮のパネルの幅 (epx)。</summary>
    private const double HashPanelWidth = 380;

    private HashPanelViewModel? _hashVm;
    private HashPanel? _hashPanel;

    /// <summary>ハッシュパネルを表示しているか。</summary>
    public bool IsHashPanelOpen => _hashPanel?.Visibility == Visibility.Visible;

    /// <summary>ハッシュパネルの組み込み (コンストラクタから呼ぶ)。</summary>
    private void InitializeHash()
    {
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected) && _hashVm is not null && IsHashPanelOpen)
            {
                _hashVm.Target = Vm.Selected;
            }
        };
    }

    /// <summary>解析 > ハッシュ。</summary>
    private void Hash_Click(object sender, RoutedEventArgs e) => ShowHashPanel();

    /// <summary>ハッシュパネルを表示する (解析 > ハッシュ、選択範囲の右クリックメニュー「ハッシュを計算」)。</summary>
    public void ShowHashPanel()
    {
        if (_hashPanel is null)
        {
            _hashVm = new HashPanelViewModel(Vm.Operations, App.Settings)
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
            _hashPanel = new HashPanel(_hashVm)
            {
                Width = HashPanelWidth,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            AutomationProperties.SetAutomationId(_hashPanel, "HashPanel");
            AutomationProperties.SetLandmarkType(_hashPanel, Microsoft.UI.Xaml.Automation.Peers.AutomationLandmarkType.Custom);
            _hashPanel.Closed += (_, _) => HideHashPanel();
            Grid.SetRow(_hashPanel, 4);
            Root.Children.Add(_hashPanel);
        }

        _hashVm!.Target = Vm.Selected;
        _hashPanel.Visibility = Visibility.Visible;
        Tabs.Margin = new Thickness(0, 0, HashPanelWidth, 0);
        _hashPanel.FocusFirst();
    }

    private void HideHashPanel()
    {
        if (_hashPanel is null)
        {
            return;
        }

        _hashPanel.Visibility = Visibility.Collapsed;

        // 閉じている間は対象範囲の変化を追わない (自動で計算しない)。
        _hashVm!.Target = null;
        Tabs.Margin = new Thickness(0);
        CurrentView()?.Focus(FocusState.Programmatic);
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
    /// テスト用の命令 "hash": {action: show / state / setParameters / expected / select}。state はパネルの状態 (行、範囲、強調表示など)。
    /// </summary>
    private async Task<System.Text.Json.Nodes.JsonObject> TestHashAsync(System.Text.Json.Nodes.JsonObject request)
    {
        string action = request["action"]?.GetValue<string>() ?? "state";
        switch (action)
        {
            case "show":
                ShowHashPanel();
                _hashPanel!.UpdateLayout();
                break;
            case "hide":
                HideHashPanel();
                break;
            case "algorithms":
                // {ids: [...]} の組み合わせだけを選ぶ。
                string[] ids = [.. request["ids"]!.AsArray().Select(n => n!.GetValue<string>())];
                foreach (HashAlgorithmItem item in _hashVm!.Algorithms)
                {
                    item.IsChecked = ids.Contains(item.Id);
                }

                break;
            case "setParameters":
                _hashVm!.SetParameters(request["id"]!.GetValue<string>(), _hashVm.ParametersOf(request["id"]!.GetValue<string>()) with
                {
                    Seed = (ulong)TestHookSettings.ReadLong(request["seed"], 0),
                });
                break;
            case "compute":
                await _hashVm!.ComputeAsync();
                break;
        }

        var state = new System.Text.Json.Nodes.JsonObject
        {
            ["open"] = IsHashPanelOpen,
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
            state["checked"] = new System.Text.Json.Nodes.JsonArray([.. vm.Algorithms.Where(a => a.IsChecked).Select(a => (System.Text.Json.Nodes.JsonNode?)a.Id)]);
        }

        return state;
    }
#endif
}
