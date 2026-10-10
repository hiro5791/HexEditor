using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.Core.View;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// ミニマップ (VIEW-35) の設定とコマンド。表示状態・幅・表示内容・範囲は全ドキュメント共通の設定 (仕様 9)。
/// </summary>
public sealed partial class MainWindow
{
    private const string MinimapVisibleKey = "view.minimap.visible";
    private const string MinimapWidthKey = "view.minimap.width";
    private const string MinimapContentKey = "view.minimap.content";
    private const string MinimapRangeKey = "view.minimap.range";
    internal const string MinimapExactKey = "view.minimap.exact";
    private const string MinimapHiddenMarksKey = "view.minimap.hiddenMarks";

    private static readonly string[] MinimapMarkKinds = ["viewport", "cursor", "selection", "search", "bookmark", "modified", "difference"];

    private bool MinimapVisible => App.Settings.GetBool(MinimapVisibleKey, false);

    private MinimapContent MinimapContentSetting => App.Settings.GetString(MinimapContentKey, "entropy") switch
    {
        "byteKinds" => MinimapContent.ByteKinds,
        "byteValue" => MinimapContent.ByteValue,
        "zero" => MinimapContent.Zero,
        "byteTheme" => MinimapContent.ByteTheme,
        _ => MinimapContent.Entropy,
    };

    private MinimapRange MinimapRangeSetting => App.Settings.GetString(MinimapRangeKey, "whole") == "around" ? MinimapRange.Around : MinimapRange.Whole;

    /// <summary>
    /// コンストラクターから 1 回呼ぶ。設定画面などでミニマップの設定 (幅・正確に計算など) が変わったら、このウィンドウのビューに反映する
    /// (幅の変更のキーボードでの代わりは設定画面の「ミニマップの幅」。仕様 1・9)。
    /// </summary>
    private void InitializeMinimapSettings()
    {
        Action<IReadOnlyCollection<string>> settingsChanged = keys =>
        {
            if (keys.Any(k => k.StartsWith("view.minimap.", StringComparison.Ordinal)))
            {
                DispatcherQueue.TryEnqueue(ApplyMinimapSettings);
            }
        };
        App.Settings.Changed += settingsChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                App.Settings.Changed -= settingsChanged;
            }
        };
    }

    private void ToggleMinimap()
    {
        App.Settings.SetBool(MinimapVisibleKey, !MinimapVisible, false);
        ApplyMinimapSettings();
    }

    private void SetMinimapContent(MinimapContent content)
    {
        string value = char.ToLowerInvariant(content.ToString()[0]) + content.ToString()[1..];
        App.Settings.SetString(MinimapContentKey, value, "entropy");
        ApplyMinimapSettings();
    }

    private void SetMinimapRange(MinimapRange range)
    {
        App.Settings.SetString(MinimapRangeKey, range == MinimapRange.Around ? "around" : "whole", "whole");
        if (range == MinimapRange.Whole && MinimapContentSetting == MinimapContent.ByteTheme)
        {
            // 「バイトテーマ」は「周辺」のときだけ (仕様 3)。
            App.Settings.SetString(MinimapContentKey, "entropy", "entropy");
        }

        ApplyMinimapSettings();
    }

    /// <summary>
    /// 「正確に計算」(仕様 5): 選択中のタブのミニマップの範囲全体を読む長時間処理。処理センターに表示し、キャンセルできる。オンの間は、
    /// 新しく開いた・切り替えたドキュメントも正確に計算する (結果はドキュメントごとにキャッシュする)。
    /// </summary>
    private void ToggleMinimapExact()
    {
        bool on = !App.Settings.GetBool(MinimapExactKey, false);
        App.Settings.SetBool(MinimapExactKey, on, false);
        if (on)
        {
            StartExactMinimap();
        }

        ApplyMinimapSettings();
    }

    private void StartExactMinimap()
    {
        if (SelectedView() is { Minimap: { Visibility: Microsoft.UI.Xaml.Visibility.Visible } minimap } && Vm.Selected is { } doc)
        {
            _ = RunExactMinimapAsync(minimap, doc.Document);
        }
    }

    /// <summary>ミニマップが「正確に計算」を求めた (設定がオンで、開いた・切り替えたドキュメントの値がまだ正確でない。仕様 5)。</summary>
    private void RunExactMinimap(MinimapView minimap)
    {
        if (minimap.Editor?.Document is { IsDisposed: false } document)
        {
            _ = RunExactMinimapAsync(minimap, document);
        }
    }

    /// <summary>ミニマップの境界のドラッグで変えた幅を、全ドキュメント共通の設定に保存する (仕様 1・9)。</summary>
    private void HexView_MinimapWidthCommitted(object? sender, double width)
    {
        App.Settings.SetInt(MinimapWidthKey, (int)Math.Round(width), 80);
        ApplyMinimapSettings();
    }

    private async Task RunExactMinimapAsync(MinimapView minimap, Core.Engine.Document document)
    {
        try
        {
            await minimap.Computer.ComputeExactAsync(Vm.Operations, Loc.Get("Operation_MinimapExact"), document);
        }
        catch (OperationCanceledException)
        {
            // キャンセル: 概算の表示のまま操作できる (TC-VIEW-35-06)。
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            AppLog.Warning($"Minimap: {ex.Message}");
        }
    }

    /// <summary>Hex ビューにミニマップの設定を反映する (読み込まれたとき、設定を変えたとき)。</summary>
    private void AttachMinimap(HexView view)
    {
        view.MinimapWidth = App.Settings.GetInt(MinimapWidthKey, 80);
        view.HiddenMinimapMarks = new HashSet<string>(App.Settings.GetString(MinimapHiddenMarksKey, string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        view.MinimapMenuOpening = BuildMinimapMenu;
        view.MinimapExactRunner = RunExactMinimap;
        view.MinimapExact = App.Settings.GetBool(MinimapExactKey, false);
        view.MinimapWidthCommitted -= HexView_MinimapWidthCommitted;
        view.MinimapWidthCommitted += HexView_MinimapWidthCommitted;
        if (MinimapVisible)
        {
            view.SetMinimapMode(MinimapContentSetting, MinimapRangeSetting);
        }

        view.MinimapVisible = MinimapVisible;
    }

    private void ApplyMinimapSettings()
    {
        foreach (HexView view in _views)
        {
            AttachMinimap(view);
        }

        RefreshCommandUi();
    }

    /// <summary>ミニマップの右クリックメニュー (仕様の「画面」): 表示内容・範囲・印の ON / OFF・「正確に計算」。</summary>
    private void BuildMinimapMenu(HexView view, MenuFlyout menu)
    {
        menu.Items.Clear();
        foreach (MinimapContent content in Enum.GetValues<MinimapContent>())
        {
            MinimapContent c = content;
            var item = new RadioMenuFlyoutItem
            {
                Text = Loc.Get("Menu_View_Minimap" + c + "/Text"),
                GroupName = "MinimapMenuContent",
                IsChecked = MinimapContentSetting == c,
                IsEnabled = c != MinimapContent.ByteTheme || MinimapRangeSetting == MinimapRange.Around,
            };
            AutomationProperties.SetAutomationId(item, "MinimapMenu_" + c);
            item.Click += (_, _) => SetMinimapContent(c);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (MinimapRange range in Enum.GetValues<MinimapRange>())
        {
            MinimapRange r = range;
            var item = new RadioMenuFlyoutItem
            {
                Text = Loc.Get("Menu_View_Minimap" + r + "/Text"),
                GroupName = "MinimapMenuRange",
                IsChecked = MinimapRangeSetting == r,
            };
            AutomationProperties.SetAutomationId(item, "MinimapMenu_" + r);
            item.Click += (_, _) => SetMinimapRange(r);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var hidden = new HashSet<string>(App.Settings.GetString(MinimapHiddenMarksKey, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries));
        foreach (string kind in MinimapMarkKinds)
        {
            string k = kind;
            var item = new ToggleMenuFlyoutItem { Text = Loc.Get("Minimap_Mark_" + k), IsChecked = !hidden.Contains(k) };
            AutomationProperties.SetAutomationId(item, "MinimapMenu_Mark_" + k);
            item.Click += (_, _) =>
            {
                if (!hidden.Remove(k))
                {
                    hidden.Add(k);
                }

                App.Settings.SetString(MinimapHiddenMarksKey, string.Join(',', hidden), string.Empty);
                ApplyMinimapSettings();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var exact = new ToggleMenuFlyoutItem { Text = Loc.Get("Menu_View_MinimapExact/Text"), IsChecked = App.Settings.GetBool(MinimapExactKey, false) };
        AutomationProperties.SetAutomationId(exact, "MinimapMenu_Exact");
        exact.Click += (_, _) => ToggleMinimapExact();
        menu.Items.Add(exact);
    }
}
