using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Tools;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls.Tools;

/// <summary>
/// 文字コード表パネル (TOOL-26 の画面)。16 × 16 の表で、各セルにバイト値と文字を示す。複数バイトの文字コードは先行バイトの表と 2 バイト目の表。
/// 表のセルは矢印キーで移動でき、スクリーンリーダーでは「0x41、A、LATIN CAPITAL LETTER A」のように読み上げる。
/// </summary>
internal sealed partial class CharTablePanel : ToolPanelBase
{
    private const string EncodingKey = "chartable.encoding";
    private const string Utf16Key = "chartable.utf16be";
    private const string ListKey = "chartable.list";
    private const string BaseKey = "chartable.base";

    /// <summary>16 × 16 のセルの表(見出し付き)。</summary>
    private sealed class CellGrid
    {
        public CellGrid(string idPrefix, Func<int, CharacterCell> cellOf, Action<int> select, Action<int> activate, Func<int, string> header)
        {
            IdPrefix = idPrefix;
            CellOf = cellOf;
            Header = header;
            Root = new Grid { ColumnSpacing = 1, RowSpacing = 1 };
            for (int i = 0; i < 17; i++)
            {
                Root.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(34) : new GridLength(32) });
                Root.RowDefinitions.Add(new RowDefinition { Height = i == 0 ? new GridLength(20) : new GridLength(32) });
            }

            for (int i = 0; i < 16; i++)
            {
                _columnHeaders[i] = AddHeader(0, i + 1);
                _rowHeaders[i] = AddHeader(i + 1, 0);
            }

            for (int value = 0; value < 256; value++)
            {
                int captured = value;
                var cell = new Button
                {
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    MinHeight = 0,
                    Width = 32,
                    Height = 32,
                    FontSize = 13,
                    FontFamily = ToolParts.Mono,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Tag = value,
                    UseSystemFocusVisuals = true,
                };
                AutomationProperties.SetAutomationId(cell, idPrefix + value.ToString("X2", CultureInfo.InvariantCulture));
                cell.Click += (_, _) => select(captured);
                cell.DoubleTapped += (_, _) => activate(captured);
                cell.GotFocus += (_, _) => FocusedValue = captured;
                Grid.SetRow(cell, (value >> 4) + 1);
                Grid.SetColumn(cell, (value & 15) + 1);
                Root.Children.Add(cell);
                Cells[value] = cell;
            }

            Root.KeyDown += (_, e) =>
            {
                if (e.OriginalSource is not Button { Tag: int v })
                {
                    return;
                }

                int next = e.Key switch
                {
                    VirtualKey.Left => v - 1,
                    VirtualKey.Right => v + 1,
                    VirtualKey.Up => v - 16,
                    VirtualKey.Down => v + 16,
                    VirtualKey.Home => v & ~15,
                    VirtualKey.End => v | 15,
                    _ => -1,
                };
                if (e.Key == VirtualKey.Enter)
                {
                    select(v);
                    activate(v);
                    e.Handled = true;
                }
                else if (next >= 0 && next < 256)
                {
                    Cells[next].Focus(FocusState.Keyboard);
                    e.Handled = true;
                }
                else if (next != -1)
                {
                    e.Handled = true;
                }
            };
        }

        public string IdPrefix { get; }

        public Func<int, CharacterCell> CellOf { get; set; }

        public Func<int, string> Header { get; set; }

        public Grid Root { get; }

        public Button[] Cells { get; } = new Button[256];

        public int FocusedValue { get; private set; } = -1;

        private readonly TextBlock[] _columnHeaders = new TextBlock[16];
        private readonly TextBlock[] _rowHeaders = new TextBlock[16];

        private TextBlock AddHeader(int row, int column)
        {
            var text = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = ToolParts.Mono,
                FontSize = 11,
                Foreground = ToolParts.ThemeBrush("TextFillColorSecondaryBrush"),
            };
            AutomationProperties.SetAccessibilityView(text, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, column);
            Root.Children.Add(text);
            return text;
        }

        /// <summary>見出しとセルの表示を作り直す。</summary>
        public void Update(int? selected)
        {
            for (int i = 0; i < 16; i++)
            {
                _columnHeaders[i].Text = Header(i);
                _rowHeaders[i].Text = Header(i * 16);
            }

            for (int v = 0; v < 256; v++)
            {
                CharacterCell cell = CellOf(v);
                Button button = Cells[v];
                button.Content = cell.Kind == CharacterCellKind.Undefined ? "⁄" : cell.Label;
                button.Opacity = cell.Kind == CharacterCellKind.Undefined ? 0.4 : 1;
                button.FontWeight = cell.Kind == CharacterCellKind.Lead ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
                AutomationProperties.SetName(button, cell.SpokenText);
                bool isSelected = selected == v;
                button.BorderThickness = new Thickness(isSelected ? 2 : 1);
                button.BorderBrush = ToolParts.ThemeBrush(isSelected ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
            }
        }
    }

    private readonly ComboBox _encodingChoice = new();
    private readonly ComboBox _baseChoice = new();
    private readonly ToggleButton _listMode = new();
    private readonly ToggleButton _utf16Endian = new();
    private readonly TextBox _search;
    private readonly TextBlock _searchMessage = new();
    private readonly IReadOnlyList<EncodingEntry> _encodings;
    private readonly CellGrid _first;
    private readonly CellGrid _second;
    private readonly StackPanel _secondHost = new() { Spacing = 4 };
    private readonly TextBlock _secondTitle = new();
    private readonly StackPanel _gridHost = new() { Spacing = 8 };
    private readonly StackPanel _listHost = new();
    private readonly Dictionary<string, TextBlock> _detail = [];
    private readonly StackPanel _detailPanel = new() { Spacing = 4 };

    private CharacterTable? _table;
    private int? _lead;
    private int? _selected;
    private int? _selectedFirst;

    public CharTablePanel(ToolHost host)
        : base(host)
    {
        _encodings = [.. EncodingCatalog.All.Where(CharacterTable.IsSupported).Where(e => e.CodePage != 12000)];
        _encodingChoice.Header = Loc.Get("Chars_Encoding");
        foreach (EncodingEntry entry in _encodings)
        {
            _encodingChoice.Items.Add(MainWindow.EncodingDisplayText(entry));
        }

        AutomationProperties.SetAutomationId(_encodingChoice, "Chars_Encoding");
        AutomationProperties.SetName(_encodingChoice, Loc.Get("Chars_Encoding"));
        _encodingChoice.SelectionChanged += (_, _) => EncodingChanged();

        _baseChoice.Header = Loc.Get("Chars_Base");
        _baseChoice.Items.Add(Loc.Get("Chars_Base_Hex"));
        _baseChoice.Items.Add(Loc.Get("Chars_Base_Dec"));
        _baseChoice.Items.Add(Loc.Get("Chars_Base_Oct"));
        _baseChoice.SelectedIndex = int.TryParse(AppState.GetString(BaseKey, "0"), NumberStyles.None, CultureInfo.InvariantCulture, out int b) && b is >= 0 and < 3 ? b : 0;
        AutomationProperties.SetAutomationId(_baseChoice, "Chars_Base");
        AutomationProperties.SetName(_baseChoice, Loc.Get("Chars_Base"));
        _baseChoice.SelectionChanged += (_, _) =>
        {
            AppState.SetString(BaseKey, _baseChoice.SelectedIndex.ToString(CultureInfo.InvariantCulture));
            RefreshTables();
        };

        _listMode.Content = Loc.Get("Chars_ListMode");
        _listMode.IsChecked = AppState.GetString(ListKey, "0") == "1";
        AutomationProperties.SetAutomationId(_listMode, "Chars_ListMode");
        _listMode.Click += (_, _) =>
        {
            AppState.SetString(ListKey, _listMode.IsChecked == true ? "1" : "0");
            ApplyMode();
        };

        _search = ToolParts.Box("Chars_Search", Loc.Get("Chars_Search"), mono: false);
        _search.PlaceholderText = Loc.Get("Chars_SearchPlaceholder");
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                RunSearch();
                e.Handled = true;
            }
        };
        AutomationProperties.SetAutomationId(_searchMessage, "Chars_SearchMessage");
        _searchMessage.Foreground = ToolParts.ThemeBrush("SystemFillColorCriticalBrush");
        _searchMessage.Visibility = Visibility.Collapsed;

        _first = new CellGrid("Chars_Cell_", v => _table?.Cell((byte)v) ?? Undefined(v), v => SelectFirst(v), v => Activate(v, trail: false), HeaderText);
        _second = new CellGrid("Chars_Trail_", v => _table is not null && _lead is { } l ? _table.TrailCell((byte)l, (byte)v) : Undefined(v),
            v => SelectTrail(v), v => Activate(v, trail: true), HeaderText);

        var options = new WrapPanel();
        foreach (FrameworkElement e in new FrameworkElement[] { _encodingChoice, _baseChoice, _listMode })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            e.VerticalAlignment = VerticalAlignment.Bottom;
            options.Children.Add(e);
        }

        Button find = ToolParts.TextButton("Chars_SearchButton", "Chars_SearchButton", RunSearch);
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        searchRow.Children.Add(_search);
        find.VerticalAlignment = VerticalAlignment.Bottom;
        searchRow.Children.Add(find);

        _secondTitle.Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"];
        AutomationProperties.SetAutomationId(_secondTitle, "Chars_TrailTitle");
        _secondHost.Children.Add(_secondTitle);
        _secondHost.Children.Add(_second.Root);
        _secondHost.Visibility = Visibility.Collapsed;
        AutomationProperties.SetAutomationId(_first.Root, "Chars_Table");
        AutomationProperties.SetName(_first.Root, Loc.Get("Chars_TableName"));
        AutomationProperties.SetAutomationId(_second.Root, "Chars_TrailTable");
        AutomationProperties.SetName(_second.Root, Loc.Get("Chars_TrailTableName"));
        _gridHost.Children.Add(_first.Root);
        _gridHost.Children.Add(_secondHost);
        AutomationProperties.SetAutomationId(_listHost, "Chars_List");

        BuildDetail();

        var body = new StackPanel();
        body.Children.Add(options);
        body.Children.Add(searchRow);
        body.Children.Add(_searchMessage);
        body.Children.Add(_gridHost);
        body.Children.Add(_listHost);
        body.Children.Add(_detailPanel);
        Content = ToolParts.Frame(body);
        FlowDirection = FlowDirection.LeftToRight;

        string saved = AppState.GetString(EncodingKey, string.Empty);
        SelectEncoding(saved.Length > 0 ? saved : DefaultEncodingId());
        ApplyMode();
    }

    private static CharacterCell Undefined(int v) => new(CharacterCellKind.Undefined, [(byte)v], -1, string.Empty, string.Empty, null);

    protected override Control FirstFocus => _encodingChoice;

    private string DefaultEncodingId() => Host.Editor?.View.Encoding is { Length: > 0 } id && FindEncoding(id) >= 0 ? id : "cp437";

    private int FindEncoding(string id)
    {
        for (int i = 0; i < _encodings.Count; i++)
        {
            if (string.Equals(_encodings[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>文字コードを選ぶ。</summary>
    public void SelectEncoding(string id)
    {
        int index = FindEncoding(id);
        if (index >= 0 && _encodingChoice.SelectedIndex != index)
        {
            _encodingChoice.SelectedIndex = index;
        }
        else if (index >= 0)
        {
            EncodingChanged();
        }
    }

    private void EncodingChanged()
    {
        if (_encodingChoice.SelectedIndex < 0)
        {
            return;
        }

        EncodingEntry entry = _encodings[_encodingChoice.SelectedIndex];
        _table = CharacterTable.TryCreate(entry);
        AppState.SetString(EncodingKey, entry.Id);
        _lead = null;
        _selected = null;
        _selectedFirst = null;
        _secondHost.Visibility = Visibility.Collapsed;
        RefreshTables();
        ShowDetail(null);
    }

    private string HeaderText(int value) => _baseChoice.SelectedIndex switch
    {
        1 => value.ToString(CultureInfo.InvariantCulture),
        2 => Convert.ToString(value, 8),
        _ => value.ToString("X", CultureInfo.InvariantCulture),
    };

    private void RefreshTables()
    {
        _first.Update(_selectedFirst);
        if (_lead is not null)
        {
            _second.Update(_selected is { } s && _selectedFirst == _lead ? s : null);
        }

        RebuildList();
    }

    private void ApplyMode()
    {
        bool list = _listMode.IsChecked == true;
        _gridHost.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        _listHost.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        RebuildList();
    }

    private void RebuildList()
    {
        _listHost.Children.Clear();
        if (_listMode.IsChecked != true || _table is null)
        {
            return;
        }

        foreach (CharacterListRow row in _table.List(_lead is { } l ? (byte)l : null))
        {
            var grid = new Grid { ColumnSpacing = 8 };
            double[] widths = [40, 40, 40, 70, 80, 220];
            foreach (double w in widths)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            }

            string[] texts = [row.Hex, row.Decimal, row.Octal, row.Character, row.CodePoint, row.Name];
            for (int i = 0; i < texts.Length; i++)
            {
                var t = new TextBlock { Text = texts[i], FontFamily = ToolParts.Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(t, i);
                grid.Children.Add(t);
            }

            AutomationProperties.SetName(grid, string.Join(", ", texts.Where(t => t.Length > 0)));
            _listHost.Children.Add(grid);
        }
    }

    // ---- 選択 ----

    private void SelectFirst(int value)
    {
        if (_table is null)
        {
            return;
        }

        CharacterCell cell = _table.Cell((byte)value);
        _selectedFirst = value;
        if (cell.Kind == CharacterCellKind.Lead)
        {
            _lead = value;
            _selected = null;
            _secondTitle.Text = Loc.Format("Chars_TrailTitle", "0x" + value.ToString("X2", CultureInfo.InvariantCulture));
            _secondHost.Visibility = Visibility.Visible;
            _second.Update(null);
            RebuildList();
        }
        else
        {
            _lead = null;
            _selected = value;
            _secondHost.Visibility = Visibility.Collapsed;
        }

        _first.Update(_selectedFirst);
        ShowDetail(cell.Kind == CharacterCellKind.Lead ? null : cell);
    }

    private void SelectTrail(int value)
    {
        if (_table is null || _lead is not { } lead)
        {
            return;
        }

        _selected = value;
        _second.Update(value);
        ShowDetail(_table.TrailCell((byte)lead, (byte)value));
    }

    private CharacterCell? _detailCell;

    private void Activate(int value, bool trail)
    {
        if (_table is null)
        {
            return;
        }

        CharacterCell cell = trail && _lead is { } lead ? _table.TrailCell((byte)lead, (byte)value) : _table.Cell((byte)value);
        if (cell.Kind == CharacterCellKind.Lead)
        {
            Cells(trail: true)[0].Focus(FocusState.Keyboard);
            return;
        }

        if (cell.Kind == CharacterCellKind.Character)
        {
            InsertCell(cell);
        }
    }

    private Button[] Cells(bool trail) => trail ? _second.Cells : _first.Cells;

    /// <summary>セルのバイトをカーソル位置に入力する。現在の入力モード(上書き / 挿入)に従う。</summary>
    private bool InsertCell(CharacterCell cell) => Host.Write(cell.Bytes, "文字コード表からの入力", true);

    /// <summary>詳細欄で選んでいるセルをカーソル位置に入力する(ボタン)。</summary>
    public bool InsertSelected() => _detailCell is { Kind: CharacterCellKind.Character } cell && InsertCell(cell);

    // ---- 詳細欄 ----

    private void BuildDetail()
    {
        foreach ((string id, string key) in new[]
        {
            ("Chars_Detail_Hex", "Chars_Row_Hex"), ("Chars_Detail_Dec", "Chars_Row_Dec"), ("Chars_Detail_Oct", "Chars_Row_Oct"),
            ("Chars_Detail_Bin", "Chars_Row_Bin"), ("Chars_Detail_Char", "Chars_Row_Char"), ("Chars_Detail_CodePoint", "Chars_Row_CodePoint"),
            ("Chars_Detail_Name", "Chars_Row_Name"), ("Chars_Detail_Utf8", "Chars_Row_Utf8"), ("Chars_Detail_Utf16", "Chars_Row_Utf16"),
        })
        {
            string captured = id;
            (Grid row, TextBlock value) = ToolParts.ValueRow(id, key, id.Replace("Chars_Detail_", "Chars_Copy_", StringComparison.Ordinal), () => _detail[captured].Text);
            _detail[id] = value;
            _detailPanel.Children.Add(row);
        }

        _utf16Endian.Content = Loc.Get("Chars_Utf16Endian");
        _utf16Endian.IsChecked = AppState.GetString(Utf16Key, "0") == "1";
        _utf16Endian.Content = _utf16Endian.IsChecked == true ? "BE" : "LE";
        AutomationProperties.SetAutomationId(_utf16Endian, "Chars_Utf16Endian");
        AutomationProperties.SetName(_utf16Endian, Loc.Get("Chars_Utf16Endian"));
        _utf16Endian.Click += (_, _) =>
        {
            _utf16Endian.Content = _utf16Endian.IsChecked == true ? "BE" : "LE";
            AppState.SetString(Utf16Key, _utf16Endian.IsChecked == true ? "1" : "0");
            ShowDetail(_detailCell);
        };

        var actions = new WrapPanel();
        foreach (FrameworkElement e in new FrameworkElement[]
        {
            _utf16Endian,
            ToolParts.TextButton("Chars_Insert", "Chars_Insert", () => InsertSelected()),
            ToolParts.TextButton("Chars_CopyChar", "Chars_CopyChar", () => { if (_detailCell is { } c) { ToolParts.Copy(c.Text.Length > 0 ? c.Text : c.Label); } }),
            ToolParts.TextButton("Chars_CopyBytes", "Chars_CopyBytes", () => { if (_detailCell is { } c) { ToolParts.Copy(Convert.ToHexString(c.Bytes)); } }),
        })
        {
            e.Margin = new Thickness(0, 0, 8, 8);
            actions.Children.Add(e);
        }

        _detailPanel.Children.Add(actions);
    }

    private void ShowDetail(CharacterCell? cell)
    {
        _detailCell = cell;
        if (cell is null)
        {
            foreach (TextBlock t in _detail.Values)
            {
                t.Text = string.Empty;
            }

            return;
        }

        CharacterDetail d = CharacterTable.Detail(cell, _utf16Endian.IsChecked == true);
        _detail["Chars_Detail_Hex"].Text = d.ByteHex;
        _detail["Chars_Detail_Dec"].Text = d.ByteDecimal;
        _detail["Chars_Detail_Oct"].Text = d.ByteOctal;
        _detail["Chars_Detail_Bin"].Text = d.ByteBinary;
        _detail["Chars_Detail_Char"].Text = d.Character;
        _detail["Chars_Detail_CodePoint"].Text = d.CodePointText;
        _detail["Chars_Detail_Name"].Text = d.Name ?? string.Empty;
        _detail["Chars_Detail_Utf8"].Text = d.Utf8;
        _detail["Chars_Detail_Utf16"].Text = d.Utf16;
    }

    // ---- 検索 (仕様 6) ----

    private void RunSearch()
    {
        if (_table is null)
        {
            return;
        }

        (byte? Lead, byte Value)? found = _table.Find(_search.Text);
        if (found is null)
        {
            _searchMessage.Text = Loc.Get("Chars_NotFound");
            _searchMessage.Visibility = Visibility.Visible;
            return;
        }

        _searchMessage.Visibility = Visibility.Collapsed;
        if (found.Value.Lead is { } lead)
        {
            SelectFirst(lead);
            SelectTrail(found.Value.Value);
            _second.Cells[found.Value.Value].Focus(FocusState.Programmatic);
        }
        else
        {
            SelectFirst(found.Value.Value);
            _first.Cells[found.Value.Value].Focus(FocusState.Programmatic);
        }
    }

    // ---- テスト用 ----

    internal CharacterTable? Table => _table;

    internal void SelectCell(int value, int? lead = null)
    {
        if (lead is { } l)
        {
            SelectFirst(l);
            SelectTrail(value);
        }
        else
        {
            SelectFirst(value);
        }
    }

    internal void SetUtf16BigEndian(bool big)
    {
        _utf16Endian.IsChecked = big;
        _utf16Endian.Content = big ? "BE" : "LE";
        AppState.SetString(Utf16Key, big ? "1" : "0");
        ShowDetail(_detailCell);
    }

    internal string Detail(string id) => _detail[id].Text;

    internal Button FirstCell(int value) => _first.Cells[value];

    internal Button TrailCell(int value) => _second.Cells[value];

    internal bool TrailVisible => _secondHost.Visibility == Visibility.Visible;

    internal int FocusedFirst => _first.FocusedValue;

    internal void FocusFirstCell(int value) => _first.Cells[value].Focus(FocusState.Keyboard);

    internal CharacterCell? DetailCell => _detailCell;
}
