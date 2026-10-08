using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Search;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索履歴 (FIND-28)。検索 (次を検索・すべて検索・置換) のたびに検索語と条件を履歴の先頭に加え、検索欄・置換欄の ↑ / ↓ と
/// 検索欄のドロップダウンで呼び出す。履歴そのもの (保存を含む) はウィンドウが持つ <see cref="SearchHistory"/>。
/// </summary>
public sealed partial class FindBar
{
    private readonly HistoryCursor _historyCursor = new();
    private readonly HistoryCursor _replaceCursor = new();
    /// <summary>
    /// コードで検索欄・置換欄に入れた文字列 (TextChanged は後から非同期に届くため、利用者の入力と区別する。届いたら null に戻す)。
    /// これと同じ文字列の変更では、履歴の位置を戻さず、インクリメンタルサーチもしない。
    /// </summary>
    private string? _programmaticQuery;

    private string? _programmaticReplace;

    /// <summary>検索履歴 (null なら履歴を使わない)。</summary>
    public SearchHistory? History { get; set; }

    /// <summary>今の条件 (種類とオプション)。</summary>
    internal SearchConditions CurrentConditions() => new()
    {
        Kind = Kind,
        Encoding = SelectedEncoding,
        CaseSensitive = CaseChoice.IsChecked == true,
        UseEscapes = EscapeChoice.IsChecked == true,
        AlignToCharacters = AlignChoice.IsChecked == true,
        WholeWord = WordChoice.IsChecked == true,
        IntegerBits = SelectedBits,
        Sign = (IntegerSign)Math.Max(0, SignChoice.SelectedIndex),
        Endian = (SearchEndian)Math.Max(0, EndianChoice.SelectedIndex),
        FloatFormat = (FloatFormat)Math.Max(0, FloatChoice.SelectedIndex),
        Tolerance = (ToleranceKind)Math.Max(0, ToleranceChoice.SelectedIndex),
        ToleranceText = ToleranceValue.Text,
    };

    /// <summary>履歴の条件を検索バーに戻す (FIND-28 の仕様 3)。</summary>
    private void ApplyConditions(SearchConditions c)
    {
        KindChoice.SelectedIndex = (int)c.Kind;
        foreach (object item in EncodingChoice.Items)
        {
            if (item is ComboBoxItem { Tag: TextEncodingId id } && id == c.Encoding)
            {
                EncodingChoice.SelectedItem = item;
            }
        }

        CaseChoice.IsChecked = c.CaseSensitive;
        EscapeChoice.IsChecked = c.UseEscapes;
        AlignChoice.IsChecked = c.AlignToCharacters;
        WordChoice.IsChecked = c.WholeWord;
        IntBitsChoice.SelectedIndex = Math.Max(0, NumericSearch.IntegerSizes.ToList().IndexOf(c.IntegerBits));
        SignChoice.SelectedIndex = (int)c.Sign;
        EndianChoice.SelectedIndex = (int)c.Endian;
        FloatChoice.SelectedIndex = (int)c.FloatFormat;
        ToleranceChoice.SelectedIndex = (int)c.Tolerance;
        ToleranceValue.Text = c.ToleranceText;
    }

    /// <summary>検索を実行したときに、検索語と条件を履歴の先頭に加える (FIND-28 の仕様 1)。</summary>
    private void AddToHistory()
    {
        if (History is not null && _pattern is not null && Query.Text.Length > 0)
        {
            History.Add(HistoryList.Find, new SearchHistoryEntry(Query.Text, CurrentConditions()));
        }

        _historyCursor.Reset();
    }

    /// <summary>置換を実行したときに、置換語を置換欄の履歴に加える (検索欄とは別の履歴。FIND-28 の仕様 2)。</summary>
    private void AddReplacementToHistory()
    {
        AddToHistory();
        if (History is not null && ReplaceQuery.Text.Length > 0)
        {
            History.Add(HistoryList.Replace, new SearchHistoryEntry(ReplaceQuery.Text, CurrentConditions()));
        }

        _replaceCursor.Reset();
    }

    /// <summary>↑ で古い方、↓ で新しい方の履歴を呼び出す。検索欄では条件も切り替える (FIND-28 の仕様 3)。</summary>
    private void RecallHistory(HistoryList list, bool older)
    {
        if (History is null)
        {
            return;
        }

        HistoryCursor cursor = list == HistoryList.Find ? _historyCursor : _replaceCursor;
        IReadOnlyList<SearchHistoryEntry> items = History.Get(list);
        SearchHistoryEntry? entry = older ? cursor.Older(items) : cursor.Newer(items);
        if (entry is null)
        {
            return;
        }

        Apply(list, entry);
    }

    private void Apply(HistoryList list, SearchHistoryEntry entry)
    {
        if (list == HistoryList.Find)
        {
            ApplyConditions(entry.Conditions);
            SetQueryText(entry.Text);
            Query.SelectionStart = Query.Text.Length;
        }
        else
        {
            if (ReplaceQuery.Text != entry.Text)
            {
                _programmaticReplace = entry.Text;
                ReplaceQuery.Text = entry.Text;
            }

            ReplaceQuery.SelectionStart = ReplaceQuery.Text.Length;
        }

        Validate();
    }

    /// <summary>検索欄に文字列を入れる (利用者の入力ではないので、履歴の位置を保ち、インクリメンタルサーチもしない)。</summary>
    private void SetQueryText(string text)
    {
        if (Query.Text != text)
        {
            _programmaticQuery = text;
            Query.Text = text;
        }
    }

    /// <summary>検索履歴を消去する (「検索履歴を消去」。FIND-28)。</summary>
    public void ClearHistory()
    {
        History?.Clear();
        _historyCursor.Reset();
        _replaceCursor.Reset();
    }

    /// <summary>テスト用: 履歴の検索語の一覧 (新しい順)。</summary>
    internal IReadOnlyList<string> HistoryTexts(HistoryList list) => History?.Get(list).Select(e => e.Text).ToList() ?? [];

    /// <summary>テスト用: 履歴の項目を選ぶ (ドロップダウンの項目のクリックと同じ)。</summary>
    internal void ChooseHistory(int index)
    {
        if (History is { } history && index >= 0 && index < history.Find.Count)
        {
            Apply(HistoryList.Find, history.Find[index]);
        }
    }

    /// <summary>テスト用: 履歴の項目を削除する (ドロップダウンの削除ボタンと同じ)。</summary>
    internal void DeleteHistory(int index) => History?.RemoveAt(HistoryList.Find, index);

    /// <summary>ドロップダウンを開いたら、履歴の一覧を作る (FIND-28 の仕様 4)。各項目は削除できる。</summary>
    private void HistoryFlyout_Opening(object? sender, object e) => FillHistoryList();

    private void FillHistoryList()
    {
        HistoryItems.Children.Clear();
        IReadOnlyList<SearchHistoryEntry> items = History?.Find ?? [];
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            SearchHistoryEntry entry = items[i];
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var choose = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new TextBlock
                {
                    Text = $"{KindName(entry.Conditions.Kind)}: {entry.Text}",
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FlowDirection = entry.Conditions.Kind == SearchKind.Text ? FlowDirection : FlowDirection.LeftToRight,
                },
            };
            AutomationProperties.SetAutomationId(choose, "Find_HistoryItem_" + index.ToString(CultureInfo.InvariantCulture));
            choose.Click += (_, _) =>
            {
                HistoryFlyout.Hide();
                Apply(HistoryList.Find, entry);
            };
            var delete = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
            AutomationProperties.SetAutomationId(delete, "Find_HistoryDelete_" + index.ToString(CultureInfo.InvariantCulture));
            AutomationProperties.SetName(delete, Loc.Format("Find_HistoryDelete_Name", entry.Text));
            ToolTipService.SetToolTip(delete, Loc.Get("Find_HistoryDelete_Tip"));
            delete.Click += (_, _) =>
            {
                History?.RemoveAt(HistoryList.Find, index);
                FillHistoryList();
            };
            Grid.SetColumn(delete, 1);
            row.Children.Add(choose);
            row.Children.Add(delete);
            HistoryItems.Children.Add(row);
        }
    }
}
