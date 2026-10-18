using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Inspector;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// 行の右クリックメニューの「ビジュアライザで開く」(float / double などの行。TOOL-25) と「カラーピッカーで開く」(色の型の行。TOOL-27)。
/// 開く処理は <see cref="OpenInTool"/> (ウィンドウが渡す)。
/// </summary>
public sealed partial class InspectorPanel
{
    private MenuFlyoutItem _openFloatMenu = null!;
    private MenuFlyoutItem _openColorMenu = null!;

    /// <summary>行を電卓・変換・参照ツールで開く(ウィンドウが渡す)。</summary>
    internal Action<InspectorItemViewModel>? OpenInTool { get; set; }

    private static bool IsFloatRow(string typeId) =>
        typeId is InspectorTypes.Float32 or InspectorTypes.Float64 or InspectorTypes.Half or InspectorTypes.BFloat16 or InspectorTypes.Float80;

    /// <summary>コンストラクターから呼ぶ。</summary>
    private void InitializeToolMenus()
    {
        _openFloatMenu = new MenuFlyoutItem { Text = Loc.Get("Inspector_MenuOpenFloat") };
        AutomationProperties.SetAutomationId(_openFloatMenu, "Inspector_MenuOpenFloat");
        _openColorMenu = new MenuFlyoutItem { Text = Loc.Get("Inspector_MenuOpenColor") };
        AutomationProperties.SetAutomationId(_openColorMenu, "Inspector_MenuOpenColor");
        _openFloatMenu.Click += (_, _) => OpenMenuTarget();
        _openColorMenu.Click += (_, _) => OpenMenuTarget();
        RowMenu.Items.Add(_openFloatMenu);
        RowMenu.Items.Add(_openColorMenu);
        RowMenu.Opening += (_, _) =>
        {
            InspectorItemViewModel? target = _menuTarget ?? Vm.Selected;
            bool isFloat = target is { Kind: InspectorItemKind.Row } && IsFloatRow(target.TypeId);
            bool isColor = target is { Kind: InspectorItemKind.Row } && InspectorTypes.IsColor(target.TypeId);
            _openFloatMenu.Visibility = isFloat ? Visibility.Visible : Visibility.Collapsed;
            _openColorMenu.Visibility = isColor ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    private void OpenMenuTarget()
    {
        if ((_menuTarget ?? Vm.Selected) is { } item)
        {
            OpenInTool?.Invoke(item);
        }
    }

    /// <summary>テスト用: 右クリックメニューの項目を押したのと同じ処理。</summary>
    internal void TestOpenInTool(InspectorItemViewModel item) => OpenInTool?.Invoke(item);
}
