using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Editing;
using HexEditor.Core.Editing.TransformView;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// 変換の定義ダイアログの中身 (EDIT-45 の「画面」): 演算の一覧 (追加・削除・並べ替え)、選んだ演算の設定、適用範囲と鍵の起点、
/// 名前を付けた保存と読み込み (仕様 14)、表示のプレビュー。入力は <see cref="TryGetDefinition"/> で <see cref="TransformDefinition"/> にする。
/// </summary>
internal sealed class TransformViewPanel : StackPanel
{
    /// <summary>名前を付けて保存した定義 (アプリの状態。ほかのドキュメントで再利用する。仕様 14)。</summary>
    public const string SavedKey = "transformView.saved";

    private static readonly TransformOpKind[] Kinds =
    [
        TransformOpKind.Xor, TransformOpKind.Add, TransformOpKind.Subtract, TransformOpKind.Not, TransformOpKind.RotateLeft, TransformOpKind.RotateRight,
        TransformOpKind.ByteSwap16, TransformOpKind.ByteSwap32, TransformOpKind.ByteSwap64, TransformOpKind.ReverseBits, TransformOpKind.SwapNibbles,
    ];

    private readonly EditorState _editor;
    private readonly List<TransformOp> _ops = [];
    private readonly IReadOnlyList<(string Id, string Name)> _plugins;
    private readonly ListView _list;
    private readonly ComboBox _addKind, _size, _endian, _operandSource, _keyOrigin, _plugin, _saved;
    private readonly TextBox _operand, _increment, _key, _bits, _rangeStart, _rangeLength, _originOffset, _name;
    private readonly RadioButton _whole, _range;
    private readonly StackPanel _opSettings, _numberPanel, _keyPanel, _rangePanel;
    private readonly TextBlock _error, _preview;
    private readonly Button _remove, _up, _down;
    private bool _loading;

    public TransformViewPanel(EditorState editor, TransformDefinition? initial, IReadOnlyList<(string Id, string Name)> plugins)
    {
        _editor = editor;
        _plugins = plugins;
        Spacing = 8;
        MinWidth = 480;

        // ---- 演算の一覧 (最大 8 個。仕様 1) ----
        _list = new ListView { Height = 150, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(_list, "TransformView_Ops");
        AutomationProperties.SetName(_list, Loc.Get("TransformView_Ops"));
        _addKind = DialogParts.Combo("TransformView_AddKind", Loc.Get("TransformView_AddKind"),
            [.. Kinds.Select(k => Loc.Get("TransformView_Kind_" + k)), .. plugins.Select(p => Loc.Format("TransformView_Kind_Plugin", p.Name))], 0);
        var add = new Button { Content = Loc.Get("TransformView_Add"), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(add, "TransformView_Add");
        add.Click += (_, _) => AddOp();
        _remove = IconButton("TransformView_Remove", "", (_, _) => RemoveOp());
        _up = IconButton("TransformView_Up", "", (_, _) => MoveOp(-1));
        _down = IconButton("TransformView_Down", "", (_, _) => MoveOp(1));
        Children.Add(new TextBlock { Text = Loc.Get("TransformView_Ops"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        Children.Add(_list);
        Children.Add(Row(_addKind, add, _remove, _up, _down));

        // ---- 選んだ演算の設定 (要素の大きさ・エンディアン・オペランドの増分は EDIT-31 と同じ) ----
        _size = DialogParts.Combo("TransformView_Size", Loc.Get("DataOp_Size"), ["1", "2", "4", "8"], 0);
        _endian = DialogParts.Combo("TransformView_Endian", Loc.Get("DataOp_Endian"), [Loc.Get("DataOp_LittleEndian"), Loc.Get("DataOp_BigEndian")], 0);
        _size.MinWidth = _endian.MinWidth = 140;
        _operandSource = DialogParts.Combo("TransformView_OperandSource", Loc.Get("DataOp_OperandSource"),
            [Loc.Get("DataOp_OperandSource_Number"), Loc.Get("TransformView_KeyHex")], 1);
        _operand = DialogParts.Field("TransformView_Operand", Loc.Get("DataOp_Operand"), "0");
        _increment = DialogParts.Field("TransformView_Increment", Loc.Get("DataOp_Increment"), "0");
        _key = DialogParts.Field("TransformView_Key", Loc.Get("TransformView_Key"), "FF");
        _bits = DialogParts.Field("TransformView_Bits", Loc.Get("DataOp_ShiftBits"), "1");
        _plugin = DialogParts.Combo("TransformView_Plugin", Loc.Get("TransformView_Plugin"), plugins.Select(p => p.Name), plugins.Count > 0 ? 0 : -1);
        _numberPanel = Row(_operand, _increment);
        _keyPanel = new StackPanel { Children = { _key } };
        _opSettings = new StackPanel { Spacing = 8 };
        _opSettings.Children.Add(Row(_size, _endian));
        _opSettings.Children.Add(_operandSource);
        _opSettings.Children.Add(_numberPanel);
        _opSettings.Children.Add(_keyPanel);
        _opSettings.Children.Add(_bits);
        _opSettings.Children.Add(_plugin);
        Children.Add(_opSettings);

        // ---- 適用範囲と鍵の起点 (仕様 3) ----
        _whole = DialogParts.Radio("TransformView_Whole", Loc.Get("TransformView_Whole"), "TransformRange", true);
        _range = DialogParts.Radio("TransformView_Range", Loc.Get("TransformView_Range"), "TransformRange", false);
        _rangeStart = DialogParts.Field("TransformView_RangeStart", Loc.Get("DataOp_RangeStart"), RangeSelectionModel.Format(editor.SelectionStart));
        _rangeLength = DialogParts.Field("TransformView_RangeLength", Loc.Get("DataOp_RangeLength"),
            RangeSelectionModel.Format(editor.HasSelection ? editor.SelectionLength : Math.Max(1, editor.Document.Length - editor.SelectionStart)));
        _rangePanel = Row(_rangeStart, _rangeLength);
        _keyOrigin = DialogParts.Combo("TransformView_KeyOrigin", Loc.Get("TransformView_KeyOrigin"),
            [Loc.Get("TransformView_Origin_Zero"), Loc.Get("TransformView_Origin_Range"), Loc.Get("TransformView_Origin_Offset")], 0);
        _originOffset = DialogParts.Field("TransformView_OriginOffset", Loc.Get("TransformView_OriginOffset"), "0");
        Children.Add(new TextBlock { Text = Loc.Get("TransformView_Scope"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        Children.Add(_whole);
        Children.Add(_range);
        Children.Add(_rangePanel);
        Children.Add(Row(_keyOrigin, _originOffset));

        // ---- 名前を付けて保存・読み込み (仕様 14) ----
        _saved = DialogParts.Combo("TransformView_Saved", Loc.Get("TransformView_Saved"), SavedDefinitions().Select(d => d.Name!), -1);
        _saved.PlaceholderText = Loc.Get("TransformView_SavedPlaceholder");
        _name = DialogParts.Field("TransformView_Name", Loc.Get("TransformView_Name"), initial?.Name ?? string.Empty, monospace: false);
        var save = new Button { Content = Loc.Get("TransformView_Save"), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(save, "TransformView_SaveDefinition");
        save.Click += (_, _) => SaveDefinition();
        Children.Add(Row(_saved, _name, save));

        // ---- 誤りとプレビュー ----
        _error = DialogParts.Caption("TransformView_Error");
        _error.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        _preview = DialogParts.Caption("TransformView_Preview", monospace: true);
        Children.Add(_error);
        Children.Add(new TextBlock { Text = Loc.Get("TransformView_PreviewTitle"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        Children.Add(_preview);

        Load(initial ?? TransformDefinition.Xor([0xFF]));
        _list.SelectionChanged += (_, _) => ShowSelected();
        foreach (ComboBox combo in new[] { _size, _endian, _operandSource, _plugin })
        {
            combo.SelectionChanged += (_, _) => StoreSelected();
        }

        foreach (TextBox box in new[] { _operand, _increment, _key, _bits })
        {
            box.TextChanged += (_, _) => StoreSelected();
        }

        foreach (TextBox box in new[] { _rangeStart, _rangeLength, _originOffset })
        {
            box.TextChanged += (_, _) => OnChanged();
        }

        _keyOrigin.SelectionChanged += (_, _) => OnChanged();
        _whole.Checked += (_, _) => OnChanged();
        _range.Checked += (_, _) => OnChanged();
        _saved.SelectionChanged += (_, _) =>
        {
            if (!_loading && _saved.SelectedIndex >= 0 && SavedDefinitions().ElementAtOrDefault(_saved.SelectedIndex) is { } d)
            {
                Load(d);
            }
        };
    }

    /// <summary>入力が変わった (ダイアログの確定ボタンの有効・無効を更新する)。</summary>
    public event EventHandler? Changed;

    private static Button IconButton(string id, string glyph, RoutedEventHandler click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, Loc.Get(id));
        ToolTipService.SetToolTip(button, Loc.Get(id));
        button.Click += click;
        return button;
    }

    private static StackPanel Row(params FrameworkElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (FrameworkElement item in items)
        {
            row.Children.Add(item);
        }

        return row;
    }

    /// <summary>名前を付けて保存した定義の一覧。</summary>
    public static IReadOnlyList<TransformDefinition> SavedDefinitions()
    {
        try
        {
            return JsonNode.Parse(AppState.GetString(SavedKey, "[]")) is JsonArray list
                ? [.. list.Select(TransformDefinition.FromJson).OfType<TransformDefinition>().Where(d => d.Name is { Length: > 0 })]
                : [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private void SaveDefinition()
    {
        if (TryGetDefinition() is not { } definition || _name.Text.Trim() is not { Length: > 0 } name)
        {
            DialogParts.MarkInvalid(_name, _name.Text.Trim().Length == 0);
            return;
        }

        DialogParts.MarkInvalid(_name, false);
        var list = SavedDefinitions().Where(d => d.Name != name).ToList();
        list.Add(definition with { Name = name });
        AppState.SetString(SavedKey, new JsonArray([.. list.Select(d => (JsonNode)d.ToJson())]).ToJsonString());
        _loading = true;
        _saved.Items.Clear();
        foreach (TransformDefinition d in list)
        {
            _saved.Items.Add(d.Name);
        }

        _saved.SelectedIndex = list.Count - 1;
        _loading = false;
    }

    private void Load(TransformDefinition definition)
    {
        _loading = true;
        _ops.Clear();
        _ops.AddRange(definition.Ops);
        _whole.IsChecked = definition.RangeStart is null;
        _range.IsChecked = definition.RangeStart is not null;
        if (definition.RangeStart is { } s)
        {
            _rangeStart.Text = RangeSelectionModel.Format(s);
            _rangeLength.Text = RangeSelectionModel.Format(definition.RangeLength ?? 1);
        }

        _keyOrigin.SelectedIndex = (int)definition.KeyOrigin;
        _originOffset.Text = definition.KeyOriginOffset.ToString(CultureInfo.InvariantCulture);
        _name.Text = definition.Name ?? _name.Text;
        _loading = false;
        RefreshList(0);
    }

    private void RefreshList(int select)
    {
        _loading = true;
        _list.Items.Clear();
        for (int i = 0; i < _ops.Count; i++)
        {
            var item = new ListViewItem { Content = $"{i + 1}. {_ops[i].Label(PluginName)}" };
            AutomationProperties.SetAutomationId(item, "TransformView_Op" + i);
            _list.Items.Add(item);
        }

        _list.SelectedIndex = _ops.Count == 0 ? -1 : Math.Clamp(select, 0, _ops.Count - 1);
        _loading = false;
        ShowSelected();
    }

    private string? PluginName(string id) => _plugins.FirstOrDefault(p => p.Id == id).Name;

    private void AddOp()
    {
        if (_ops.Count >= TransformDefinition.MaxOps)
        {
            return;
        }

        int index = _addKind.SelectedIndex;
        TransformOp op = index < Kinds.Length
            ? Kinds[index] == TransformOpKind.Xor ? TransformOp.XorKey([0xFF]) : new TransformOp { Kind = Kinds[index], Operand = 1 }
            : new TransformOp { Kind = TransformOpKind.Plugin, PluginId = _plugins[index - Kinds.Length].Id };
        _ops.Add(op);
        RefreshList(_ops.Count - 1);
    }

    private void RemoveOp()
    {
        int i = _list.SelectedIndex;
        if (i >= 0 && i < _ops.Count)
        {
            _ops.RemoveAt(i);
            RefreshList(i);
        }
    }

    private void MoveOp(int delta)
    {
        int i = _list.SelectedIndex, j = i + delta;
        if (i >= 0 && j >= 0 && j < _ops.Count)
        {
            (_ops[i], _ops[j]) = (_ops[j], _ops[i]);
            RefreshList(j);
        }
    }

    /// <summary>選んだ演算の設定を入力欄に出す。</summary>
    private void ShowSelected()
    {
        if (_loading)
        {
            return;
        }

        int i = _list.SelectedIndex;
        _remove.IsEnabled = i >= 0;
        _up.IsEnabled = i > 0;
        _down.IsEnabled = i >= 0 && i < _ops.Count - 1;
        _opSettings.Visibility = i >= 0 ? Visibility.Visible : Visibility.Collapsed;
        if (i < 0)
        {
            OnChanged();
            return;
        }

        _loading = true;
        TransformOp op = _ops[i];
        _size.SelectedIndex = Array.IndexOf([1, 2, 4, 8], op.Size) is var s and >= 0 ? s : 0;
        _endian.SelectedIndex = op.BigEndian ? 1 : 0;
        _operandSource.SelectedIndex = op.Key is not null ? 1 : 0;
        _operand.Text = "0x" + op.Operand.ToString("X", CultureInfo.InvariantCulture);
        _increment.Text = op.Increment == 0 ? "0" : "0x" + op.Increment.ToString("X", CultureInfo.InvariantCulture);
        _key.Text = op.Key is { } key ? string.Join(' ', key.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) : _key.Text;
        _bits.Text = op.Bits.ToString(CultureInfo.InvariantCulture);
        _plugin.SelectedIndex = op.PluginId is { } id ? Math.Max(0, _plugins.ToList().FindIndex(p => p.Id == id)) : _plugin.SelectedIndex;
        _loading = false;
        UpdateVisibility(op);
        OnChanged();
    }

    private void UpdateVisibility(TransformOp op)
    {
        bool plugin = op.Kind == TransformOpKind.Plugin;
        bool xor = op.Kind == TransformOpKind.Xor;
        bool usesKey = xor && _operandSource.SelectedIndex == 1;
        bool elementWise = op.Kind is TransformOpKind.Add or TransformOpKind.Subtract or TransformOpKind.Not or TransformOpKind.RotateLeft
            or TransformOpKind.RotateRight || xor && !usesKey;
        _size.Visibility = _endian.Visibility = elementWise ? Visibility.Visible : Visibility.Collapsed;
        _operandSource.Visibility = xor ? Visibility.Visible : Visibility.Collapsed;
        _numberPanel.Visibility = (op.Kind is TransformOpKind.Add or TransformOpKind.Subtract || xor && !usesKey) ? Visibility.Visible : Visibility.Collapsed;
        _keyPanel.Visibility = usesKey ? Visibility.Visible : Visibility.Collapsed;
        _bits.Visibility = op.Kind is TransformOpKind.RotateLeft or TransformOpKind.RotateRight ? Visibility.Visible : Visibility.Collapsed;
        _plugin.Visibility = plugin ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>入力欄の値を選んだ演算に入れる (読めない値は前の値のまま、誤りは <see cref="TryGetDefinition"/> で示す)。</summary>
    private void StoreSelected()
    {
        int i = _list.SelectedIndex;
        if (_loading || i < 0 || i >= _ops.Count)
        {
            return;
        }

        TransformOp op = _ops[i];
        int size = new[] { 1, 2, 4, 8 }[Math.Max(0, _size.SelectedIndex)];
        bool xorKey = op.Kind == TransformOpKind.Xor && _operandSource.SelectedIndex == 1;
        op = op with
        {
            Size = size,
            BigEndian = _endian.SelectedIndex == 1,
            Key = xorKey ? HexText.TryParse(_key.Text) ?? [] : null,
            Operand = TryNumber(_operand.Text, out ulong v) ? v : op.Operand,
            Increment = TryNumber(_increment.Text, out ulong inc) ? inc : op.Increment,
            Bits = int.TryParse(_bits.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits) ? bits : op.Bits,
            PluginId = op.Kind == TransformOpKind.Plugin && _plugin.SelectedIndex >= 0 ? _plugins[_plugin.SelectedIndex].Id : op.PluginId,
        };
        _ops[i] = op;
        if (_list.Items[i] is ListViewItem item)
        {
            item.Content = $"{i + 1}. {op.Label(PluginName)}";
        }

        UpdateVisibility(op);
        OnChanged();
    }

    private bool TryNumber(string text, out ulong value)
    {
        value = 0;
        if (!ExpressionEvaluator.TryEvaluateWide(text, new EditorExpressionContext(_editor), out Int128 v, out _) || v < -(Int128)ulong.MaxValue || v > ulong.MaxValue)
        {
            return false;
        }

        value = v < 0 ? (ulong)(long)v : (ulong)v;
        return true;
    }

    private void OnChanged()
    {
        if (_loading)
        {
            return;
        }

        _rangePanel.Visibility = _range.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _originOffset.Visibility = _keyOrigin.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        TransformDefinition? definition = TryGetDefinition();
        UpdatePreview(definition);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>入力から定義を作る。誤りがあれば入力欄に赤枠と説明文を出して null (確定ボタンを無効にする。EDIT-45 の「エラー」)。</summary>
    public TransformDefinition? TryGetDefinition()
    {
        string? error = null;
        int i = _list.SelectedIndex;
        bool keyBad = false, operandBad = false, rangeBad = false, originBad = false;
        if (i >= 0 && i < _ops.Count && _ops[i].Kind == TransformOpKind.Xor && _operandSource.SelectedIndex == 1)
        {
            byte[]? key = HexText.TryParse(_key.Text);
            keyBad = key is null || key.Length is 0 or > TransformDefinition.MaxKeyLength;
            error = key is null ? Loc.Get(_key.Text.Trim().Length == 0 ? "TransformView_Error_KeyEmpty" : "TransformView_Error_KeyHex")
                : key.Length == 0 ? Loc.Get("TransformView_Error_KeyEmpty") : key.Length > TransformDefinition.MaxKeyLength ? Loc.Get("TransformView_Error_KeyTooLong") : null;
        }

        if (i >= 0 && _numberPanel.Visibility == Visibility.Visible && (!TryNumber(_operand.Text, out ulong op) || !TryNumber(_increment.Text, out _)
            || _ops[i].EffectiveSize < 8 && op > (1UL << (_ops[i].EffectiveSize * 8)) - 1))
        {
            operandBad = true;
            error ??= Loc.Get("TransformView_Error_Operand");
        }

        long? start = null, length = null;
        if (_range.IsChecked == true)
        {
            var context = new EditorExpressionContext(_editor);
            if (TryEval(_rangeStart.Text, context, out long s) && TryEval(_rangeLength.Text, context, out long l) && s >= 0 && l > 0)
            {
                (start, length) = (s, l);
            }
            else
            {
                rangeBad = true;
                error ??= Loc.Get("TransformView_Error_Range");
            }
        }

        long originOffset = 0;
        if (_keyOrigin.SelectedIndex == 2 && !TryEval(_originOffset.Text, new EditorExpressionContext(_editor), out originOffset))
        {
            originBad = true;
            error ??= Loc.Get("TransformView_Error_Origin");
        }

        DialogParts.MarkInvalid(_key, keyBad);
        DialogParts.MarkInvalid(_operand, operandBad);
        DialogParts.MarkInvalid(_rangeStart, rangeBad);
        DialogParts.MarkInvalid(_rangeLength, rangeBad);
        DialogParts.MarkInvalid(_originOffset, originBad);
        var definition = new TransformDefinition
        {
            Ops = [.. _ops],
            RangeStart = start,
            RangeLength = length,
            KeyOrigin = (TransformKeyOrigin)Math.Max(0, _keyOrigin.SelectedIndex),
            KeyOriginOffset = originOffset,
            Name = _name.Text.Trim() is { Length: > 0 } n ? n : null,
        };
        TransformError e = definition.Validate();
        if (error is null && e != TransformError.None)
        {
            error = Loc.Get("TransformView_Error_" + e);
        }

        _error.Text = error ?? string.Empty;
        return error is null ? definition : null;
    }

    private static bool TryEval(string text, IExpressionContext context, out long value)
    {
        try
        {
            value = ExpressionEvaluator.Evaluate(text, context);
            return true;
        }
        catch (ExpressionException)
        {
            value = 0;
            return false;
        }
    }

    /// <summary>表示のプレビュー: カーソル位置からの 16 バイトの元のデータと変換後。</summary>
    private void UpdatePreview(TransformDefinition? definition)
    {
        if (definition is null)
        {
            _preview.Text = string.Empty;
            return;
        }

        try
        {
            long at = _editor.Cursor - _editor.Cursor % 16;
            byte[] raw = new byte[(int)Math.Clamp(_editor.Document.Length - at, 0, 16)];
            _editor.Document.DataSnapshot.Read(at, raw);
            byte[] shown = (byte[])raw.Clone();
            definition.Create(MainWindow.PluginTransform).Forward(at, shown);
            _preview.Text = Loc.Format("TransformView_PreviewLines", RangeSelectionModel.Format(at),
                string.Join(' ', raw.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))),
                string.Join(' ', shown.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))));
        }
        catch (ArgumentException)
        {
            _preview.Text = string.Empty;
        }
    }
}
