using System.Text.Json;
using HexEditor.App.Services;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Editing;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// 塗りつぶし・挿入の「内容」の部品 (EDIT-29 の仕様 2〜4。EDIT-14・EDIT-15 と共通)。種類の選択と、種類ごとの設定欄を持つ。
/// 入力は <see cref="TryGetSpec"/> で <see cref="FillSpec"/> にする。最後に使った種類と設定 (シードを除く) を設定に記憶する (仕様 7)。
/// </summary>
internal sealed class FillContentPanel : StackPanel
{
    private static readonly FillKind[] AllKinds =
        [FillKind.Byte, FillKind.HexPattern, FillKind.Text, FillKind.Random, FillKind.CryptoRandom, FillKind.Counter, FillKind.File, FillKind.Clipboard];

    private readonly FillKind[] _kinds;
    private readonly EditorState _editor;
    private readonly string _settingsKey;
    private readonly ComboBox _kind;
    private readonly TextBox _value, _pattern, _text, _min, _max, _seed, _start, _step, _filePath, _fileOffset, _fileLength;
    private readonly ComboBox _size, _overflow, _origin, _textEncoding;
    private readonly CheckBox _bigEndian, _signed, _repeat;
    private readonly TextBlock _error;
    private readonly Dictionary<FillKind, StackPanel> _sections = [];
    private readonly StackPanel _repeatSection;
    private readonly int _maxPatternLength;
    private readonly string _patternTooLongKey;
    private bool _loading;

    /// <param name="editor">文字コード・入力式の名前に使う。</param>
    /// <param name="settingsKey">記憶に使う設定のキー (塗りつぶしと挿入で別に覚える)。</param>
    /// <param name="allowRepeatOptions">繰り返しと起点の設定を出すか (塗りつぶしだけ)。</param>
    /// <param name="kinds">選べる種類 (null ならすべて。データ演算の鍵は Hex・テキスト・ファイル・クリップボード)。</param>
    /// <param name="maxPatternLength">Hex・テキストの長さの上限 (塗りつぶしは 1 MiB、データ演算の鍵は 16 MiB。EDIT-33 の仕様 2)。</param>
    /// <param name="patternTooLongKey">上限を超えたときの文言のリソースキー。</param>
    public FillContentPanel(EditorState editor, string settingsKey, bool allowRepeatOptions, IReadOnlyList<FillKind>? kinds = null,
        int maxPatternLength = FillSpec.MaxPatternLength, string patternTooLongKey = "Fill_Error_PatternTooLong")
    {
        _maxPatternLength = maxPatternLength;
        _patternTooLongKey = patternTooLongKey;
        _kinds = kinds is null ? AllKinds : [.. kinds];
        _editor = editor;
        _settingsKey = settingsKey;
        Spacing = 8;
        _kind = DialogParts.Combo("Fill_Kind", Loc.Get("Fill_Kind"), _kinds.Select(k => Loc.Get("Fill_Kind_" + k)), 0);
        Children.Add(_kind);

        _value = DialogParts.Field("Fill_Value", Loc.Get("Fill_Value"), "00");
        Section(FillKind.Byte, _value);

        _pattern = DialogParts.Field("Fill_Pattern", Loc.Get("Fill_Pattern"), "DE AD BE EF");
        Section(FillKind.HexPattern, _pattern);

        _text = DialogParts.Field("Fill_Text", Loc.Format("Fill_Text", editor.TextEncoding.Name), string.Empty, monospace: false);

        // テキストの文字コード: 既定は表示中の文字コード。表示の文字コードの一覧から選べる (仕様 2 の表)。
        _textEncoding = new ComboBox { Header = Loc.Get("Fill_TextEncoding"), MinWidth = 220 };
        AutomationProperties.SetAutomationId(_textEncoding, "Fill_TextEncoding");
        AutomationProperties.SetName(_textEncoding, Loc.Get("Fill_TextEncoding"));
        _textEncoding.Items.Add(new ComboBoxItem { Content = Loc.Format("Fill_TextEncodingDisplay", editor.TextEncoding.Name), Tag = DisplayEncoding });
        foreach (EncodingEntry entry in EncodingCatalog.All.Where(e => e.Selectable))
        {
            _textEncoding.Items.Add(new ComboBoxItem { Content = MainWindow.EncodingDisplayText(entry), Tag = entry.Id });
        }

        _textEncoding.SelectedIndex = 0;
        Section(FillKind.Text, _text, _textEncoding);

        _min = DialogParts.Field("Fill_RandomMin", Loc.Get("Fill_RandomMin"), "00");
        _max = DialogParts.Field("Fill_RandomMax", Loc.Get("Fill_RandomMax"), "FF");
        _seed = DialogParts.Field("Fill_Seed", Loc.Get("Fill_Seed"));
        _seed.PlaceholderText = Loc.Get("Fill_SeedPlaceholder");
        Section(FillKind.Random, _min, _max, _seed);

        Section(FillKind.CryptoRandom, new TextBlock { Text = Loc.Get("Fill_CryptoNote"), TextWrapping = TextWrapping.Wrap });

        _start = DialogParts.Field("Fill_CounterStart", Loc.Get("Fill_CounterStart"), "0");
        _step = DialogParts.Field("Fill_CounterStep", Loc.Get("Fill_CounterStep"), "1");
        _size = DialogParts.Combo("Fill_CounterSize", Loc.Get("Fill_CounterSize"), ["1", "2", "4", "8"], 0);
        _bigEndian = DialogParts.Check("Fill_CounterBigEndian", Loc.Get("Fill_CounterBigEndian"), false);
        _signed = DialogParts.Check("Fill_CounterSigned", Loc.Get("Fill_CounterSigned"), false);
        _overflow = DialogParts.Combo("Fill_CounterOverflow", Loc.Get("Fill_CounterOverflow"),
            [Loc.Get("Fill_CounterWrap"), Loc.Get("Fill_CounterSaturate")], 0);
        Section(FillKind.Counter, _start, _step, _size, _bigEndian, _signed, _overflow);

        _filePath = DialogParts.Field("Fill_FilePath", Loc.Get("Fill_FilePath"), string.Empty, monospace: false);
        _fileOffset = DialogParts.Field("Fill_FileOffset", Loc.Get("Fill_FileOffset"), "0");
        _fileLength = DialogParts.Field("Fill_FileLength", Loc.Get("Fill_FileLength"));
        _fileLength.PlaceholderText = Loc.Get("Fill_FileLengthPlaceholder");
        Section(FillKind.File, _filePath, _fileOffset, _fileLength);

        Section(FillKind.Clipboard, new TextBlock { Text = Loc.Get("Fill_ClipboardNote"), TextWrapping = TextWrapping.Wrap });

        _repeat = DialogParts.Check("Fill_Repeat", Loc.Get("Fill_Repeat"), true);
        _origin = DialogParts.Combo("Fill_Origin", Loc.Get("Fill_Origin"), [Loc.Get("Fill_OriginRange"), Loc.Get("Fill_OriginZero")], 0);
        _repeatSection = new StackPanel { Spacing = 8 };
        _repeatSection.Children.Add(_repeat);
        _repeatSection.Children.Add(_origin);
        if (allowRepeatOptions)
        {
            Children.Add(_repeatSection);
        }

        _error = DialogParts.Caption("Fill_Error");
        Children.Add(_error);

        Load();
        foreach (TextBox box in new[] { _value, _pattern, _text, _min, _max, _seed, _start, _step, _filePath, _fileOffset, _fileLength })
        {
            box.TextChanged += (_, _) => OnChanged();
        }

        foreach (ComboBox combo in new[] { _kind, _size, _overflow, _origin, _textEncoding })
        {
            combo.SelectionChanged += (_, _) => OnChanged();
        }

        foreach (CheckBox check in new[] { _bigEndian, _signed, _repeat })
        {
            check.Checked += (_, _) => OnChanged();
            check.Unchecked += (_, _) => OnChanged();
        }

        UpdateSections();
    }

    /// <summary>入力が変わった。</summary>
    public event EventHandler? Changed;

    public FillKind Kind => _kinds[Math.Max(0, _kind.SelectedIndex)];

    /// <summary>ファイルの内容を選んだ場合のパス (ダイアログがファイルを選ぶため)。</summary>
    public string FilePath
    {
        get => _filePath.Text;
        set => _filePath.Text = value;
    }

    /// <summary>
    /// 入力を内容の指定にする。誤りがあれば null で、欄を赤枠にして理由を示す。クリップボードの内容は実行するときに読むため、
    /// ここでは種類だけを返す。
    /// </summary>
    public FillSpec? TryGetSpec()
    {
        var context = new EditorExpressionContext(_editor);
        string? error = null;
        foreach (TextBox box in new[] { _value, _pattern, _text, _min, _max, _seed, _start, _step, _filePath, _fileOffset, _fileLength })
        {
            DialogParts.MarkInvalid(box, false);
        }

        long Eval(TextBox box, long min, long max, DefaultRadix radix = DefaultRadix.Hexadecimal)
        {
            if (!DialogParts.TryEvaluate(box.Text, context, out long v, out ExpressionException? e, radix))
            {
                error ??= DialogParts.ExpressionError(e!);
                DialogParts.MarkInvalid(box, true);
                return 0;
            }

            if (v < min || v > max)
            {
                error ??= Loc.Format("Fill_Error_Range", Core.View.StatusFormat.Hex(min), Core.View.StatusFormat.Hex(max));
                DialogParts.MarkInvalid(box, true);
            }

            return v;
        }

        var spec = new FillSpec
        {
            Kind = Kind,
            Repeat = _repeat.IsChecked == true,
            Origin = _origin.SelectedIndex == 1 ? PatternOrigin.OffsetZero : PatternOrigin.RangeStart,
        };
        switch (Kind)
        {
            case FillKind.Byte:
                spec = spec with { Value = (byte)Eval(_value, 0, 255) };
                break;
            case FillKind.HexPattern:
                byte[]? bytes = HexText.TryParse(_pattern.Text);
                if (bytes is not { Length: > 0 })
                {
                    error ??= Loc.Get(string.IsNullOrWhiteSpace(_pattern.Text) ? "Fill_Error_EmptyPattern" : "Fill_Error_HexPattern");
                    DialogParts.MarkInvalid(_pattern, true);
                }
                else if (bytes.Length > _maxPatternLength)
                {
                    error ??= Loc.Get(_patternTooLongKey);
                    DialogParts.MarkInvalid(_pattern, true);
                }

                spec = spec with { Pattern = bytes };
                break;
            case FillKind.Text:
                string text = FillText.Unescape(_text.Text);
                TextEncoding encoding = SelectedTextEncoding;
                if (text.Length == 0)
                {
                    error ??= Loc.Get("Fill_Error_EmptyPattern");
                    DialogParts.MarkInvalid(_text, true);
                }
                else if (!encoding.TryEncode(text, out byte[] encoded))
                {
                    error ??= Loc.Format("Notice_NotEncodableChar", encoding.FirstUnencodable(text) ?? text, encoding.Name);
                    DialogParts.MarkInvalid(_text, true);
                }
                else if (encoded.Length > _maxPatternLength)
                {
                    error ??= Loc.Get(_patternTooLongKey);
                    DialogParts.MarkInvalid(_text, true);
                }
                else
                {
                    spec = spec with { Pattern = encoded };
                }

                break;
            case FillKind.Random:
                long min = Eval(_min, 0, 255), max = Eval(_max, 0, 255);
                ulong? seed = null;
                if (!string.IsNullOrWhiteSpace(_seed.Text))
                {
                    seed = unchecked((ulong)Eval(_seed, long.MinValue, long.MaxValue, DefaultRadix.Decimal));
                }

                if (error is null && min > max)
                {
                    error = Loc.Get("Fill_Error_MinMax");
                    DialogParts.MarkInvalid(_max, true);
                }

                spec = spec with { RandomMin = (byte)min, RandomMax = (byte)max, Seed = seed };
                break;
            case FillKind.Counter:
                spec = spec with
                {
                    CounterStart = Eval(_start, long.MinValue, long.MaxValue),
                    CounterStep = Eval(_step, long.MinValue, long.MaxValue),
                    CounterSize = 1 << Math.Max(0, _size.SelectedIndex),
                    CounterBigEndian = _bigEndian.IsChecked == true,
                    CounterSigned = _signed.IsChecked == true,
                    CounterOverflow = _overflow.SelectedIndex == 1 ? CounterOverflow.Saturate : CounterOverflow.Wrap,
                };
                break;
            case FillKind.File:
                if (string.IsNullOrWhiteSpace(_filePath.Text))
                {
                    error ??= Loc.Get("Fill_Error_NoFile");
                    DialogParts.MarkInvalid(_filePath, true);
                }

                long offset = Eval(_fileOffset, 0, long.MaxValue);
                long? length = string.IsNullOrWhiteSpace(_fileLength.Text) ? null : Eval(_fileLength, 0, long.MaxValue);
                spec = spec with { FilePath = _filePath.Text.Trim().Trim('"'), FileOffset = offset, FileLength = length };
                break;
        }

        _error.Text = error ?? string.Empty;
        return error is null ? spec : null;
    }

    /// <summary>最後に使った種類と設定を記憶する (シードは記憶しない。仕様 7)。</summary>
    public void Save()
    {
        var state = new Dictionary<string, string>
        {
            ["kind"] = Kind.ToString(),
            ["value"] = _value.Text,
            ["pattern"] = _pattern.Text,
            ["text"] = _text.Text,
            ["min"] = _min.Text,
            ["max"] = _max.Text,
            ["start"] = _start.Text,
            ["step"] = _step.Text,
            ["size"] = _size.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["bigEndian"] = (_bigEndian.IsChecked == true).ToString(),
            ["signed"] = (_signed.IsChecked == true).ToString(),
            ["overflow"] = _overflow.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["repeat"] = (_repeat.IsChecked == true).ToString(),
            ["origin"] = _origin.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["textEncoding"] = SelectedTextEncodingId,
        };
        AppState.SetString(_settingsKey, JsonSerializer.Serialize(state));
    }

    private void Load()
    {
        string json = AppState.GetString(_settingsKey, string.Empty);
        if (json.Length == 0)
        {
            return;
        }

        try
        {
            _loading = true;
            Dictionary<string, string> state = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
            string Get(string key, string fallback) => state.TryGetValue(key, out string? v) ? v : fallback;
            int Index(string key) => int.TryParse(Get(key, "0"), out int i) ? i : 0;
            _kind.SelectedIndex = Math.Max(0, Array.IndexOf(_kinds, Enum.TryParse(Get("kind", "Byte"), out FillKind k) ? k : _kinds[0]));
            _value.Text = Get("value", _value.Text);
            _pattern.Text = Get("pattern", _pattern.Text);
            _text.Text = Get("text", _text.Text);
            _min.Text = Get("min", _min.Text);
            _max.Text = Get("max", _max.Text);
            _start.Text = Get("start", _start.Text);
            _step.Text = Get("step", _step.Text);
            _size.SelectedIndex = Math.Clamp(Index("size"), 0, 3);
            _bigEndian.IsChecked = Get("bigEndian", "False") == "True";
            _signed.IsChecked = Get("signed", "False") == "True";
            _overflow.SelectedIndex = Math.Clamp(Index("overflow"), 0, 1);
            _repeat.IsChecked = Get("repeat", "True") == "True";
            _origin.SelectedIndex = Math.Clamp(Index("origin"), 0, 1);
            string encodingId = Get("textEncoding", DisplayEncoding);
            _textEncoding.SelectedItem = _textEncoding.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals((string)i.Tag, encodingId, StringComparison.OrdinalIgnoreCase)) ?? _textEncoding.Items[0];
        }
        catch (JsonException)
        {
        }
        finally
        {
            _loading = false;
        }
    }

    private void Section(FillKind kind, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (UIElement child in children)
        {
            panel.Children.Add(child);
        }

        _sections[kind] = panel;
        Children.Add(panel);
    }

    /// <summary>テキストの文字コードの「表示中の文字コード」。</summary>
    private const string DisplayEncoding = "display";

    /// <summary>選んでいるテキストの文字コードの名前 (表示中の文字コードなら <c>display</c>)。</summary>
    private string SelectedTextEncodingId => _textEncoding.SelectedItem is ComboBoxItem { Tag: string id } ? id : DisplayEncoding;

    /// <summary>テキストの内容を符号化する文字コード。</summary>
    private TextEncoding SelectedTextEncoding =>
        SelectedTextEncodingId == DisplayEncoding ? _editor.TextEncoding : TextEncoding.FromId(SelectedTextEncodingId);

    private void UpdateSections()
    {
        _text.Header = Loc.Format("Fill_Text", SelectedTextEncoding.Name);
        foreach ((FillKind kind, StackPanel panel) in _sections)
        {
            panel.Visibility = kind == Kind ? Visibility.Visible : Visibility.Collapsed;
        }

        bool repeatable = Kind is FillKind.HexPattern or FillKind.Text or FillKind.File or FillKind.Clipboard;
        _repeatSection.Visibility = repeatable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnChanged()
    {
        if (_loading)
        {
            return;
        }

        UpdateSections();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
