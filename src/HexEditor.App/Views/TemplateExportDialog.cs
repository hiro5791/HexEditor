using HexEditor.App.Services;
using HexEditor.Core.Templates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>エクスポートの設定 (TPL-25 の「画面」)。</summary>
public sealed record TemplateExportRequest(TemplateExportFormat Format, bool Subtree, TemplateExportOptions Options);

/// <summary>エクスポートの設定ダイアログ (形式、対象、オプション)。保存先は「保存」の後のファイルの選択で決める。</summary>
public sealed class TemplateExportDialog
{
    private readonly ContentDialog _dialog;
    private readonly RadioButtons _format;
    private readonly RadioButtons _target;
    private readonly NumberBox _maxElements;
    private readonly NumberBox _maxDepth;
    private readonly CheckBox _hidden;
    private readonly CheckBox _hex;

    public TemplateExportDialog(XamlRoot root, bool canExportSubtree, string? subtreeName)
    {
        _format = new RadioButtons { Header = Loc.Get("TplExport_Format"), MaxColumns = 2, Items = { "JSON", "CSV" }, SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(_format, "TplExport_Format");
        _target = new RadioButtons { Header = Loc.Get("TplExport_Target") };
        _target.Items.Add(Loc.Get("TplExport_TargetAll"));
        if (canExportSubtree)
        {
            _target.Items.Add(Loc.Format("TplExport_TargetSubtree", subtreeName ?? string.Empty));
        }

        _target.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_target, "TplExport_Target");
        _maxElements = new NumberBox { Header = Loc.Get("TplExport_MaxElements"), PlaceholderText = Loc.Get("TplExport_Unlimited"), Minimum = 0, Value = double.NaN };
        AutomationProperties.SetAutomationId(_maxElements, "TplExport_MaxElements");
        _maxDepth = new NumberBox { Header = Loc.Get("TplExport_MaxDepth"), PlaceholderText = Loc.Get("TplExport_Unlimited"), Minimum = 0, Value = double.NaN };
        AutomationProperties.SetAutomationId(_maxDepth, "TplExport_MaxDepth");
        _hidden = new CheckBox { Content = Loc.Get("TplExport_IncludeHidden") };
        AutomationProperties.SetAutomationId(_hidden, "TplExport_IncludeHidden");
        _hex = new CheckBox { Content = Loc.Get("TplExport_HexOffsets") };
        AutomationProperties.SetAutomationId(_hex, "TplExport_HexOffsets");
        var content = new StackPanel { Spacing = 8, MinWidth = 360, Children = { _format, _target, _maxElements, _maxDepth, _hidden, _hex } };
        _dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get("TplExport_Title"),
            Content = new ScrollViewer { Content = content },
            PrimaryButtonText = Loc.Get("TplExport_Save"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(_dialog, "TemplateExportDialog");
    }

    public async Task<TemplateExportRequest?> ShowAsync()
    {
        if (await _dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        var options = new TemplateExportOptions
        {
            MaxArrayElements = double.IsNaN(_maxElements.Value) ? null : (long)_maxElements.Value,
            MaxDepth = double.IsNaN(_maxDepth.Value) ? null : (int)_maxDepth.Value,
            IncludeHidden = _hidden.IsChecked == true,
            HexOffsets = _hex.IsChecked == true,
            Display = TemplateDisplayContext.Current,
        };
        return new TemplateExportRequest(_format.SelectedIndex == 1 ? TemplateExportFormat.Csv : TemplateExportFormat.Json, _target.SelectedIndex == 1, options);
    }
}
