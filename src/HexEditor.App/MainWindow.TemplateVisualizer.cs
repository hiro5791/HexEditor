using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Panels;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Presentation;
using HexEditor.Core.Templates.Visualization;
using HexEditor.Core.View;
using HexEditor.Platform.Imaging;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// テンプレートのビジュアライザ (07 の TPL-16) のつなぎ込み: パネルの登録、結果ツリーで <c>visualize</c> 属性を持つノードを選んだときの表示
/// (仕様 2)、ノードの「表示」ボタンと右クリックメニュー、保存 (画像は PNG、音声は WAV、表は CSV。仕様 5)。
/// </summary>
public sealed partial class MainWindow
{
    public const string TemplateVisualizerPanelId = "templateVisualizer";

    private TemplateVisualizerViewModel? _templateVisVm;

    private TemplateVisualizerViewModel TemplateVisVm => _templateVisVm ??= new TemplateVisualizerViewModel();

    private TemplateVisualizerPanel? TemplateVisualizerView => ShownPanelContent(TemplateVisualizerPanelId) as TemplateVisualizerPanel;

    /// <summary>パネルの一覧に登録する (<see cref="RegisterTemplatePanels"/> から呼ぶ)。</summary>
    private static void RegisterTemplateVisualizerPanel()
    {
        if (PanelRegistry.Find(TemplateVisualizerPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(TemplateVisualizerPanelId, "Panel_TemplateVisualizer_Title", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateTemplateVisualizerPanel()));
        }
    }

    private TemplateVisualizerPanel CreateTemplateVisualizerPanel()
    {
        var panel = new TemplateVisualizerPanel(TemplateVisVm);
        panel.SaveRequested += (_, _) => _ = SaveTemplateVisualAsync(null);

        // パネルを開いたら、選んでいるノードを表示する。
        if (TemplateVisVm.Node is null && SelectedVisualizerNode() is { } node)
        {
            _ = TemplateVisVm.ShowAsync(node);
        }

        return panel;
    }

    /// <summary>コマンドを登録する (<see cref="RegisterTemplateCommands"/> から呼ぶ)。</summary>
    private void RegisterTemplateVisualizerCommands()
    {
        Commands.Register("templates.visualize", () => ShowTemplateVisualizer(SelectedVisualizerNode()),
            () => NeedsDocument(_ => SelectedVisualizerNode() is null ? Loc.Get("TplVis_NoVisualizer") : null));
        RegisterOnlineTemplateCommands();
        RegisterAutomationFeatures();
    }

    /// <summary>結果ツリーの選択と「表示」ボタンをつなぐ (<see cref="CreateTemplateVm"/> から呼ぶ)。</summary>
    private void HookTemplateVisualizer(TemplatePanelViewModel templateVm)
    {
        templateVm.Rows.VisualizerRequested += (_, item) =>
        {
            templateVm.SelectedItem = item;
            ShowTemplateVisualizer(item.Node);
        };
        templateVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TemplatePanelViewModel.SelectedItem) && IsPanelShown(TemplateVisualizerPanelId)
                && SelectedVisualizerNode() is { } node && !ReferenceEquals(node, TemplateVisVm.Node))
            {
                // 結果ツリーで visualize 属性を持つノードを選ぶと表示する (仕様 2)。
                _ = TemplateVisVm.ShowAsync(node);
            }
        };
    }

    /// <summary>結果ツリーで選んでいる、ビジュアライザを持つノード。</summary>
    private TemplateNode? SelectedVisualizerNode() =>
        _templateVm?.SelectedItem is { Kind: TemplateTreeItemKind.Node, Node: { } node } && TemplateVisualizer.HasVisualizer(node) ? node : null;

    /// <summary>ビジュアライザのパネルを開いてノードを表示する。</summary>
    internal void ShowTemplateVisualizer(TemplateNode? node)
    {
        if (node is null)
        {
            return;
        }

        ShowPanel(TemplateVisualizerPanelId);
        _ = TemplateVisVm.ShowAsync(node);
    }

    /// <summary>右クリックメニューの「ビジュアライザで表示」(<see cref="FillTemplateMenu"/> から呼ぶ)。</summary>
    private void AddTemplateVisualizerMenuItem(MenuFlyout menu, TemplateTreeItem item)
    {
        if (item is not { Kind: TemplateTreeItemKind.Node, Node: { } node } || !TemplateVisualizer.HasVisualizer(node))
        {
            return;
        }

        var mi = new MenuFlyoutItem { Text = Loc.Get("TplMenu_Visualize"), Icon = new FontIcon { Glyph = "" } };
        AutomationProperties.SetAutomationId(mi, "TplMenu_Visualize");
        mi.Click += (_, _) => ShowTemplateVisualizer(node);
        menu.Items.Add(mi);
    }

    // ───────────── 保存 (仕様 5) ─────────────

    /// <summary>表示中の内容を保存する。<paramref name="path"/> が null なら保存先を選ぶ。</summary>
    internal async Task<bool> SaveTemplateVisualAsync(string? path)
    {
        TemplateVisualizerViewModel vm = TemplateVisVm;
        (string extension, string filterKey)? kind = vm.Visual switch
        {
            BitmapVisual or EncodedImageVisual when vm.CanSave => (".png", "Visualizer_PngFiles"),
            AudioVisual => (".wav", "TplVis_WavFiles"),
            TableVisual => (".csv", "TplVis_CsvFiles"),
            _ => null,
        };
        if (kind is not { } k)
        {
            ShowNotice(Loc.Get("Visualizer_NothingToSave"), InfoBarSeverity.Informational, Vm.Selected);
            return false;
        }

        string suggested = SafeVisualizerFileName(vm.Node?.Name ?? "visualizer") + k.extension;
        if (path is null)
        {
            if (TestHooks.TrySavePicker(suggested, out string? chosen))
            {
                path = chosen;
            }
            else
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.TemplateVisualizer" };
                picker.FileTypeChoices.Add(Loc.Get(k.filterKey), [k.extension]);
                path = (await picker.PickSaveFileAsync())?.Path;
            }
        }

        if (path is null)
        {
            return false;
        }

        TemplateVisual visual = vm.Visual!;
        DecodedImage? decoded = vm.Decoded;
        try
        {
            await Task.Run(() =>
            {
                switch (visual)
                {
                    case BitmapVisual b:
                        using (FileStream s = File.Create(path))
                        {
                            PngEncoder.Write(s, b.Width, b.Height, b.Pixels);
                        }

                        break;
                    case EncodedImageVisual when decoded is not null:
                        using (FileStream s = File.Create(path))
                        {
                            PngEncoder.Write(s, decoded.Width, decoded.Height, ImageCodec.ToArgb(decoded));
                        }

                        break;
                    case AudioVisual a:
                        File.WriteAllBytes(path, a.ToWav());
                        break;
                    case TableVisual t:
                        using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true)))
                        {
                            t.WriteCsv(w);
                        }

                        break;
                }
            });
            ShowNotice(Loc.Format("Visualizer_Saved", Path.GetFileName(path)), InfoBarSeverity.Success, Vm.Selected);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Visualizer_SaveError", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error, Vm.Selected);
            return false;
        }
    }

    private static string SafeVisualizerFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. name.Select(c => invalid.Contains(c) || c is '[' or ']' ? '_' : c)]);
        return safe.Length == 0 ? "visualizer" : safe;
    }
}
