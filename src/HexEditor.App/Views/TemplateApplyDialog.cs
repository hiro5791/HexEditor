using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Expressions;
using HexEditor.Core.Templates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Views;

/// <summary>テンプレートの選択画面の 1 項目 (TPL-20 の仕様 1)。</summary>
public sealed class TemplatePickItem
{
    public TemplatePickItem(TemplateCatalogEntry entry, bool recent)
    {
        Entry = entry;
        Recent = recent;
        CultureInfo ui = CultureInfo.CurrentUICulture;
        Name = entry.DisplayName(ui);
        Description = entry.Metadata.GetDescription(ui) ?? string.Empty;
        string origin = Views.TemplateSettingsSection.OriginText(entry);
        string kaitai = entry.Metadata.IsKaitai ? " · Kaitai" : string.Empty;
        string script = entry.Metadata.UsesExtensions ? " · " + Loc.Get("Tpl_UsesScript") : string.Empty;
        Detail = (recent ? Loc.Get("Tpl_Recent") + " · " : string.Empty) + origin + kaitai + script
            + (entry.Metadata.Extensions.Count > 0 ? " · " + string.Join(", ", entry.Metadata.Extensions) : string.Empty);
    }

    public TemplateCatalogEntry Entry { get; }

    public bool Recent { get; }

    public string Name { get; }

    public string Description { get; }

    public string Detail { get; }

    public bool Matches(string filter) => filter.Length == 0
        || Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Entry.Metadata.Extensions.Any(x => x.Contains(filter, StringComparison.OrdinalIgnoreCase))
        || Entry.Metadata.Mimes.Any(x => x.Contains(filter, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}

/// <summary>
/// テンプレートの選択画面 (TPL-20 の「画面」): 検索欄、一覧 (最近使ったものを上に)、詳細、開始オフセット、エンディアン、種類
/// (固定・追従。追従は型を選ぶ。TPL-26 の仕様 3)、「適用」「キャンセル」。
/// </summary>
public sealed class TemplateApplyDialog
{
    private readonly ContentDialog _dialog;
    private readonly TextBox _search;
    private readonly ListView _list;
    private readonly TextBlock _details;
    private readonly TextBox _offset;
    private readonly TextBlock _offsetResult;
    private readonly ComboBox _endian;
    private readonly RadioButtons _kind;
    private readonly ComboBox _type;
    private readonly List<TemplatePickItem> _all;

    public TemplateApplyDialog(XamlRoot root, long startOffset, long documentLength, bool documentBigEndian, TemplateApplyKind kind = TemplateApplyKind.Fixed,
        bool kaitaiOnly = false)
    {
        DocumentLength = documentLength;
        IReadOnlyList<string> recent = TemplateService.Recent;
        IReadOnlyList<TemplateCatalogEntry> entries = TemplateService.Catalog.Entries;
        _all = [];
        foreach (string path in recent)
        {
            if (entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) is { } e)
            {
                _all.Add(new TemplatePickItem(e, recent: true));
            }
        }

        foreach (TemplateCatalogEntry e in entries.OrderBy(e => e.Origin).ThenBy(e => e.DisplayName(CultureInfo.CurrentUICulture), StringComparer.CurrentCultureIgnoreCase))
        {
            if (_all.All(i => !string.Equals(i.Entry.Path, e.Path, StringComparison.OrdinalIgnoreCase)) && (!kaitaiOnly || e.Metadata.IsKaitai))
            {
                _all.Add(new TemplatePickItem(e, recent: false));
            }
        }

        _search = new TextBox { PlaceholderText = Loc.Get("TplApply_SearchPlaceholder") };
        AutomationProperties.SetAutomationId(_search, "TplApply_Search");
        AutomationProperties.SetName(_search, Loc.Get("TplApply_Search"));
        _list = new ListView { Height = 220, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(_list, "TplApply_List");
        AutomationProperties.SetName(_list, Loc.Get("TplApply_List"));
        _details = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        AutomationProperties.SetAutomationId(_details, "TplApply_Details");
        _offset = new TextBox
        {
            Header = Loc.Get("TplApply_Offset"),
            Text = "0x" + startOffset.ToString("X", CultureInfo.InvariantCulture),
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            FlowDirection = FlowDirection.LeftToRight,
        };
        AutomationProperties.SetAutomationId(_offset, "TplApply_Offset");
        _offsetResult = new TextBlock { Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
        AutomationProperties.SetAutomationId(_offsetResult, "TplApply_OffsetResult");
        _endian = new ComboBox { Header = Loc.Get("TplApply_Endian"), MinWidth = 160 };
        _endian.Items.Add(new ComboBoxItem { Content = Loc.Get(documentBigEndian ? "TplApply_EndianDocumentBig" : "TplApply_EndianDocumentLittle"), Tag = "document" });
        _endian.Items.Add(new ComboBoxItem { Content = Loc.Get("TplApply_EndianLittle"), Tag = "little" });
        _endian.Items.Add(new ComboBoxItem { Content = Loc.Get("TplApply_EndianBig"), Tag = "big" });
        _endian.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_endian, "TplApply_Endian");
        _kind = new RadioButtons { Header = Loc.Get("TplApply_Kind"), MaxColumns = 2 };
        _kind.Items.Add(Loc.Get("TplApply_KindFixed"));
        _kind.Items.Add(Loc.Get("TplApply_KindFollow"));
        _kind.SelectedIndex = kind == TemplateApplyKind.Follow ? 1 : 0;
        AutomationProperties.SetAutomationId(_kind, "TplApply_Kind");
        _type = new ComboBox { Header = Loc.Get("TplApply_Type"), MinWidth = 220, IsEnabled = kind == TemplateApplyKind.Follow };
        AutomationProperties.SetAutomationId(_type, "TplApply_Type");

        var options = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        options.Children.Add(_offset);
        Grid.SetRow(_offsetResult, 1);
        options.Children.Add(_offsetResult);
        Grid.SetColumn(_endian, 1);
        options.Children.Add(_endian);
        Grid.SetRow(_kind, 2);
        options.Children.Add(_kind);
        Grid.SetRow(_type, 2);
        Grid.SetColumn(_type, 1);
        options.Children.Add(_type);

        var content = new StackPanel { Spacing = 8, MinWidth = 520 };
        content.Children.Add(_search);
        content.Children.Add(_list);
        content.Children.Add(_details);
        content.Children.Add(options);
        _dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Loc.Get(kaitaiOnly ? "TplApply_TitleKaitai" : "TplApply_Title"),
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = Loc.Get("TplApply_Apply"),
            CloseButtonText = Loc.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(_dialog, "TemplateApplyDialog");

        _search.TextChanged += (_, _) => Filter();
        _list.SelectionChanged += (_, _) => SelectionChanged();
        _offset.TextChanged += (_, _) => Validate();
        _kind.SelectionChanged += (_, _) =>
        {
            _type.IsEnabled = _kind.SelectedIndex == 1;
            Validate();
        };
        _type.SelectionChanged += (_, _) => Validate();
        _dialog.PrimaryButtonClick += (_, e) =>
        {
            if (Build() is not { } info)
            {
                e.Cancel = true;
                return;
            }

            Result = info;
        };
        Filter();
    }

    /// <summary>選んだ内容 (「適用」を押したとき)。</summary>
    public TemplateApplicationInfo? Result { get; private set; }

    public long DocumentLength { get; }

    /// <summary>選択画面の一覧 (テスト用)。</summary>
    internal IReadOnlyList<TemplatePickItem> Items => [.. _list.Items.OfType<TemplatePickItem>()];

    private void Filter()
    {
        string f = _search.Text.Trim();
        object? keep = _list.SelectedItem;
        _list.Items.Clear();
        foreach (TemplatePickItem item in _all.Where(i => i.Matches(f)))
        {
            _list.Items.Add(item);
        }

        _list.SelectedItem = keep is TemplatePickItem k && _list.Items.Contains(k) ? k : _list.Items.FirstOrDefault();
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        if (_list.SelectedItem is not TemplatePickItem item)
        {
            _details.Text = string.Empty;
            _type.Items.Clear();
            Validate();
            return;
        }

        TemplateMetadata m = item.Entry.Metadata;
        _details.Text = string.Join("\n", new[]
        {
            item.Description,
            item.Detail,
            m.Version is { } v ? Loc.Format("TplApply_Version", v) : null,
            m.License is { } l ? Loc.Format("TplApply_License", l) : null,
        }.Where(s => !string.IsNullOrEmpty(s)));

        // 追従で評価する型 (テンプレートで定義した型。TPL-26 の仕様 3)
        string? keep = _type.SelectedItem as string;
        _type.Items.Clear();
        try
        {
            foreach (string t in TemplateService.Load(item.Entry.Path).TypeNames.Order(StringComparer.Ordinal))
            {
                _type.Items.Add(t);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Template types: {ex.GetType().Name}");
        }

        _type.SelectedItem = keep is not null && _type.Items.Contains(keep) ? keep : _type.Items.FirstOrDefault();
        Validate();
    }

    private bool TryOffset(out long offset)
    {
        offset = 0;
        return ExpressionEvaluator.TryEvaluate(_offset.Text, new NoContext(DocumentLength), out offset, out _, DefaultRadix.Hexadecimal)
            && offset >= 0 && offset <= DocumentLength;
    }

    private void Validate()
    {
        bool offsetOk = TryOffset(out long offset);
        _offsetResult.Text = offsetOk ? "= " + offset.ToString("N0", CultureInfo.CurrentCulture) + " (0x" + offset.ToString("X", CultureInfo.InvariantCulture) + ")"
            : Loc.Get("TplApply_BadOffset");
        _dialog.IsPrimaryButtonEnabled = offsetOk && _list.SelectedItem is TemplatePickItem && (_kind.SelectedIndex == 0 || _type.SelectedItem is string);
    }

    private TemplateApplicationInfo? Build()
    {
        if (_list.SelectedItem is not TemplatePickItem item || !TryOffset(out long offset))
        {
            return null;
        }

        bool follow = _kind.SelectedIndex == 1;
        return new TemplateApplicationInfo
        {
            TemplatePath = item.Entry.Path,
            StartOffset = offset,
            BigEndian = (_endian.SelectedItem as ComboBoxItem)?.Tag switch
            {
                "little" => false,
                "big" => true,
                _ => null,
            },
            Kind = follow ? TemplateApplyKind.Follow : TemplateApplyKind.Fixed,
            TypeName = follow ? _type.SelectedItem as string : null,
        };
    }

    public async Task<TemplateApplicationInfo?> ShowAsync()
    {
        await _dialog.ShowQueuedAsync();
        return Result;
    }

    /// <summary>入力式の文脈 (ドキュメントの長さだけ)。</summary>
    private sealed class NoContext(long length) : IExpressionContext
    {
        public long Cursor => 0;

        public long Length => length;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }
}
