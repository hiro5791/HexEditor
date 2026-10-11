using System.Text;
using HexEditor.App.Services;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace HexEditor.App.Controls;

/// <summary>
/// 列見出し (VIEW-05)。Hex 列とテキスト列の上に、行の中での位置を示す見出しを固定表示する。カーソルのある列の見出しを強調する
/// (VIEW-06 の仕様 2)。オフセット列の見出しを押すと、オフセットの基数を選ぶメニューを出す (VIEW-05 の仕様 7)。
/// </summary>
public sealed partial class HexView
{
    private string _rulerLine = string.Empty;
    private (int Start, int Length)[] _rulerHighlight = [];
    private readonly List<(string Text, int Index, bool Hex)> _rulerLabels = [];

    // 見出しの文字列を作った条件。変わらなければ作り直さない (1 行 4,096 バイトでは 8,000 個の文字列になり、毎フレーム作ると
    // 描画が遅れる。VIEW-04 の仕様 3)。
    private (RowFormat Format, long BaseValue, OffsetRadix Radix, bool Lowercase, string Names)? _rulerKey;

    /// <summary>テキスト列の見出し (VIEW-24 の仕様 3): 列の番号、文字コード名、見出しの文字位置。テキスト列が 1 列なら空。</summary>
    private readonly List<(int Column, string Name, int Index)> _textHeaders = [];

    /// <summary>テキスト列の見出しに出す文字コード名 (UTF-16 / UTF-32 は開始位置も。例: 「UTF-16 LE (奇数)」)。</summary>
    private string TextColumnName(int column)
    {
        if (_editor is null)
        {
            return string.Empty;
        }

        TextEncoding encoding = _editor.TextEncodingOf(column);
        IReadOnlyList<TextColumnSpec> specs = _editor.View.TextColumns;
        TextColumnSpec spec = column < specs.Count ? specs[column] : new TextColumnSpec(encoding.Id);
        return encoding.Kind switch
        {
            TextEncodingKind.Utf16 => Loc.Format(spec.Utf16Phase == 1 ? "HexView_TextColumn_Odd" : "HexView_TextColumn_Even", encoding.Name),
            TextEncodingKind.Utf32 => Loc.Format("HexView_TextColumn_Phase", encoding.Name, spec.Utf32Phase),
            _ => encoding.Name,
        };
    }
    private string _rulerText = string.Empty;
    private MenuFlyout? _radixMenu;

    /// <summary>オフセットの基数を変えるよう求めた (列見出しのメニュー)。ウィンドウはメニューの表示を更新する。</summary>
    public event EventHandler? ViewSettingsChanged;

    private void ApplyRulerFont()
    {
        foreach (TextBlock t in (TextBlock[])[RulerText, OffsetHeaderText])
        {
            t.FontFamily = _font;
            t.FontSize = _fontSize;
            t.CharacterSpacing = _characterSpacing;
        }

        RulerText.LineHeight = _rowHeight;
        RulerBar.Height = _rowHeight;
    }

    /// <summary>見出しの位置を、オフセット列の幅と横スクロールに合わせる。</summary>
    private void LayoutRuler()
    {
        double contentLeft = ContentLeft;
        OffsetHeader.Width = Math.Max(0, contentLeft - _cellWidth);
        OffsetHeader.Padding = new Thickness(LeftPadding, 0, 0, 0);
        OffsetHeader.Visibility = _showOffset ? Visibility.Visible : Visibility.Collapsed;
        RulerViewport.Margin = new Thickness(contentLeft, 0, 0, 0);
        double width = Math.Max(0, Surface.ActualWidth - contentLeft);
        RulerViewportClip.Rect = new Windows.Foundation.Rect(0, 0, width, Math.Max(_rowHeight, 1));
    }

    /// <summary>見出しの文字列と、カーソルのある列の強調を更新する。</summary>
    private void UpdateRuler(HexLayout layout, ViewSettings view, OffsetFormat format)
    {
        Visibility visibility = view.ShowRuler ? Visibility.Visible : Visibility.Collapsed;
        if (RulerBar.Visibility != visibility)
        {
            RulerBar.Visibility = visibility;
        }

        if (!view.ShowRuler)
        {
            return;
        }

        string header = Loc.Get(view.Radix switch
        {
            OffsetRadix.Decimal => "HexView_Ruler_OffsetDecimal",
            OffsetRadix.Octal => "HexView_Ruler_OffsetOctal",
            OffsetRadix.Sector => "HexView_Ruler_OffsetSector",
            _ => "HexView_Ruler_OffsetHex",
        });
        if (OffsetHeaderText.Text != header)
        {
            OffsetHeaderText.Text = header;
            AutomationProperties.SetName(OffsetHeader, header);
            AutomationProperties.SetHelpText(OffsetHeader, Loc.Get("HexView_Ruler_OffsetHelp"));
        }

        // 行の先頭のアドレスの下位の桁 (VIEW-05 の仕様 4)。行の先頭のずれがあると、行が …08 などから始まる。
        RowFormat row = _format;
        int b = row.BytesPerRow;
        ulong first = format.AddressOf(layout.RowStart(0));
        long baseValue = (long)(first % (ulong)b);
        OffsetRadix radix = view.Radix == OffsetRadix.Sector ? OffsetRadix.Hex : view.Radix;
        string names = row.ShownTextColumns > 1 ? string.Join('|', Enumerable.Range(0, row.ShownTextColumns).Select(TextColumnName)) : string.Empty;
        (RowFormat, long, OffsetRadix, bool, string) key = (row, baseValue, radix, view.LowercaseHex, names);
        if (_rulerKey != key)
        {
            _rulerKey = key;
            var line = new StringBuilder(new string(' ', row.LineLength));
            _rulerLabels.Clear();
            _textHeaders.Clear();
            if (row.ShowHex)
            {
                // 見出しはグループの先頭の表示の位置に置く。逆順表示では、その位置にあるバイトの番号 (数字も逆順。VIEW-11 の仕様 9)。
                // Hex 以外の形式ではセルごと (VIEW-10)。
                int group = row.IsHexBytes ? Math.Max(1, row.GroupSize) : row.Unit;
                for (int s = 0; s < b; s += group)
                {
                    int c = row.IsHexBytes ? row.ByteOfSlot(s, b) : s;
                    int at = row.IsHexBytes ? row.CellStart(s) : row.CellStart(s / row.Unit);
                    string label = OffsetFormat.RulerLabel(baseValue + c, radix, view.LowercaseHex, RowFormat.HexCellChars);
                    Put(line, at, label);
                    _rulerLabels.Add((label, at, true));
                }
            }

            for (int t = 0; row.ShowText && t < row.ShownTextColumns; t++)
            {
                if (row.ShownTextColumns > 1)
                {
                    // テキスト列が複数あるときは、各列の見出しに文字コード名を表示する (VIEW-24 の仕様 3)。
                    string name = TextColumnName(t);
                    string shown = name.Length > b ? name[..Math.Max(1, b - 1)] + "…" : name;
                    int at = row.TextIndex(t, 0);
                    Put(line, at, shown);
                    _textHeaders.Add((t, name, at));
                    continue;
                }

                for (int c = 0; c < b; c++)
                {
                    string digit = OffsetFormat.RulerLabel(baseValue + c, radix, view.LowercaseHex, 1);
                    Put(line, row.TextIndex(t, c), digit);
                    _rulerLabels.Add((digit, row.TextIndex(t, c), false));
                }
            }

            _rulerText = line.ToString();
        }

        // カーソルのある位置の見出しを強調する (VIEW-06 の仕様 2)。グループ化しているときはグループの見出し。
        var highlight = new List<(int, int)>();
        if (view.HighlightCurrentRow && _editor is not null)
        {
            int column = layout.ColumnOf(_editor.Cursor);
            if (row.ShowHex)
            {
                int group = row.IsHexBytes ? Math.Max(1, row.GroupSize) : row.Unit;
                int slot = row.IsHexBytes ? row.SlotOf(column, b) : column;
                int groupStart = slot / group * group;
                highlight.Add((row.IsHexBytes ? row.CellStart(groupStart) : row.CellStart(groupStart / row.Unit), RowFormat.HexCellChars));
            }

            if (row.ShowText && row.ShownTextColumns == 1)
            {
                highlight.Add((row.TextIndex(column), 1));
            }
        }

        string text = _rulerText;
        (int, int)[] marks = [.. highlight];
        if (text == _rulerLine && marks.SequenceEqual(_rulerHighlight))
        {
            return;
        }

        _rulerLine = text;
        _rulerHighlight = marks;
        RulerText.Text = text;
        RulerText.TextHighlighters.Clear();
        if (marks.Length > 0 && _palette is not null)
        {
            var h = new TextHighlighter { Background = _palette.RulerHighlight, Foreground = _palette.RulerHighlightText };
            foreach ((int start, int length) in marks)
            {
                h.Ranges.Add(new TextRange { StartIndex = start, Length = length });
            }

            RulerText.TextHighlighters.Add(h);
        }
    }

    private static void Put(StringBuilder line, int index, string text)
    {
        for (int i = 0; i < text.Length && index + i < line.Length; i++)
        {
            line[index + i] = text[i];
        }
    }

    /// <summary>オフセット列の見出しのクリック: 基数を選ぶメニュー (VIEW-05 の仕様 7、VIEW-19)。</summary>
    private void OffsetHeader_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        _radixMenu ??= CreateRadixMenu();
        foreach (MenuFlyoutItemBase item in _radixMenu.Items)
        {
            if (item is RadioMenuFlyoutItem radio && radio.Tag is OffsetRadix r)
            {
                radio.IsChecked = _editor.View.Radix == r;
            }
        }

        _radixMenu.ShowAt(OffsetHeader);
    }

    private MenuFlyout CreateRadixMenu()
    {
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "HexView_RadixMenu");
        foreach ((OffsetRadix radix, string key) in new[]
        {
            (OffsetRadix.Hex, "Menu_View_RadixHex"),
            (OffsetRadix.Decimal, "Menu_View_RadixDecimal"),
            (OffsetRadix.Octal, "Menu_View_RadixOctal"),
            (OffsetRadix.Sector, "Menu_View_RadixSector"),
        })
        {
            var item = new RadioMenuFlyoutItem { Text = Loc.Get(key + "/Text"), Tag = radix, GroupName = "HexViewRadix" };
            AutomationProperties.SetAutomationId(item, "HexView_Radix" + radix);
            item.Click += (_, _) =>
            {
                if (_editor is not null)
                {
                    _editor.ApplyView(_editor.View with { Radix = radix });
                    ViewSettingsChanged?.Invoke(this, EventArgs.Empty);
                }
            };
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) => Focus(FocusState.Programmatic);
        return menu;
    }

    /// <summary>列見出しのメニューが開いているか (テスト用)。</summary>
    internal bool RadixMenuOpen => _radixMenu?.IsOpen ?? false;

    // ---- テキスト列の見出しの右クリックメニュー (VIEW-24 の仕様 3) ----

    private MenuFlyout? _textColumnMenu;
    private int _textColumnMenuColumn;

    /// <summary>テキスト列の文字コードを変えるよう求めた (見出しのメニュー。引数は列の番号)。ウィンドウが文字コードの一覧を開く。</summary>
    public event EventHandler<int>? TextColumnEncodingRequested;

    /// <summary>テキスト列の構成を変えた (列の削除・移動)。ウィンドウはメニューの表示を更新する。</summary>
    private void RaiseViewSettingsChanged() => ViewSettingsChanged?.Invoke(this, EventArgs.Empty);

    private void RulerBar_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (_editor is null || _textHeaders.Count == 0)
        {
            return;
        }

        double x = e.GetPosition(RulerViewport).X - RulerShift.X;
        int index = (int)Math.Floor(x / _cellWidth);
        int column = -1;
        foreach ((int c, _, int at) in _textHeaders)
        {
            if (index >= at - 1)
            {
                column = c;
            }
        }

        if (column >= 0)
        {
            ShowTextColumnMenu(column, e.GetPosition(this));
            e.Handled = true;
        }
    }

    /// <summary>テキスト列の見出しのメニューを開く (テスト用の命令からも呼ぶ)。</summary>
    internal void ShowTextColumnMenu(int column, Windows.Foundation.Point? position = null)
    {
        if (_editor is null)
        {
            return;
        }

        _textColumnMenuColumn = column;
        _textColumnMenu ??= CreateTextColumnMenu();
        UpdateTextColumnItems(_textColumnMenu.Items, column);
        _textColumnMenu.ShowAt(this, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = position ?? new Windows.Foundation.Point(ContentLeft, _rowHeight),
        });
    }

    /// <summary>テキスト列の見出しのメニューの項目を選ぶ (テスト用。項目の Click と同じ処理)。</summary>
    internal void ChooseTextColumnMenu(int column, string action)
    {
        _textColumnMenuColumn = column;
        RunTextColumnAction(action, column);
        _textColumnMenu?.Hide();
    }

    private MenuFlyout CreateTextColumnMenu()
    {
        var menu = new MenuFlyout();
        AutomationProperties.SetAutomationId(menu, "HexView_TextColumnMenu");
        foreach (MenuFlyoutItem item in CreateTextColumnItems("HexView_TextColumn", () => _textColumnMenuColumn))
        {
            menu.Items.Add(item);
        }

        menu.Closed += (_, _) => Focus(FocusState.Programmatic);
        return menu;
    }

    /// <summary>
    /// テキスト列のメニューの項目 (文字コードの変更・削除・左へ移動・右へ移動。VIEW-24 の仕様 3)。見出しのメニューと、Hex ビューの右クリックメニュー
    /// (Shift+F10・アプリケーションキーで開ける) の「テキスト列」で使う。<paramref name="column"/> が対象の列を返す。
    /// </summary>
    private List<MenuFlyoutItem> CreateTextColumnItems(string idPrefix, Func<int> column)
    {
        var items = new List<MenuFlyoutItem>();
        foreach ((string tag, string key) in new[]
        {
            ("Encoding", "HexView_TextColumn_ChangeEncoding"),
            ("Remove", "HexView_TextColumn_Remove"),
            ("Left", "HexView_TextColumn_MoveLeft"),
            ("Right", "HexView_TextColumn_MoveRight"),
        })
        {
            var item = new MenuFlyoutItem { Text = Loc.Get(key), Tag = tag };
            AutomationProperties.SetAutomationId(item, idPrefix + tag);
            item.Click += (_, _) => RunTextColumnAction(tag, column());
            items.Add(item);
        }

        return items;
    }

    /// <summary>テキスト列のメニューの項目を、<paramref name="column"/> の列に対して使えるかで有効・無効にする。</summary>
    private void UpdateTextColumnItems(IEnumerable<MenuFlyoutItemBase> items, int column)
    {
        int count = _editor?.View.TextColumnCount ?? 1;
        foreach (MenuFlyoutItemBase item in items)
        {
            if (item is MenuFlyoutItem { Tag: string tag } m)
            {
                m.IsEnabled = tag switch
                {
                    "Remove" => count > 1,
                    "Left" => column > 0,
                    "Right" => column < count - 1,
                    _ => true,
                };
            }
        }
    }

    private void RunTextColumnAction(string action, int column)
    {
        if (_editor is null || column < 0 || column >= _editor.View.TextColumnCount)
        {
            return;
        }

        ViewSettings? next = action switch
        {
            "Remove" => _editor.View.WithoutTextColumn(column),
            "Left" => _editor.View.WithTextColumnMoved(column, -1),
            "Right" => _editor.View.WithTextColumnMoved(column, +1),
            _ => null,
        };
        if (action == "Encoding")
        {
            TextColumnEncodingRequested?.Invoke(this, column);
            return;
        }

        if (next is null)
        {
            return;
        }

        bool wasActive = _editor.TextColumn == column;
        _editor.ApplyView(next);

        // 移した列が操作中の列なら、移した先を操作中の列にする。
        if (wasActive && action is "Left" or "Right")
        {
            _editor.SetTextColumn(column + (action == "Left" ? -1 : 1));
        }

        RaiseViewSettingsChanged();
    }

    /// <summary>列見出しのメニューの項目を選ぶ (テスト用。メニューの項目の Click と同じ処理)。</summary>
    internal void ChooseRadix(OffsetRadix radix)
    {
        if (_editor is not null)
        {
            _editor.ApplyView(_editor.View with { Radix = radix });
            ViewSettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        _radixMenu?.Hide();
    }
}
