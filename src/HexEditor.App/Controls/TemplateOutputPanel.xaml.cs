using System.Collections.ObjectModel;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Syntax;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>出力パネルの 1 行 (TPL-27 の仕様 4)。</summary>
public sealed class TemplateOutputRow
{
    public TemplateOutputRow(TemplateOutputEntry entry, string templateName)
    {
        Entry = entry;
        TemplateName = templateName;
    }

    public TemplateOutputEntry Entry { get; }

    public string TemplateName { get; }

    public TemplateDiagnostic? Diagnostic => Entry.Diagnostic;

    public string SeverityText => TemplateDiagnosticText.Severity(Entry.Severity);

    public string Glyph => Entry.Severity switch
    {
        TemplateSeverity.Error => "",
        TemplateSeverity.Warning => "",
        _ => "",
    };

    public Brush IconBrush => (Brush)Application.Current.Resources[Entry.Severity switch
    {
        TemplateSeverity.Error => "SystemFillColorCriticalBrush",
        TemplateSeverity.Warning => "SystemFillColorCautionBrush",
        _ => "TextFillColorSecondaryBrush",
    }];

    public string Code => Diagnostic?.Code ?? string.Empty;

    public Visibility CodeVisibility => Code.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>行の文字 (理由、テンプレートの位置、データの位置、パス)。</summary>
    public string Text
    {
        get
        {
            if (Diagnostic is not { } d)
            {
                return Entry.Text;
            }

            string text = TemplateDiagnosticText.Message(d);
            if (d.Location is { } at)
            {
                text += $" ({Path.GetFileName(at.File)}:{at.Line}:{at.Column})";
            }

            if (d.DataOffset is { } offset)
            {
                text += " @ 0x" + offset.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (d.Path is { Length: > 0 } path)
            {
                text += " [" + path + "]";
            }

            return text;
        }
    }

    /// <summary>読み上げ・テスト用の文字 (種類、コード、文字)。</summary>
    public override string ToString() => SeverityText + (Code.Length > 0 ? " " + Code : string.Empty) + ": " + Text;
}

/// <summary>出力パネルが頼む操作 (ウィンドウが実装する)。</summary>
public interface ITemplateOutputHost
{
    /// <summary>テンプレートの位置を開く (「ソースへ」)。</summary>
    Task OpenTemplateSourceAsync(TemplateSourceLocation location);

    /// <summary>データの位置へ移動する (「データへ」)。</summary>
    void GoToTemplateDataOffset(long offset, string? sectionName);

    /// <summary>エラーのコードのヘルプを開く (TPL-27 の仕様 7)。</summary>
    Task OpenTemplateHelpAsync(string code);
}

/// <summary>テンプレートの出力パネル (TPL-27 の仕様 4)。作業中のドキュメントのすべての適用の出力を時刻順に並べる。</summary>
public sealed partial class TemplateOutputPanel : UserControl
{
    private readonly ObservableCollection<TemplateOutputRow> _rows = [];
    private readonly HashSet<int> _cleared = [];

    public TemplateOutputPanel(ITemplateOutputHost host)
    {
        Host = host;
        InitializeComponent();
        List.ItemsSource = _rows;
    }

    public ITemplateOutputHost Host { get; }

    internal IReadOnlyList<TemplateOutputRow> Rows => _rows;

    internal ListView ListView => List;

    /// <summary>表示し直す (適用の結果が変わった)。</summary>
    internal void Refresh(DocumentTemplates? templates)
    {
        var rows = new List<TemplateOutputRow>();
        foreach (TemplateApplicationEntry entry in templates?.Entries ?? [])
        {
            if (entry.Result is not { } result || _cleared.Contains(result.GetHashCode()))
            {
                continue;
            }

            rows.AddRange(result.Output.Select(o => new TemplateOutputRow(o, entry.DisplayName)));
        }

        rows.Sort((a, b) => a.Entry.TimeUtc.CompareTo(b.Entry.TimeUtc));
        _rows.Clear();
        foreach (TemplateOutputRow r in rows)
        {
            _rows.Add(r);
        }

        int errors = rows.Count(r => r.Entry.Severity == TemplateSeverity.Error);
        int warnings = rows.Count(r => r.Entry.Severity == TemplateSeverity.Warning);
        Summary.Text = Loc.Format("Tpl_Counts", errors, warnings);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _rows.Clear();

    private async void Code_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TemplateOutputRow row && row.Code.Length > 0)
        {
            await Host.OpenTemplateHelpAsync(row.Code);
        }
    }

    private void List_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is TemplateOutputRow row)
        {
            e.Handled = true;
            ShowRowMenu(row, e.OriginalSource as FrameworkElement ?? List);
        }
    }

    private void List_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && List.SelectedItem is TemplateOutputRow row)
        {
            e.Handled = true;
            ShowRowMenu(row, List.ContainerFromItem(row) as FrameworkElement ?? List);
        }
    }

    /// <summary>「ソースへ」「データへ」を選ぶメニュー (TPL-27 の仕様 4)。</summary>
    internal MenuFlyout RowMenu(TemplateOutputRow row)
    {
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "TplOut_RowMenu");
        var source = new MenuFlyoutItem { Text = Loc.Get("TplOut_ToSource"), IsEnabled = row.Diagnostic?.Location is not null };
        AutomationProperties.SetAutomationId(source, "TplOut_ToSource");
        source.Click += async (_, _) =>
        {
            if (row.Diagnostic?.Location is { } at)
            {
                await Host.OpenTemplateSourceAsync(at);
            }
        };
        var data = new MenuFlyoutItem { Text = Loc.Get("TplOut_ToData"), IsEnabled = row.Diagnostic?.DataOffset is not null };
        AutomationProperties.SetAutomationId(data, "TplOut_ToData");
        data.Click += (_, _) =>
        {
            if (row.Diagnostic?.DataOffset is { } offset)
            {
                Host.GoToTemplateDataOffset(offset, row.Diagnostic.SectionName);
            }
        };
        menu.Items.Add(source);
        menu.Items.Add(data);
        if (row.Code.Length > 0)
        {
            var help = new MenuFlyoutItem { Text = Loc.Format("TplOut_Help", row.Code) };
            AutomationProperties.SetAutomationId(help, "TplOut_HelpItem");
            help.Click += async (_, _) => await Host.OpenTemplateHelpAsync(row.Code);
            menu.Items.Add(help);
        }

        return menu;
    }

    private void ShowRowMenu(TemplateOutputRow row, FrameworkElement anchor) => RowMenu(row).ShowAt(anchor);
}
