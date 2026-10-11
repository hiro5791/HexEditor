using HexEditor.App.Services;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// 検索できる文字コードのドロップダウン (EDIT-38 の「画面」)。入力した文字列を名前・ID・コードページ番号などに含む文字コードだけを
/// 候補に出す (絞り込みは <see cref="EncodingCatalog.Matches"/>。表示文字コードの選択の絞り込みと同じ規則)。
/// 候補を選ぶ (クリック、↑↓ と Enter) と選択が変わる。↓ / F4 / Alt+↓ で全件の一覧を開く。フォーカスを外すと、欄には選択中の文字コードの名前に戻る。
/// </summary>
internal sealed class EncodingPicker : UserControl
{
    // AutoSuggestBox は派生できないため包む。
    private readonly AutoSuggestBox _box = new();
    private readonly IReadOnlyList<EncodingEntry> _entries;
    private EncodingEntry _selected;

    /// <param name="entries">選べる文字コード (空でないこと)。</param>
    /// <param name="selectedId">最初に選ぶ文字コードの ID (一覧になければ先頭)。</param>
    public EncodingPicker(string automationId, string header, IReadOnlyList<EncodingEntry> entries, string selectedId)
    {
        if (entries.Count == 0)
        {
            throw new ArgumentException("文字コードの一覧が空です。", nameof(entries));
        }

        _entries = entries;
        _selected = Find(selectedId) ?? entries[0];
        _box.Header = header;
        _box.MinWidth = 320;
        _box.PlaceholderText = Loc.Get("EncodingPicker_Placeholder");
        _box.QueryIcon = new SymbolIcon(Symbol.Find);
        AutomationProperties.SetAutomationId(_box, automationId);
        AutomationProperties.SetName(_box, header);
        AutomationProperties.SetHelpText(_box, Loc.Get("EncodingPicker_Placeholder"));
        _box.Text = DisplayText(_selected);

        _box.TextChanged += (_, e) =>
        {
            if (e.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ShowCandidates(_box.Text);
            }
        };
        _box.SuggestionChosen += (_, e) =>
        {
            if (e.SelectedItem is Candidate c)
            {
                Select(c.Entry);
            }
        };
        _box.QuerySubmitted += (_, e) =>
        {
            // 候補を選ばずに Enter を押したら、絞り込んだ最初の候補を選ぶ。
            EncodingEntry? entry = (e.ChosenSuggestion as Candidate)?.Entry ?? Filter(e.QueryText).FirstOrDefault();
            if (entry is not null)
            {
                Select(entry);
            }

            _box.Text = DisplayText(_selected);
            _box.IsSuggestionListOpen = false;
        };
        _box.PreviewKeyDown += OnPreviewKeyDown;
        _box.LostFocus += (_, _) =>
        {
            // 入力の途中で離れたら、選択中の文字コードの名前に戻す。
            if (!_box.IsSuggestionListOpen)
            {
                _box.Text = DisplayText(_selected);
            }
        };

        Content = _box;
    }

    /// <summary>選択が変わった。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>選択中の文字コード。</summary>
    public EncodingEntry Selected => _selected;

    /// <summary>入力した文字列に合う文字コード (一覧の順)。</summary>
    public IEnumerable<EncodingEntry> Filter(string? query) =>
        _entries.Where(e => EncodingCatalog.Matches(e, DisplayText(e), query));

    /// <summary>文字コードを選ぶ (ID で。テストと設定の復元用)。一覧になければ false。</summary>
    public bool SelectById(string id)
    {
        if (Find(id) is not { } entry)
        {
            return false;
        }

        Select(entry);
        _box.Text = DisplayText(entry);
        return true;
    }

    private EncodingEntry? Find(string id) =>
        _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string DisplayText(EncodingEntry entry) => MainWindow.EncodingDisplayText(entry);

    private void Select(EncodingEntry entry)
    {
        if (ReferenceEquals(entry, _selected))
        {
            return;
        }

        _selected = entry;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowCandidates(string? query)
    {
        _box.ItemsSource = Filter(query).Select(e => new Candidate(e, DisplayText(e))).ToList();
        _box.IsSuggestionListOpen = true;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // ↓ / F4 / Alt+↓: 一覧が閉じていれば全件を開く (ComboBox と同じ操作)。
        if (!_box.IsSuggestionListOpen && e.Key is VirtualKey.Down or VirtualKey.F4)
        {
            ShowCandidates(null);
            e.Handled = true;
        }
        else if (_box.IsSuggestionListOpen && e.Key == VirtualKey.Escape)
        {
            _box.IsSuggestionListOpen = false;
            _box.Text = DisplayText(_selected);
            e.Handled = true;
        }
    }

    /// <summary>候補の項目 (一覧と欄には <see cref="ToString"/> の名前を出す)。</summary>
    private sealed record Candidate(EncodingEntry Entry, string Text)
    {
        public override string ToString() => Text;
    }
}
