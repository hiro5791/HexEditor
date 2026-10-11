using System.Globalization;
using System.Text;
using HexEditor.App.Services;
using HexEditor.Core.Expressions;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// 「文字列の抽出」パネル (FIND-32 の画面)。上部に設定項目 (最小の長さ、文字コード、文字の種類、NUL で終わるものだけ、範囲) と「抽出」
/// ボタン、下に共通の結果一覧。抽出は長時間処理として行い、結果は見つかった順に一覧に出る (ストリーミング)。
/// </summary>
public sealed partial class StringsPanel : UserControl
{
    /// <summary>既定の文字コード (仕様 1: ASCII と UTF-16LE)。</summary>
    internal static readonly string[] DefaultEncodings = ["ascii", "utf-16le"];

    private readonly TextBox _minLength = new() { Width = 80, Text = StringExtractionOptions.DefaultMinLength.ToString(CultureInfo.InvariantCulture) };
    private readonly DropDownButton _encodingsButton = new();
    private readonly StackPanel _encodingList = new();
    private readonly ComboBox _charKind = new() { SelectedIndex = -1 };
    private readonly CheckBox _nulOnly = new();
    private readonly ComboBox _scope = new();
    private readonly TextBox _rangeStart = new() { Width = 120, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBox _rangeEnd = new() { Width = 120, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FlowDirection = FlowDirection.LeftToRight };
    private readonly Button _extract = new();
    private readonly TextBlock _error = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private List<string> _encodings = [.. DefaultEncodings];
    private CancellationTokenSource? _running;

    public StringsPanel()
    {
        Results = new SearchResultsPanel();
        AutomationProperties.SetAutomationId(Results, "Strings_Results");

        var settings = new WrapPanel();
        AddLabeled(settings, "Strings_MinLength", _minLength);
        _minLength.TextChanged += (_, _) => Validate();

        _encodingsButton.Flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 320, Content = _encodingList } };
        _encodingsButton.Flyout.Opening += (_, _) => FillEncodings();
        AddLabeled(settings, "Strings_Encodings", _encodingsButton);

        _charKind.Items.Add(new ComboBoxItem { Content = Loc.Get("Strings_CharKind_Printable") });
        _charKind.Items.Add(new ComboBoxItem { Content = Loc.Get("Strings_CharKind_Newlines") });
        _charKind.SelectedIndex = 0;
        AddLabeled(settings, "Strings_CharKind", _charKind);

        _nulOnly.Content = Loc.Get("Strings_NulOnly");
        AutomationProperties.SetAutomationId(_nulOnly, "Strings_NulOnly");
        settings.Children.Add(_nulOnly);

        _scope.Items.Add(new ComboBoxItem { Content = Loc.Get("Strings_Scope_Document") });
        _scope.Items.Add(new ComboBoxItem { Content = Loc.Get("Strings_Scope_Selection") });
        _scope.Items.Add(new ComboBoxItem { Content = Loc.Get("Strings_Scope_Range") });
        _scope.SelectedIndex = 0;
        _scope.SelectionChanged += (_, _) => Validate();
        AddLabeled(settings, "Strings_Scope", _scope);
        AddLabeled(settings, "Strings_RangeStart", _rangeStart);
        AddLabeled(settings, "Strings_RangeEnd", _rangeEnd);
        _rangeStart.TextChanged += (_, _) => Validate();
        _rangeEnd.TextChanged += (_, _) => Validate();

        _extract.Content = Loc.Get("Strings_Extract");
        _extract.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AutomationProperties.SetAutomationId(_extract, "Strings_Extract");
        _extract.Click += async (_, _) => await ExtractAsync();
        settings.Children.Add(_extract);
        AutomationProperties.SetAutomationId(_error, "Strings_Error");
        _error.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        settings.Children.Add(_error);

        var root = new Grid { RowSpacing = 4, Padding = new Thickness(0, 6, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var settingsHost = new Border { Padding = new Thickness(12, 0, 12, 0), Child = settings };
        root.Children.Add(settingsHost);
        Grid.SetRow(Results, 1);
        root.Children.Add(Results);
        Content = root;
        Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        UpdateEncodingsButton();
        Validate();
    }

    /// <summary>結果一覧 (共通の結果一覧。00-overview 9 章)。</summary>
    public SearchResultsPanel Results { get; }

    /// <summary>抽出の対象のビュー (選択中のタブ)。</summary>
    public Func<EditorState?>? EditorSource { get; set; }

    /// <summary>抽出中か。</summary>
    public bool IsRunning => _running is not null;

    /// <summary>テスト用: 文字コードを選ぶ。</summary>
    internal void SetEncodings(IReadOnlyList<string> ids)
    {
        _encodings = [.. ids];
        UpdateEncodingsButton();
    }

    /// <summary>テスト用: 設定 (最小の長さ、NUL で終わるものだけ、改行を含める)。</summary>
    internal void SetOptions(int? minLength, bool? nulOnly, bool? newlines)
    {
        if (minLength is int n)
        {
            _minLength.Text = n.ToString(CultureInfo.InvariantCulture);
        }

        if (nulOnly is bool b)
        {
            _nulOnly.IsChecked = b;
        }

        if (newlines is bool l)
        {
            _charKind.SelectedIndex = l ? 1 : 0;
        }
    }

    private static void AddLabeled(Panel panel, string key, FrameworkElement control)
    {
        string name = Loc.Get(key);
        AutomationProperties.SetAutomationId(control, key);
        AutomationProperties.SetName(control, name);
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(control);
        panel.Children.Add(stack);
    }

    private void FillEncodings()
    {
        _encodingList.Children.Clear();
        foreach (EncodingEntry entry in TermRow.Encodings)
        {
            var box = new CheckBox { Content = MainWindow.EncodingDisplayText(entry), Tag = entry.Id, IsChecked = _encodings.Contains(entry.Id) };
            AutomationProperties.SetAutomationId(box, "Strings_Encoding_" + entry.Id);
            box.Checked += (_, _) => Toggle(entry.Id, true);
            box.Unchecked += (_, _) => Toggle(entry.Id, false);
            _encodingList.Children.Add(box);
        }
    }

    private void Toggle(string id, bool on)
    {
        _encodings.Remove(id);
        if (on)
        {
            _encodings.Add(id);
        }

        UpdateEncodingsButton();
        Validate();
    }

    private void UpdateEncodingsButton()
    {
        string names = string.Join(", ", _encodings.Select(id => EncodingCatalog.Find(id) is { } e ? e.Label : id));
        _encodingsButton.Content = names.Length == 0 ? Loc.Get("Strings_NoEncoding") : names;
    }

    /// <summary>設定を読む。誤りがあれば説明文を出して null。</summary>
    private (StringExtractionOptions Options, SearchScope Scope)? Read(EditorState? editor, bool show)
    {
        string? error = null;
        if (!int.TryParse(_minLength.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int min) || min < 1 || min > StringExtractionOptions.MaxMinLength)
        {
            error = Loc.Format("Strings_MinLengthInvalid", StringExtractionOptions.MaxMinLength);
        }

        var encodings = new List<(string, Encoding)>();
        foreach (string id in _encodings)
        {
            if (TextEncodings.FromCatalogId(id) is { } e)
            {
                encodings.Add((EncodingCatalog.Find(id) is { } entry ? entry.Label : id, e));
            }
        }

        if (encodings.Count == 0)
        {
            error ??= Loc.Get("Strings_NoEncoding");
        }

        SearchScope scope = SearchScope.WholeDocument;
        _rangeStart.Visibility = _rangeEnd.Visibility = _scope.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (editor is not null && _scope.SelectedIndex == 1)
        {
            scope = editor.HasSelection ? SearchScope.Of(editor.SelectionStart, editor.SelectionLength) : SearchScope.WholeDocument;
            if (!editor.HasSelection)
            {
                error ??= Loc.Get("Strings_NoSelection");
            }
        }
        else if (editor is not null && _scope.SelectedIndex == 2)
        {
            var context = new EditorExpressionContext(editor);
            if (ExpressionEvaluator.TryEvaluate(_rangeStart.Text, context, out long start, out _)
                && ExpressionEvaluator.TryEvaluate(_rangeEnd.Text, context, out long end, out _)
                && start >= 0 && start <= end && end < editor.Document.Length)
            {
                scope = SearchScope.Of([SearchRange.FromInclusive(start, end)]);
            }
            else
            {
                error ??= Loc.Get("Find_RangeInvalid");
            }
        }

        if (show)
        {
            _error.Text = error ?? string.Empty;
        }

        return error is null
            ? (new StringExtractionOptions
            {
                MinLength = min,
                Encodings = encodings,
                IncludeNewlines = _charKind.SelectedIndex == 1,
                NulTerminatedOnly = _nulOnly.IsChecked == true,
            }, scope)
            : null;
    }

    private void Validate()
    {
        bool ok = Read(EditorSource?.Invoke(), show: true) is not null;
        _extract.IsEnabled = ok;
    }

    /// <summary>「抽出」(FIND-32)。結果は共通の結果一覧に見つかった順に出す。キャンセルすると、それまでの結果を残す。</summary>
    public async Task ExtractAsync()
    {
        if (EditorSource?.Invoke() is not { } editor || Read(editor, show: true) is not { } read)
        {
            return;
        }

        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        int limit = Math.Clamp(App.Settings?.GetInt(FindBar.FindAllLimitKey, 1_000_000) ?? 1_000_000, 1_000, 100_000_000);
        var options = new SearchOptions { Scope = read.Scope, ChunkSize = FindBar.ChunkSizeSetting, MaxMatches = limit };
        SearchResults results = StringExtractor.CreateResults(editor.Document.Current, read.Options, options);
        string summary = string.Join(", ", read.Options.Encodings.Select(e => e.Name));
        AppLog.Info($"Strings: start ({summary}, min {read.Options.MinLength})");
        try
        {
            await Results.RunAsync([new SearchTarget(editor, string.Empty, results)], Loc.Get("Strings_KindName"), summary, Encoding.ASCII, cts);
        }
        finally
        {
            if (_running == cts)
            {
                _running = null;
            }
        }
    }

    /// <summary>抽出を取り消す。</summary>
    public void Cancel() => Results.CancelRunning();
}
