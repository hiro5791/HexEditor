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

        OffsetHeaderText.Text = Loc.Get(view.Radix switch
        {
            OffsetRadix.Decimal => "HexView_Ruler_OffsetDecimal",
            OffsetRadix.Octal => "HexView_Ruler_OffsetOctal",
            OffsetRadix.Sector => "HexView_Ruler_OffsetSector",
            _ => "HexView_Ruler_OffsetHex",
        });
        AutomationProperties.SetName(OffsetHeader, OffsetHeaderText.Text);
        AutomationProperties.SetHelpText(OffsetHeader, Loc.Get("HexView_Ruler_OffsetHelp"));

        // 行の先頭のアドレスの下位の桁 (VIEW-05 の仕様 4)。行の先頭のずれがあると、行が …08 などから始まる。
        RowFormat row = _format;
        int b = row.BytesPerRow;
        ulong first = format.AddressOf(layout.RowStart(0));
        long baseValue = (long)(first % (ulong)b);
        OffsetRadix radix = view.Radix == OffsetRadix.Sector ? OffsetRadix.Hex : view.Radix;
        var line = new StringBuilder(new string(' ', row.LineLength));
        _rulerLabels.Clear();
        for (int c = 0; c < b; c++)
        {
            if (row.ShowHex && row.IsGroupStart(c))
            {
                string label = OffsetFormat.RulerLabel(baseValue + c, radix, view.LowercaseHex, RowFormat.HexCellChars);
                Put(line, row.HexIndex(c), label);
                _rulerLabels.Add((label, row.HexIndex(c), true));
            }

            if (row.ShowText)
            {
                string digit = OffsetFormat.RulerLabel(baseValue + c, radix, view.LowercaseHex, 1);
                Put(line, row.TextIndex(c), digit);
                _rulerLabels.Add((digit, row.TextIndex(c), false));
            }
        }

        // カーソルのある位置の見出しを強調する (VIEW-06 の仕様 2)。グループ化しているときはグループの見出し。
        var highlight = new List<(int, int)>();
        if (view.HighlightCurrentRow && _editor is not null)
        {
            int column = layout.ColumnOf(_editor.Cursor);
            if (row.ShowHex)
            {
                int groupStart = column / Math.Max(1, row.GroupSize) * Math.Max(1, row.GroupSize);
                highlight.Add((row.HexIndex(groupStart), RowFormat.HexCellChars));
            }

            if (row.ShowText)
            {
                highlight.Add((row.TextIndex(column), 1));
            }
        }

        string text = line.ToString();
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
