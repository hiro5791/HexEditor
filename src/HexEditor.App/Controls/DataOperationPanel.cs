using System.Globalization;
using System.Text.Json;
using HexEditor.App.Services;
using HexEditor.Core.Editing;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace HexEditor.App.Controls;

/// <summary>データ演算ダイアログで決めた内容 (演算の設定と対象範囲)。鍵の内容 (ファイル・クリップボード) は実行するときに読む。</summary>
internal sealed record DataOperationRequest(DataOperationSpec Spec, IReadOnlyList<TargetRange> Ranges, FillSpec? KeySource);

/// <summary>
/// データ演算ダイアログの中身 (EDIT-31〜EDIT-37 の「画面」)。上部に演算の種類 (分類と演算のドロップダウン)、中央に共通の設定と演算ごとの設定、
/// 下部にプレビュー。入力は <see cref="TryGetRequest"/> で <see cref="DataOperationRequest"/> にする。最後に使った設定を記憶する。
/// </summary>
internal sealed class DataOperationPanel : StackPanel
{
    private const string StateKey = "data.operation.dialog";

    private static readonly DataOperationCategory[] Categories = Enum.GetValues<DataOperationCategory>();

    private readonly EditorState _editor;
    private readonly Document _document;
    private readonly IReadOnlyList<TargetRange> _selection;
    private readonly ComboBox _category, _kind, _type, _size, _endian, _overflow, _operandSource, _keyOrigin, _scope, _bitIndex, _bitFill, _bitOrder, _bitScope;
    private readonly RadioButton _targetSelection, _targetWhole, _targetRange;
    private readonly TextBox _start, _length, _operand, _increment, _process, _skip, _keyIncrement, _shiftBits, _rotateIncrement, _min, _max,
        _bitOffset, _bitCount;
    private readonly FillContentPanel _key;
    private readonly StackPanel _common, _arithmetic, _bitwise, _keyPanel, _shift, _clamp, _bits, _rangeFields, _numberOperand;
    private readonly TextBlock _error, _note, _xorNote, _previewBefore, _previewAfter, _previewMarks, _valuesBefore, _valuesAfter;
    private readonly List<DataOperationKind> _kinds = [];
    private bool _loading;

    /// <param name="selection">選択範囲 (マルチ選択・矩形選択を含む。空なら選択なし)。</param>
    /// <param name="initialKind">最初に選ぶ演算 (コマンドパレットの「データ演算: &lt;演算名&gt;」)。null なら前回の演算。</param>
    /// <param name="documentBigEndian">ドキュメントのエンディアン (VIEW-11。エンディアンの既定。EDIT-31 の仕様 4)。</param>
    public DataOperationPanel(EditorState editor, IReadOnlyList<TargetRange> selection, DataOperationKind? initialKind, bool documentBigEndian)
    {
        _editor = editor;
        _document = editor.Document;
        _selection = selection;
        Spacing = 8;
        MinWidth = 460;

        // ---- 演算の種類 ----
        _category = DialogParts.Combo("DataOp_Category", Loc.Get("DataOp_Category"), Categories.Select(c => Loc.Get("DataOp_Category_" + c)), 0);
        _kind = DialogParts.Combo("DataOp_Kind", Loc.Get("DataOp_Kind"), [], -1);
        var kindRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        kindRow.Children.Add(_category);
        kindRow.Children.Add(_kind);
        Children.Add(kindRow);

        // ---- 共通の設定 (対象範囲、型、大きさ、エンディアン、オペランド、増分、処理と飛ばし) ----
        bool hasSelection = selection.Count > 0;
        _targetSelection = DialogParts.Radio("DataOp_TargetSelection",
            selection.Count > 1 ? Loc.Format("DataOp_TargetMultiSelection", selection.Count) : Loc.Get("DataOp_TargetSelection"), "DataOpTarget", hasSelection);
        _targetSelection.IsEnabled = hasSelection;
        _targetWhole = DialogParts.Radio("DataOp_TargetWhole", Loc.Get("DataOp_TargetWhole"), "DataOpTarget", !hasSelection);
        _targetRange = DialogParts.Radio("DataOp_TargetRange", Loc.Get("DataOp_TargetRange"), "DataOpTarget", false);
        long start0 = hasSelection ? selection[0].Offset : editor.Cursor;
        long length0 = hasSelection ? selection[0].Length : Math.Max(0, _document.Length - start0);
        _start = DialogParts.Field("DataOp_RangeStart", Loc.Get("DataOp_RangeStart"), RangeSelectionModel.Format(start0));
        _length = DialogParts.Field("DataOp_RangeLength", Loc.Get("DataOp_RangeLength"), RangeSelectionModel.Format(length0));
        _rangeFields = Row(_start, _length);
        var target = new StackPanel { Spacing = 4 };
        target.Children.Add(new TextBlock { Text = Loc.Get("DataOp_Target"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        target.Children.Add(_targetSelection);
        target.Children.Add(_targetWhole);
        target.Children.Add(_targetRange);
        target.Children.Add(_rangeFields);

        _type = DialogParts.Combo("DataOp_Type", Loc.Get("DataOp_Type"), Enum.GetValues<ElementType>().Select(t => Loc.Get("DataOp_Type_" + t)), 0);
        _size = DialogParts.Combo("DataOp_Size", Loc.Get("DataOp_Size"), ["1", "2", "4", "8"], 0);
        _endian = DialogParts.Combo("DataOp_Endian", Loc.Get("DataOp_Endian"),
            [Loc.Get("DataOp_LittleEndian"), Loc.Get("DataOp_BigEndian")], documentBigEndian ? 1 : 0);
        _type.MinWidth = _size.MinWidth = _endian.MinWidth = 140;
        _operand = DialogParts.Field("DataOp_Operand", Loc.Get("DataOp_Operand"), "1");
        _increment = DialogParts.Field("DataOp_Increment", Loc.Get("DataOp_Increment"), "0");
        _process = DialogParts.Field("DataOp_Process", Loc.Get("DataOp_Process"), "1");
        _skip = DialogParts.Field("DataOp_Skip", Loc.Get("DataOp_Skip"), "0");
        _process.MinWidth = _skip.MinWidth = 140;
        _scope = DialogParts.Combo("DataOp_IncrementScope", Loc.Get("DataOp_IncrementScope"),
            [Loc.Get("DataOp_IncrementScope_PerRange"), Loc.Get("DataOp_IncrementScope_Continuous")], 0);
        _scope.Visibility = selection.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _numberOperand = Row(_operand, _increment);
        _common = new StackPanel { Spacing = 8 };
        _common.Children.Add(target);
        _common.Children.Add(Row(_type, _size, _endian));
        _common.Children.Add(_numberOperand);
        _common.Children.Add(Row(_process, _skip));
        _common.Children.Add(_scope);
        Children.Add(_common);

        // ---- 算術 (EDIT-32) ----
        _overflow = DialogParts.Combo("DataOp_Overflow", Loc.Get("DataOp_Overflow"), [Loc.Get("DataOp_Overflow_Wrap"), Loc.Get("DataOp_Overflow_Saturate")], 0);
        _arithmetic = Section(_overflow);

        // ---- ビット演算 (EDIT-33) ----
        _operandSource = DialogParts.Combo("DataOp_OperandSource", Loc.Get("DataOp_OperandSource"),
            [Loc.Get("DataOp_OperandSource_Number"), Loc.Get("DataOp_OperandSource_Key")], 0);
        _key = new FillContentPanel(editor, "data.operation.key", allowRepeatOptions: false,
            [FillKind.HexPattern, FillKind.Text, FillKind.File, FillKind.Clipboard]);
        _keyOrigin = DialogParts.Combo("DataOp_KeyOrigin", Loc.Get("DataOp_KeyOrigin"), [Loc.Get("Fill_OriginRange"), Loc.Get("Fill_OriginZero")], 0);
        _keyIncrement = DialogParts.Field("DataOp_KeyIncrement", Loc.Get("DataOp_KeyIncrement"), "0");
        _keyPanel = Stack(_key, Row(_keyOrigin, _keyIncrement));
        _xorNote = DialogParts.Caption("DataOp_XorNote");
        _xorNote.Text = Loc.Get("DataOp_XorNote");
        _bitwise = Section(_operandSource, _keyPanel, _xorNote);

        // ---- シフト・ローテート (EDIT-34) ----
        _shiftBits = DialogParts.Field("DataOp_ShiftBits", Loc.Get("DataOp_ShiftBits"), "1");
        _rotateIncrement = DialogParts.Field("DataOp_RotateIncrement", Loc.Get("DataOp_RotateIncrement"), "0");
        _shift = Section(Row(_shiftBits, _rotateIncrement));

        // ---- 制限 (EDIT-36) ----
        _min = DialogParts.Field("DataOp_Min", Loc.Get("DataOp_Min"));
        _max = DialogParts.Field("DataOp_Max", Loc.Get("DataOp_Max"));
        _min.PlaceholderText = _max.PlaceholderText = Loc.Get("DataOp_NoLimit");
        _clamp = Section(Row(_min, _max));

        // ---- ビットの挿入・削除 (EDIT-37) ----
        _bitOffset = DialogParts.Field("DataOp_BitOffset", Loc.Get("DataOp_BitOffset"), RangeSelectionModel.Format(hasSelection ? selection[0].Offset : editor.Cursor));
        _bitIndex = DialogParts.Combo("DataOp_BitIndex", Loc.Get("DataOp_BitIndex"), Enumerable.Range(0, 8).Select(i => i.ToString(CultureInfo.CurrentCulture)), 7);
        _bitCount = DialogParts.Field("DataOp_BitCount", Loc.Get("DataOp_BitCount"), "1");
        _bitFill = DialogParts.Combo("DataOp_BitFill", Loc.Get("DataOp_BitFill"), ["0", "1"], 0);
        _bitOrder = DialogParts.Combo("DataOp_BitOrder", Loc.Get("DataOp_BitOrder"), [Loc.Get("DataOp_BitOrder_Msb"), Loc.Get("DataOp_BitOrder_Lsb")], 0);
        _bitScope = DialogParts.Combo("DataOp_BitScope", Loc.Get("DataOp_BitScope"),
            [Loc.Get("DataOp_BitScope_Selection"), Loc.Get("DataOp_BitScope_ToEnd")], hasSelection ? 0 : 1);
        _bits = Section(Row(_bitOffset, _bitIndex), Row(_bitCount, _bitFill), Row(_bitOrder, _bitScope));

        // ---- 誤り・注記・プレビュー (EDIT-31 の仕様 8・10) ----
        _error = DialogParts.Caption("DataOp_Error");
        _error.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        _note = DialogParts.Caption("DataOp_Note");
        _previewBefore = DialogParts.Caption("DataOp_PreviewBefore", monospace: true);
        _previewAfter = DialogParts.Caption("DataOp_PreviewAfter", monospace: true);
        _previewMarks = DialogParts.Caption("DataOp_PreviewMarks", monospace: true);
        _valuesBefore = DialogParts.Caption("DataOp_ValuesBefore", monospace: true);
        _valuesAfter = DialogParts.Caption("DataOp_ValuesAfter", monospace: true);
        foreach (TextBlock t in new[] { _previewBefore, _previewAfter, _previewMarks })
        {
            t.TextWrapping = TextWrapping.NoWrap;
        }

        var preview = new StackPanel { Spacing = 2 };
        preview.Children.Add(new TextBlock { Text = Loc.Get("DataOp_Preview"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        preview.Children.Add(new ScrollViewer
        {
            Content = Stack(_previewBefore, _previewAfter, _previewMarks),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
        });
        preview.Children.Add(_valuesBefore);
        preview.Children.Add(_valuesAfter);
        Children.Add(_error);
        Children.Add(_note);
        Children.Add(preview);

        Load(initialKind);
        _category.SelectionChanged += (_, _) =>
        {
            if (!_loading)
            {
                FillKinds(null);
                OnChanged();
            }
        };
        foreach (ComboBox combo in new[] { _kind, _type, _size, _endian, _overflow, _operandSource, _keyOrigin, _scope, _bitIndex, _bitFill, _bitOrder, _bitScope })
        {
            combo.SelectionChanged += (_, _) => OnChanged();
        }

        foreach (TextBox box in new[] { _start, _length, _operand, _increment, _process, _skip, _keyIncrement, _shiftBits, _rotateIncrement, _min, _max, _bitOffset, _bitCount })
        {
            box.TextChanged += (_, _) => OnChanged();
        }

        foreach (RadioButton radio in new[] { _targetSelection, _targetWhole, _targetRange })
        {
            radio.Checked += (_, _) => OnChanged();
        }

        _key.Changed += (_, _) => OnChanged();
    }

    /// <summary>入力が変わった (ダイアログが実行ボタンの有効状態を更新する)。</summary>
    public event EventHandler? Changed;

    public DataOperationKind SelectedKind => _kind.SelectedIndex >= 0 && _kind.SelectedIndex < _kinds.Count ? _kinds[_kind.SelectedIndex] : DataOperationKind.Add;

    private DataOperationCategory SelectedCategory => Categories[Math.Max(0, _category.SelectedIndex)];

    /// <summary>
    /// 入力を演算の指定にする。誤りがあれば null で、欄を赤枠にして理由を示す。プレビューと注記も更新する。
    /// </summary>
    public DataOperationRequest? TryGetRequest()
    {
        var context = new EditorExpressionContext(_editor);
        string? error = null;
        foreach (TextBox box in new[] { _start, _length, _operand, _increment, _process, _skip, _keyIncrement, _shiftBits, _rotateIncrement, _min, _max, _bitOffset, _bitCount })
        {
            DialogParts.MarkInvalid(box, false);
        }

        long Eval(TextBox box, long min = long.MinValue, long max = long.MaxValue)
        {
            if (!DialogParts.TryEvaluate(box.Text, context, out long v, out ExpressionException? e))
            {
                error ??= DialogParts.ExpressionError(e!);
                DialogParts.MarkInvalid(box, true);
                return 0;
            }

            if (v < min || v > max)
            {
                error ??= Loc.Format("Fill_Error_Range", StatusFormat.Hex(min), StatusFormat.Hex(max));
                DialogParts.MarkInvalid(box, true);
            }

            return v;
        }

        double? EvalFloat(TextBox box, bool optional)
        {
            if (optional && string.IsNullOrWhiteSpace(box.Text))
            {
                return null;
            }

            if (!DataOperationSpec.TryParseFloat(box.Text, out double v))
            {
                error ??= Loc.Get("DataOp_Error_Float");
                DialogParts.MarkInvalid(box, true);
                return 0;
            }

            return v;
        }

        DataOperationKind kind = SelectedKind;
        var spec = new DataOperationSpec
        {
            Kind = kind,
            Type = (ElementType)Math.Max(0, _type.SelectedIndex),
            Size = 1 << Math.Max(0, _size.SelectedIndex),
            BigEndian = _endian.SelectedIndex == 1,
            Overflow = _overflow.SelectedIndex == 1 ? OverflowMode.Saturate : OverflowMode.Wrap,
            IncrementScope = _scope.SelectedIndex == 1 ? IncrementScope.Continuous : IncrementScope.PerRange,
            OperandSource = _operandSource.SelectedIndex == 1 ? OperandSource.KeyBytes : OperandSource.Number,
            KeyOrigin = _keyOrigin.SelectedIndex == 1 ? PatternOrigin.OffsetZero : PatternOrigin.RangeStart,
            LsbFirst = _bitOrder.SelectedIndex == 1,
            FillWithOne = _bitFill.SelectedIndex == 1,
            BitScope = _bitScope.SelectedIndex == 1 ? BitShiftScope.ToEnd : BitShiftScope.SelectionOnly,
            BitIndex = Math.Max(0, _bitIndex.SelectedIndex),
        };
        UpdateSections(spec);

        // 対象範囲 (EDIT-31 の仕様 1)。
        IReadOnlyList<TargetRange> ranges = [];
        if (_targetSelection.IsChecked == true && _selection.Count > 0)
        {
            ranges = _selection;
        }
        else if (_targetWhole.IsChecked == true)
        {
            ranges = [new TargetRange(0, _document.Length)];
        }
        else
        {
            long s = Eval(_start, 0, _document.Length);
            long n = Eval(_length, 0, Math.Max(0, _document.Length - Math.Max(0, s)));
            ranges = [new TargetRange(s, n)];
        }

        FillSpec? keySource = null;
        switch (spec.Category)
        {
            case DataOperationCategory.Arithmetic or DataOperationCategory.Bitwise or DataOperationCategory.ShiftRotate
                or DataOperationCategory.Clamp or DataOperationCategory.Reorder:
                spec = spec with { ProcessCount = (int)Eval(_process, 1, int.MaxValue), SkipCount = (int)Eval(_skip, 0, int.MaxValue) };
                break;
        }

        if (spec.UsesOperand && !spec.UsesKey)
        {
            if (spec.IsFloat)
            {
                spec = spec with { FloatOperand = EvalFloat(_operand, false) ?? 0, FloatIncrement = EvalFloat(_increment, false) ?? 0 };
            }
            else
            {
                spec = spec with { Operand = Eval(_operand), Increment = Eval(_increment) };
            }
        }

        switch (spec.Category)
        {
            case DataOperationCategory.Bitwise when spec.UsesKey:
                spec = spec with { KeyIncrement = (int)Eval(_keyIncrement, -255, 255) };
                keySource = _key.TryGetSpec();
                if (keySource is null)
                {
                    error ??= Loc.Get("DataOp_Error_Key");
                }
                else if (keySource.Kind is FillKind.HexPattern or FillKind.Text)
                {
                    spec = spec with { Key = keySource.Pattern };
                }
                else
                {
                    // ファイル・クリップボードの鍵は実行するときに読む。設定の確認には仮の 1 バイトを使う。
                    spec = spec with { Key = [0] };
                }

                break;
            case DataOperationCategory.ShiftRotate:
                spec = spec with { ShiftBits = (int)Eval(_shiftBits, 0, int.MaxValue), RotateIncrement = (int)Eval(_rotateIncrement, int.MinValue, int.MaxValue) };
                break;
            case DataOperationCategory.Clamp when spec.Type == ElementType.Float:
                spec = spec with { FloatMin = EvalFloat(_min, true), FloatMax = EvalFloat(_max, true) };
                break;
            case DataOperationCategory.Clamp:
                spec = spec with
                {
                    Min = string.IsNullOrWhiteSpace(_min.Text) ? null : Eval(_min),
                    Max = string.IsNullOrWhiteSpace(_max.Text) ? null : Eval(_max),
                };
                break;
            case DataOperationCategory.BitInsertDelete:
                spec = spec with { BitOffset = Eval(_bitOffset, 0, _document.Length), BitCount = Eval(_bitCount, 1, 1L << 31) };
                break;
        }

        // 設定の誤り (入力欄のエラー。EDIT-31〜37 の「エラー」)。
        if (error is null)
        {
            DataOperationError specError;
            if (spec.Category == DataOperationCategory.BitInsertDelete)
            {
                TargetRange? region = ranges.Count > 0 ? ranges.FirstOrDefault(r => r.Offset <= spec.BitOffset && spec.BitOffset < r.End) : null;
                specError = BitShifter.Analyze(_document.Length, _document.CanResize, region is { Length: > 0 } ? region : null, spec).Error;
            }
            else
            {
                specError = ranges.Sum(r => r.Length) == 0 ? DataOperationError.PositionOutOfRange : DataOperationRunner.Validate(ranges, spec);
            }

            if (specError != DataOperationError.None)
            {
                error = Loc.Get("DataOp_Error_" + specError);
                TextBox? box = specError switch
                {
                    DataOperationError.OperandOutOfRange or DataOperationError.DivideByZero or DataOperationError.DivisorBecomesZero =>
                        spec.Category == DataOperationCategory.Clamp ? _min : _operand,
                    DataOperationError.BitCountOutOfRange => spec.Category == DataOperationCategory.ShiftRotate ? _shiftBits : _bitCount,
                    DataOperationError.MinGreaterThanMax => _max,
                    DataOperationError.InvalidStride => _process,
                    DataOperationError.PositionOutOfRange => spec.Category == DataOperationCategory.BitInsertDelete ? _bitOffset : _length,
                    _ => null,
                };
                if (box is not null)
                {
                    DialogParts.MarkInvalid(box, true);
                }
            }
        }

        _error.Text = error ?? string.Empty;
        UpdatePreview(error is null ? spec : null, ranges);
        return error is null ? new DataOperationRequest(spec, ranges, keySource is { Kind: FillKind.File or FillKind.Clipboard } ? keySource : null) : null;
    }

    /// <summary>最後に使った演算と設定を記憶する。</summary>
    public void Save()
    {
        var state = new Dictionary<string, string>
        {
            ["kind"] = SelectedKind.ToString(),
            ["type"] = Index(_type),
            ["size"] = Index(_size),
            ["operand"] = _operand.Text,
            ["increment"] = _increment.Text,
            ["process"] = _process.Text,
            ["skip"] = _skip.Text,
            ["overflow"] = Index(_overflow),
            ["operandSource"] = Index(_operandSource),
            ["keyOrigin"] = Index(_keyOrigin),
            ["keyIncrement"] = _keyIncrement.Text,
            ["shiftBits"] = _shiftBits.Text,
            ["rotateIncrement"] = _rotateIncrement.Text,
            ["min"] = _min.Text,
            ["max"] = _max.Text,
            ["bitCount"] = _bitCount.Text,
            ["bitFill"] = Index(_bitFill),
            ["bitOrder"] = Index(_bitOrder),
        };
        AppState.SetString(StateKey, JsonSerializer.Serialize(state));
        _key.Save();
    }

    private static string Index(ComboBox combo) => combo.SelectedIndex.ToString(CultureInfo.InvariantCulture);

    private void Load(DataOperationKind? initialKind)
    {
        _loading = true;
        try
        {
            Dictionary<string, string> state = [];
            try
            {
                string json = AppState.GetString(StateKey, string.Empty);
                state = json.Length > 0 ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [] : [];
            }
            catch (JsonException)
            {
            }

            string Get(string key, string fallback) => state.TryGetValue(key, out string? v) ? v : fallback;
            void SetIndex(ComboBox combo, string key)
            {
                if (int.TryParse(Get(key, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < combo.Items.Count)
                {
                    combo.SelectedIndex = i;
                }
            }

            DataOperationKind kind = initialKind
                ?? (Enum.TryParse(Get("kind", nameof(DataOperationKind.Add)), out DataOperationKind k) ? k : DataOperationKind.Add);
            _category.SelectedIndex = Array.IndexOf(Categories, DataOperationSpec.CategoryOf(kind));
            FillKinds(kind);
            SetIndex(_type, "type");
            SetIndex(_size, "size");
            SetIndex(_overflow, "overflow");
            SetIndex(_operandSource, "operandSource");
            SetIndex(_keyOrigin, "keyOrigin");
            SetIndex(_bitFill, "bitFill");
            SetIndex(_bitOrder, "bitOrder");
            _operand.Text = Get("operand", _operand.Text);
            _increment.Text = Get("increment", _increment.Text);
            _process.Text = Get("process", _process.Text);
            _skip.Text = Get("skip", _skip.Text);
            _keyIncrement.Text = Get("keyIncrement", _keyIncrement.Text);
            _shiftBits.Text = Get("shiftBits", _shiftBits.Text);
            _rotateIncrement.Text = Get("rotateIncrement", _rotateIncrement.Text);
            _min.Text = Get("min", _min.Text);
            _max.Text = Get("max", _max.Text);
            _bitCount.Text = Get("bitCount", _bitCount.Text);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>分類に属する演算をドロップダウンに入れる。</summary>
    private void FillKinds(DataOperationKind? select)
    {
        DataOperationCategory category = SelectedCategory;
        _kinds.Clear();
        _kinds.AddRange(Enum.GetValues<DataOperationKind>().Where(k => DataOperationSpec.CategoryOf(k) == category));
        bool loading = _loading;
        _loading = true;
        _kind.Items.Clear();
        foreach (DataOperationKind k in _kinds)
        {
            _kind.Items.Add(Loc.Get("DataOp_Kind_" + k));
        }

        _kind.SelectedIndex = select is { } s && _kinds.Contains(s) ? _kinds.IndexOf(s) : 0;
        _loading = loading;
    }

    /// <summary>演算に関係する設定欄だけを表示する。</summary>
    private void UpdateSections(DataOperationSpec spec)
    {
        DataOperationCategory category = spec.Category;
        bool bits = category == DataOperationCategory.BitInsertDelete;
        bool reorder = category == DataOperationCategory.Reorder;
        _common.Visibility = Show(!bits);
        _rangeFields.Visibility = Show(_targetRange.IsChecked == true);
        _arithmetic.Visibility = Show(category == DataOperationCategory.Arithmetic && spec.Type != ElementType.Float);
        _bitwise.Visibility = Show(category == DataOperationCategory.Bitwise);
        _keyPanel.Visibility = Show(spec.UsesKey);
        _operandSource.Visibility = Show(spec.UsesOperand);
        _xorNote.Visibility = Show(spec.Kind == DataOperationKind.Xor);
        _shift.Visibility = Show(category == DataOperationCategory.ShiftRotate);
        _rotateIncrement.Visibility = Show(spec.Kind is DataOperationKind.RotateLeft or DataOperationKind.RotateRight);
        _clamp.Visibility = Show(category == DataOperationCategory.Clamp);
        _bits.Visibility = Show(bits);
        _bitFill.Visibility = Show(spec.Kind == DataOperationKind.InsertBits);
        _numberOperand.Visibility = Show(spec.UsesOperand && !spec.UsesKey);
        _type.Visibility = Show(!reorder && !spec.UsesKey);
        _size.Visibility = Show(!reorder && !spec.UsesKey);
        _endian.Visibility = Show(!reorder && !spec.UsesKey);
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>プレビュー (EDIT-31 の仕様 10、EDIT-37 の「画面」) と注記 (仕様 8)。</summary>
    private void UpdatePreview(DataOperationSpec? spec, IReadOnlyList<TargetRange> ranges)
    {
        _previewBefore.Text = _previewAfter.Text = _previewMarks.Text = _valuesBefore.Text = _valuesAfter.Text = _note.Text = string.Empty;
        if (spec is null)
        {
            return;
        }

        try
        {
            if (spec.Category == DataOperationCategory.BitInsertDelete)
            {
                TargetRange? region = ranges.FirstOrDefault(r => r.Offset <= spec.BitOffset && spec.BitOffset < r.End);
                (byte[] before, byte[] after) = BitShifter.Preview(_document.Current, _document.CanResize, region is { Length: > 0 } ? region : null, spec);
                _previewBefore.Text = Loc.Format("DataOp_PreviewBeforeLine", string.Join(' ', before.Select(Binary)));
                _previewAfter.Text = Loc.Format("DataOp_PreviewAfterLine", string.Join(' ', after.Select(Binary)));
                BitShiftInfo info = BitShifter.Analyze(_document.Length, _document.CanResize, region is { Length: > 0 } ? region : null, spec);
                _note.Text = info.RewriteBytes > 0
                    ? Loc.Format("DataOp_RewriteNote", StatusFormat.ShortSize(info.RewriteBytes, CultureInfo.CurrentCulture) ?? StatusFormat.Number(info.RewriteBytes, CultureInfo.CurrentCulture))
                    : string.Empty;
                return;
            }

            DataOperationPreview preview = DataOperationRunner.Preview(_document.Current, ranges, spec);
            _previewBefore.Text = Loc.Format("DataOp_PreviewBeforeLine", DialogParts.Hex(preview.Before));
            _previewAfter.Text = Loc.Format("DataOp_PreviewAfterLine", DialogParts.Hex(preview.After));

            // 変わるバイトの下に印を付ける (色だけに頼らない)。見出しの幅は変更前・変更後の行と同じにそろえる。
            string prefix = Loc.Format("DataOp_PreviewBeforeLine", string.Empty);
            var marks = new System.Text.StringBuilder(new string(' ', prefix.Length));
            for (int i = 0; i < preview.After.Length; i++)
            {
                marks.Append(preview.IsChanged(i) ? "^^ " : "   ");
            }

            _previewMarks.Text = marks.ToString().TrimEnd();
            AutomationProperties.SetName(_previewMarks, Loc.Format("DataOp_ChangedCount",
                Enumerable.Range(0, preview.After.Length).Count(preview.IsChanged)));
            if (preview.BeforeValues.Count > 0)
            {
                _valuesBefore.Text = Loc.Format("DataOp_ValuesBeforeLine", string.Join(", ", preview.BeforeValues));
                _valuesAfter.Text = Loc.Format("DataOp_ValuesAfterLine", string.Join(", ", preview.AfterValues));
            }

            if (preview.TrailingBytes > 0)
            {
                _note.Text = Loc.Format("DataOp_TrailingNote", preview.TrailingBytes);
            }
        }
        catch (IOException)
        {
            // 読めない範囲はプレビューしない (実行すると理由を示す)。
        }
    }

    private static string Binary(byte b) => Convert.ToString(b, 2).PadLeft(8, '0');

    private void OnChanged()
    {
        if (_loading)
        {
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static StackPanel Row(params FrameworkElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (FrameworkElement child in children)
        {
            if (child is TextBox box)
            {
                box.MinWidth = 140;
            }

            row.Children.Add(child);
        }

        return row;
    }

    private StackPanel Section(params UIElement[] children)
    {
        StackPanel panel = Stack(children);
        Children.Add(panel);
        return panel;
    }

    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (UIElement child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }
}
