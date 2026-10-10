using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// ブックマークの編集 (INSP-24): 名前・開始オフセット・長さ・色・番号・コメント。変更は入力と同時に反映する (仕様 7)。
/// 範囲がドキュメントの外になる入力は赤枠と説明文で示し、反映しない。
/// </summary>
public sealed partial class BookmarkEditor : UserControl
{
    private readonly List<RadioButton> _colors = [];
    private BookmarkCollection? _bookmarks;
    private Bookmark? _bookmark;
    private EditorState? _editor;
    private bool _loading;

    public BookmarkEditor()
    {
        InitializeComponent();
        NumberChoice.Items.Add(new ComboBoxItem { Content = Loc.Get("Bookmark_NumberNone") });
        for (int i = 1; i <= 9; i++)
        {
            NumberChoice.Items.Add(new ComboBoxItem { Content = i.ToString(CultureInfo.InvariantCulture) });
        }

        AutomationProperties.SetName(CommentBox, Loc.Get("Bookmark_CommentLabel/Text"));
        AutomationProperties.SetName(Preview, Loc.Get("Bookmark_PreviewToggle/Content"));
        for (int i = 1; i <= BookmarkColor.PaletteSize; i++)
        {
            var swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1) };
            var radio = new RadioButton { Content = swatch, GroupName = "BookmarkColor", MinWidth = 0, Tag = i, Padding = new Thickness(4, 0, 0, 0) };
            AutomationProperties.SetAutomationId(radio, "Bookmark_Color" + i);
            AutomationProperties.SetName(radio, Loc.Format("Bookmarks_ColorNumber", i));
            ToolTipService.SetToolTip(radio, Loc.Format("Bookmarks_ColorNumber", i));
            radio.Checked += Color_Checked;
            _colors.Add(radio);
            ColorChoices.Children.Add(radio);
        }

        ActualThemeChanged += (_, _) => UpdateSwatches();
    }

    /// <summary>コメントのリンクを押した (リンク先。<c>#0x100</c> などのドキュメント内の位置か URL)。</summary>
    public event EventHandler<string>? LinkClicked;

    public Bookmark? Bookmark => _bookmark;

    /// <summary>ハイコントラストか (色見本の色の取り方)。</summary>
    public bool HighContrast { get; set; }

    /// <summary>編集するブックマークを設定する。</summary>
    public void Load(BookmarkCollection bookmarks, Bookmark bookmark, EditorState editor)
    {
        _loading = true;
        _bookmarks = bookmarks;
        _bookmark = bookmark;
        _editor = editor;
        NameBox.Text = bookmark.Name;
        StartBox.Text = "0x" + bookmark.Start.ToString("X", CultureInfo.InvariantCulture);
        RangeMode.SelectedIndex = 0;
        LengthBox.Header = Loc.Get("Bookmark_Length/Header");
        LengthBox.Text = bookmark.Length.ToString(CultureInfo.InvariantCulture);
        CommentBox.Text = bookmark.Comment;
        NumberChoice.SelectedIndex = bookmark.Number;
        LoadGroups();
        CustomToggle.IsChecked = bookmark.Color.IsCustom;
        Picker.Visibility = bookmark.Color.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        if (bookmark.Color.IsCustom)
        {
            Picker.Color = AnnotationBrushes.FromRgb(bookmark.Color.Rgb);
        }

        PreviewToggle.IsChecked = false;
        ShowPreview(false);
        UpdateSwatches();
        UpdateNameNote();
        UpdateRange(apply: false);
        UpdateRemaining();
        _loading = false;
    }

    /// <summary>名前の欄にフォーカスを置く (「名前を変更」)。</summary>
    public void FocusName()
    {
        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    private void UpdateSwatches()
    {
        bool loading = _loading;
        _loading = true;
        foreach (RadioButton radio in _colors)
        {
            var color = BookmarkColor.Palette((int)radio.Tag);
            var swatch = (Border)radio.Content;
            swatch.Background = AnnotationBrushes.Mark(color, this, HighContrast);
            swatch.BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
            radio.IsChecked = _bookmark?.Color == color;
        }

        _loading = loading;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _bookmark is null || _bookmarks is null)
        {
            return;
        }

        // 名前は 1〜256 文字。空にした間は前の名前のまま。
        if (NameBox.Text.Length > 0)
        {
            _bookmarks.Rename(_bookmark, NameBox.Text);
        }

        UpdateNameNote();
    }

    /// <summary><c>bm.名前</c> に使えない名前なら知らせる (エラーではない。INSP-24 の仕様 2)。</summary>
    private void UpdateNameNote()
    {
        string name = NameBox.Text;
        NameNote.Text = name.Length > 0 && !Bookmark.IsExpressionName(name) ? Loc.Get("Bookmark_NameNotUsable") : string.Empty;
        NameNote.Visibility = NameNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Range_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
        {
            UpdateRange(apply: true);
        }
    }

    /// <summary>開始と長さを入力式で読む。ドキュメントの外になる範囲は赤枠と説明文で示し、反映しない (INSP-24 の「エラー」)。</summary>
    private void UpdateRange(bool apply)
    {
        if (_editor is null || _bookmark is null || _bookmarks is null)
        {
            return;
        }

        var context = new EditorExpressionContext(_editor);
        bool startOk = ExpressionEvaluator.TryEvaluate(StartBox.Text, context, out long start, out _);
        bool byEnd = RangeMode.SelectedIndex == 1;
        bool lengthOk = ExpressionEvaluator.TryEvaluate(LengthBox.Text, context, out long length, out _, byEnd ? DefaultRadix.Hexadecimal : DefaultRadix.Decimal);
        if (byEnd && lengthOk && startOk)
        {
            // 終了は最後のバイトの位置 (含む)。
            length = length - start + 1;
        }

        long docLength = _editor.Document.Length;
        bool valid = startOk && lengthOk && start >= 0 && length >= 0 && start <= docLength && length <= docLength - start;
        SetError(StartBox, !startOk || start < 0 || start > docLength);
        SetError(LengthBox, !lengthOk || length < 0 || startOk && length > docLength - start);
        RangeInfo.Foreground = (Brush)Application.Current.Resources[valid ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush"];
        RangeInfo.Text = valid
            ? Loc.Format("Bookmark_RangeInfo", "0x" + start.ToString("X", CultureInfo.InvariantCulture),
                length > 0 ? "0x" + (start + length - 1).ToString("X", CultureInfo.InvariantCulture) : "—", length.ToString("N0", CultureInfo.CurrentCulture))
            : Loc.Format("Bookmark_RangeError", "0x" + docLength.ToString("X", CultureInfo.InvariantCulture));
        if (valid && apply)
        {
            _bookmarks.SetRange(_bookmark, start, length);
        }
    }

    /// <summary>「長さ」と「終了 (このバイトを含む)」を切り替える。今の範囲をその形で入れ直す。</summary>
    private void RangeMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bookmark is null || LengthBox is null)
        {
            return;
        }

        bool byEnd = RangeMode.SelectedIndex == 1;
        bool loading = _loading;
        _loading = true;
        LengthBox.Header = byEnd ? Loc.Get("Bookmark_EndHeader") : Loc.Get("Bookmark_Length/Header");
        LengthBox.Text = byEnd
            ? "0x" + (_bookmark.Start + Math.Max(1, _bookmark.Length) - 1).ToString("X", CultureInfo.InvariantCulture)
            : _bookmark.Length.ToString(CultureInfo.InvariantCulture);
        _loading = loading;
        UpdateRange(apply: false);
    }

    private static void SetError(TextBox box, bool error)
    {
        if (error)
        {
            box.BorderBrush = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }
        else
        {
            box.ClearValue(Control.BorderBrushProperty);
        }
    }

    private void Color_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading && sender is RadioButton { Tag: int index } && _bookmark is not null && _bookmarks is not null)
        {
            _bookmarks.SetColor(_bookmark, BookmarkColor.Palette(index));
            CustomToggle.IsChecked = false;
            Picker.Visibility = Visibility.Collapsed;
        }
    }

    private void CustomToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = CustomToggle.IsChecked == true;
        Picker.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && _bookmark is not null && _bookmarks is not null)
        {
            _bookmarks.SetColor(_bookmark, BookmarkColor.Custom(AnnotationBrushes.ToRgb(Picker.Color)));
            UpdateSwatches();
        }
    }

    private void Picker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (!_loading && CustomToggle.IsChecked == true && _bookmark is not null && _bookmarks is not null)
        {
            _bookmarks.SetColor(_bookmark, BookmarkColor.Custom(AnnotationBrushes.ToRgb(args.NewColor)));
            UpdateSwatches();
        }
    }

    private void NumberChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && _bookmark is not null && _bookmarks is not null && NumberChoice.SelectedIndex >= 0)
        {
            _bookmarks.SetNumber(_bookmark, NumberChoice.SelectedIndex);
        }
    }

    // ---- グループ (INSP-27) ----

    /// <summary>グループの欄の選択肢: 「グループなし」と、今あるグループのパス。</summary>
    private void LoadGroups()
    {
        GroupChoice.Items.Clear();
        GroupChoice.Items.Add(Loc.Get("Bookmark_GroupNone"));
        foreach (string path in _bookmarks!.Groups.Select(g => g.Path).Order(StringComparer.OrdinalIgnoreCase))
        {
            GroupChoice.Items.Add(path);
        }

        GroupChoice.SelectedIndex = _bookmark!.Group is { } group ? GroupChoice.Items.IndexOf(group) : 0;
        GroupError.Visibility = Visibility.Collapsed;
    }

    private void GroupChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && GroupChoice.SelectedIndex >= 0)
        {
            ApplyGroup(GroupChoice.SelectedIndex == 0 ? null : GroupChoice.SelectedItem as string);
        }
    }

    private void GroupChoice_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        string text = args.Text.Trim();
        args.Handled = true;
        ApplyGroup(text.Length == 0 || text == Loc.Get("Bookmark_GroupNone") ? null : text);
    }

    /// <summary>グループを付け替える (入力したパスのグループがなければ作る)。9 階層目なら説明文を出して変えない。</summary>
    internal void ApplyGroup(string? path)
    {
        if (_bookmark is null || _bookmarks is null)
        {
            return;
        }

        try
        {
            _bookmarks.SetGroup(_bookmark, path);
            GroupError.Visibility = Visibility.Collapsed;
        }
        catch (BookmarkGroupDepthException)
        {
            GroupError.Text = Loc.Format("Bookmarks_GroupTooDeep", BookmarkGroups.MaxDepth);
            GroupError.Visibility = Visibility.Visible;
        }
    }

    private void CommentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading && _bookmark is not null && _bookmarks is not null)
        {
            _bookmarks.SetComment(_bookmark, CommentBox.Text);
        }

        UpdateRemaining();
    }

    /// <summary>コメントの残りの文字数 (64 KB まで。INSP-24 の「エラー」)。</summary>
    private void UpdateRemaining() =>
        CommentRemaining.Text = Loc.Format("Bookmark_CommentRemaining",
            (BookmarkCollection.MaxCommentLength - CommentBox.Text.Length).ToString("N0", CultureInfo.CurrentCulture));

    private void PreviewToggle_Click(object sender, RoutedEventArgs e) => ShowPreview(PreviewToggle.IsChecked == true);

    /// <summary>コメントの編集とプレビューを切り替える (INSP-24 の仕様 6)。</summary>
    public void ShowPreview(bool preview)
    {
        PreviewToggle.IsChecked = preview;
        CommentBox.Visibility = preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewHost.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        if (preview)
        {
            MarkdownRenderer.Render(Preview, CommentBox.Text, url => LinkClicked?.Invoke(this, url));
        }
    }

    /// <summary>プレビューの文字列と太字の範囲 (テスト用)。</summary>
    internal IEnumerable<(string Text, bool Bold, bool Link)> PreviewRuns() => MarkdownRenderer.Runs(Preview);

    /// <summary>プレビューのリンクを押す (テスト用の命令の通り道。Invoke と同じ)。</summary>
    internal bool ClickLink(string text)
    {
        foreach (Microsoft.UI.Xaml.Documents.Block block in Preview.Blocks)
        {
            if (block is Microsoft.UI.Xaml.Documents.Paragraph p && FindLink(p.Inlines, text) is { } url)
            {
                LinkClicked?.Invoke(this, url);
                return true;
            }
        }

        return false;
    }

    private string? FindLink(Microsoft.UI.Xaml.Documents.InlineCollection inlines, string text)
    {
        foreach (Microsoft.UI.Xaml.Documents.Inline inline in inlines)
        {
            if (inline is Microsoft.UI.Xaml.Documents.Hyperlink link
                && string.Concat(link.Inlines.OfType<Microsoft.UI.Xaml.Documents.Run>().Select(r => r.Text)) == text)
            {
                return MarkdownLite.ParseInlines(CommentBox.Text).Concat(MarkdownLite.Parse(CommentBox.Text).SelectMany(b => b.Inlines))
                    .FirstOrDefault(i => i.Kind == MarkdownInlineKind.Link && i.PlainText == text)?.Url;
            }

            if (inline is Microsoft.UI.Xaml.Documents.Span span && FindLink(span.Inlines, text) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
