using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Templates.Inference;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>テンプレートの推定のパネルの持ち主 (MainWindow)。</summary>
public interface ITemplateInferenceHost
{
    /// <summary>推定する。<paramref name="candidate"/> はレコード長の候補を選んだとき。</summary>
    Task RunInferenceAsync(int? candidate = null, bool keepManual = false);

    void ApplyInference();

    void OpenInferenceInEditor();

    void CopyInferenceDraft();

    /// <summary>表の行を選んだ (Hex ビューでそのフィールドの範囲を選ぶ)。</summary>
    void InferenceRowSelected(int index);

    /// <summary>入力式の文脈 (作業中のドキュメント)。</summary>
    Core.Expressions.IExpressionContext? InferenceExpressionContext { get; }

    /// <summary>パネルを表示した・閉じた (Hex ビューのプレビューを描き直す)。</summary>
    void InferencePanelVisibilityChanged();
}

/// <summary>
/// 「テンプレートの推定」パネル (TPL-40 の仕様 1。既定は右)。上から入力 (TPL-38 の仕様 1) と「推定」、レコード長の候補、状態、フィールドの表、
/// 下書きのプレビュー (読み取り専用。折りたためる)、ボタン (「適用」「エディタで開く」「コピー」)。状態はドキュメントごと
/// (<see cref="TemplateInferenceSession"/>) で、このパネルは作業中のドキュメントの状態を表示する。
/// </summary>
public sealed partial class TemplateInferencePanel : UserControl, ICodeEditorKeys
{
    /// <summary>この幅 (epx) 未満では表の「理由」の列を省く (TPL-40 の画面)。</summary>
    public const double NarrowWidth = 400;

    private readonly ITemplateInferenceHost _host;
    private readonly TextBox _start = new() { MinWidth = 90 };
    private readonly TextBox _length = new() { MinWidth = 90 };
    private readonly TextBox _recordLength = new() { MinWidth = 70 };
    private readonly TextBox _count = new() { MinWidth = 70 };
    private readonly ComboBox _endian = new() { MinWidth = 100 };
    private readonly Button _run = new();
    private readonly Button _reinfer = new();
    private readonly TextBlock _block = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListView _candidates = new() { MaxHeight = 120, SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListView _table = new() { SelectionMode = ListViewSelectionMode.Single, IsDoubleTapEnabled = true };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBox _preview = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = DialogParts.Mono, MaxHeight = 220,
    };
    private readonly Expander _previewExpander = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button _apply = new();
    private readonly Button _openEditor = new();
    private readonly Button _copy = new();
    private readonly MenuFlyout _rowMenu = new();
    private TemplateInferenceSession? _session;
    private bool _updating;
    private bool _narrow;

    public TemplateInferencePanel(ITemplateInferenceHost host)
    {
        _host = host;
        AutomationProperties.SetAutomationId(this, "TemplateInferencePanel");

        // 入力
        var inputs = new WrapPanel();
        AddField(inputs, _start, "TplInfer_Start");
        AddField(inputs, _length, "TplInfer_Length");
        AddField(inputs, _recordLength, "TplInfer_RecordLength");
        _recordLength.PlaceholderText = Loc.Get("TplInfer_Auto");
        AddField(inputs, _count, "TplInfer_Count");
        _count.PlaceholderText = Loc.Get("TplInfer_FillRange");
        foreach (string key in new[] { "TplInfer_Endian_Auto", "TplInfer_Endian_Little", "TplInfer_Endian_Big" })
        {
            _endian.Items.Add(new ComboBoxItem { Content = Loc.Get(key) });
        }

        _endian.SelectedIndex = 0;
        _endian.Header = Loc.Get("TplInfer_Endian");
        AutomationProperties.SetAutomationId(_endian, "TplInfer_Endian");
        AutomationProperties.SetName(_endian, Loc.Get("TplInfer_Endian"));
        _endian.SelectionChanged += (_, _) => InputChanged();
        inputs.Children.Add(_endian);
        foreach (TextBox box in new[] { _start, _length, _recordLength, _count })
        {
            box.TextChanged += (_, _) => InputChanged();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter && _run.IsEnabled)
                {
                    e.Handled = true;
                    _ = _host.RunInferenceAsync();
                }
            };
        }

        SetButton(_run, "TplInfer_Run", () => _ = _host.RunInferenceAsync());
        _run.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        _run.VerticalAlignment = VerticalAlignment.Bottom;
        inputs.Children.Add(_run);
        SetButton(_reinfer, "TplInfer_Reinfer", () => _ = _host.RunInferenceAsync(keepManual: true));
        _reinfer.VerticalAlignment = VerticalAlignment.Bottom;
        inputs.Children.Add(_reinfer);

        _block.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        AutomationProperties.SetAutomationId(_block, "TplInfer_BlockReason");
        AutomationProperties.SetLiveSetting(_block, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        // レコード長の候補
        AutomationProperties.SetAutomationId(_candidates, "TplInfer_Candidates");
        AutomationProperties.SetName(_candidates, Loc.Get("TplInfer_Candidates"));
        _candidates.SelectionChanged += (_, _) =>
        {
            if (!_updating && _candidates.SelectedItem is ListViewItem { Tag: RecordLengthCandidate c } && c.Length != _session?.Result?.RecordLength)
            {
                _ = _host.RunInferenceAsync(c.Length);
            }
        };

        AutomationProperties.SetAutomationId(_status, "TplInfer_Status");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _status.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

        // フィールドの表
        AutomationProperties.SetAutomationId(_table, "TplInfer_Table");
        AutomationProperties.SetName(_table, Loc.Get("TplInfer_Table"));
        _table.SelectionChanged += (_, _) =>
        {
            UpdateDetails();
            if (!_updating && _table.SelectedIndex >= 0)
            {
                _host.InferenceRowSelected(_table.SelectedIndex);
            }
        };
        _table.DoubleTapped += (_, _) => _ = RenameAsync();
        BuildRowMenu();
        _table.ContextFlyout = _rowMenu;
        AutomationProperties.SetAutomationId(_details, "TplInfer_Details");
        _details.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

        // 下書きのプレビュー
        AutomationProperties.SetAutomationId(_preview, "TplInfer_Preview");
        AutomationProperties.SetName(_preview, Loc.Get("TplInfer_Preview"));
        _previewExpander.Header = Loc.Get("TplInfer_Preview");
        _previewExpander.Content = _preview;
        _previewExpander.IsExpanded = true;
        AutomationProperties.SetAutomationId(_previewExpander, "TplInfer_PreviewExpander");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        SetButton(_apply, "TplInfer_Apply", _host.ApplyInference);
        SetButton(_openEditor, "TplInfer_OpenEditor", _host.OpenInferenceInEditor);
        SetButton(_copy, "TplInfer_Copy", _host.CopyInferenceDraft);
        buttons.Children.Add(_apply);
        buttons.Children.Add(_openEditor);
        buttons.Children.Add(_copy);

        var root = new Grid { RowSpacing = 6, Padding = new Thickness(12, 8, 12, 8) };
        UIElement[] parts = [inputs, _block, _candidates, _status, _table, _details, _previewExpander, buttons];
        for (int i = 0; i < parts.Length; i++)
        {
            root.RowDefinitions.Add(new RowDefinition { Height = parts[i] == _table ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetRow((FrameworkElement)parts[i], i);
            root.Children.Add(parts[i]);
        }

        Content = root;
        Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width < NarrowWidth;
            if (narrow != _narrow)
            {
                _narrow = narrow;
                RefreshTable();
            }
        };
        Loaded += (_, _) => _host.InferencePanelVisibilityChanged();
        Unloaded += (_, _) => _host.InferencePanelVisibilityChanged();
        Refresh();
    }

    /// <summary>表示している状態。</summary>
    public TemplateInferenceSession? Session => _session;

    public TextBox RecordLengthBox => _recordLength;

    public ListView Table => _table;

    /// <summary>名前を変える入力欄 (開いていれば。テスト用の命令)。</summary>
    internal TextBox? RenameBox { get; private set; }

    internal TextBlock? RenameError { get; private set; }

    internal Flyout? RenameFlyout { get; private set; }

    internal Flyout? SplitFlyout { get; private set; }

    internal TextBox? SplitBox { get; private set; }

    internal MenuFlyout RowMenu => _rowMenu;

    // ───────────── 状態の表示 ─────────────

    /// <summary>表示する状態を変える (作業中のドキュメントが変わった)。</summary>
    public void Bind(TemplateInferenceSession? session)
    {
        if (ReferenceEquals(session, _session))
        {
            Refresh();
            return;
        }

        _session = session;
        Refresh();
    }

    /// <summary>入力欄の値を状態に入れる。</summary>
    private void InputChanged()
    {
        if (_updating || _session is not { } s)
        {
            return;
        }

        s.StartText = _start.Text;
        s.LengthText = _length.Text;
        s.RecordLengthText = _recordLength.Text;
        s.RecordCountText = _count.Text;
        s.Endian = (InferenceEndian)Math.Max(0, _endian.SelectedIndex);
        UpdateRunState();
    }

    /// <summary>状態を入力から作る (誤りなら理由)。</summary>
    public static (TemplateInferenceRequest? Request, string? Error) ParseRequest(TemplateInferenceSession s, Core.Expressions.IExpressionContext? context, long documentLength)
    {
        if (context is null)
        {
            return (null, Loc.Get("TplInfer_NoDocument"));
        }

        if (!DialogParts.TryEvaluate(s.StartText, context, out long start, out _))
        {
            return (null, Loc.Get("TplInfer_BadStart"));
        }

        if (!DialogParts.TryEvaluate(s.LengthText, context, out long length, out _) || length < 0)
        {
            return (null, Loc.Get("TplInfer_BadLength"));
        }

        int? k = null;
        if (!string.IsNullOrWhiteSpace(s.RecordLengthText))
        {
            if (!long.TryParse(s.RecordLengthText.Trim().Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.CurrentCulture, out long kv)
                && !DialogParts.TryEvaluate(s.RecordLengthText, context, out kv, out _, Core.Expressions.DefaultRadix.Decimal))
            {
                return (null, Loc.Format("TplInfer_Block_RecordLength", 1, TemplateInferenceRequest.MaxRecordLength));
            }

            if (kv is < 1 or > TemplateInferenceRequest.MaxRecordLength)
            {
                return (null, Loc.Format("TplInfer_Block_RecordLength", 1, TemplateInferenceRequest.MaxRecordLength));
            }

            k = (int)kv;
        }

        long? count = null;
        if (!string.IsNullOrWhiteSpace(s.RecordCountText))
        {
            if (!long.TryParse(s.RecordCountText.Trim().Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.CurrentCulture, out long n)
                && !DialogParts.TryEvaluate(s.RecordCountText, context, out n, out _, Core.Expressions.DefaultRadix.Decimal))
            {
                return (null, Loc.Get("TplInfer_BadCount"));
            }

            count = n;
        }

        var request = new TemplateInferenceRequest { Start = start, Length = length, RecordLength = k, RecordCount = count, Endian = s.Endian };
        return request.Validate(documentLength) is { } reason ? (request, Loc.Format(reason.Key, reason.Args)) : (request, null);
    }

    private void UpdateRunState()
    {
        if (_session is not { } s)
        {
            _run.IsEnabled = false;
            _block.Text = string.Empty;
            return;
        }

        (_, string? error) = ParseRequest(s, _host.InferenceExpressionContext, s.Document.Length);
        _run.IsEnabled = error is null;
        _block.Text = error ?? string.Empty;
        _block.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(_run, error);
    }

    /// <summary>全体を表示し直す。</summary>
    public void Refresh()
    {
        _updating = true;
        try
        {
            TemplateInferenceSession? s = _session;
            _start.Text = s?.StartText ?? string.Empty;
            _length.Text = s?.LengthText ?? string.Empty;
            _recordLength.Text = s?.RecordLengthText ?? string.Empty;
            _count.Text = s?.RecordCountText ?? string.Empty;
            _endian.SelectedIndex = (int)(s?.Endian ?? InferenceEndian.Auto);
            _candidates.Items.Clear();
            foreach (RecordLengthCandidate c in s?.Candidates ?? [])
            {
                string text = Loc.Format("TplInfer_Candidate", c.Length, c.Score.ToString("0.000", CultureInfo.CurrentCulture), c.RecordCount);
                var item = new ListViewItem { Content = text, Tag = c };
                AutomationProperties.SetName(item, text);
                _candidates.Items.Add(item);
                if (c.Length == s!.Result?.RecordLength)
                {
                    _candidates.SelectedItem = item;
                }
            }

            _candidates.Visibility = _candidates.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = StatusText(s);
            _preview.Text = s?.Draft ?? string.Empty;
            bool hasDraft = !string.IsNullOrEmpty(s?.Draft) && s!.Table.Rows.Count > 0;
            _apply.IsEnabled = _openEditor.IsEnabled = _copy.IsEnabled = hasDraft;
            _reinfer.IsEnabled = s?.Result?.HasFields == true;
        }
        finally
        {
            _updating = false;
        }

        UpdateRunState();
        RefreshTable();
    }

    /// <summary>状態の文 (使ったレコードの数、エンディアン、端数、見つからなかった旨)。</summary>
    public static string StatusText(TemplateInferenceSession? s)
    {
        if (s is null)
        {
            return Loc.Get("TplInfer_NoDocument");
        }

        if (s.Running)
        {
            return Loc.Get("TplInfer_Running");
        }

        if (s.Result is not { } r)
        {
            return string.Empty;
        }

        if (r.Blocked is { } blocked)
        {
            return Loc.Format(blocked.Key, blocked.Args);
        }

        if (r.NotFound)
        {
            return Loc.Get("TplInfer_NotFound") + " " + Loc.Get("TplInfer_VariableLength");
        }

        var parts = new List<string>
        {
            Loc.Format("TplInfer_StatusRecords", r.RecordLength ?? 0, r.RecordCount, r.SampleCount),
            Loc.Get(r.BigEndian ? "TplInfer_StatusBig" : "TplInfer_StatusLittle"),
        };
        if (r.UnreadableRecords > 0)
        {
            parts.Add(Loc.Format("TplInfer_StatusUnreadable", r.UnreadableRecords));
        }

        if (r.Remainder > 0)
        {
            parts.Add(Loc.Format("TplInfer_Remainder", r.Remainder));
        }

        if (s.InternalError is { } error)
        {
            parts.Add(Loc.Format("TplInfer_InternalError", error));
        }

        return string.Join(" ", parts);
    }

    /// <summary>表を作り直す (選んでいた行は残す)。</summary>
    public void RefreshTable()
    {
        int selected = _table.SelectedIndex;
        _updating = true;
        try
        {
            _table.Items.Clear();
            IReadOnlyList<InferenceRow> rows = _session?.Table.Rows ?? [];
            for (int i = 0; i < rows.Count; i++)
            {
                _table.Items.Add(MakeRow(rows[i]));
            }

            if (selected >= 0 && selected < rows.Count)
            {
                _table.SelectedIndex = selected;
            }
        }
        finally
        {
            _updating = false;
        }

        UpdateDetails();
    }

    public static string KindText(InferredKind kind) => Loc.Get("TplInfer_Kind_" + kind);

    public static string ConfidenceText(InferenceConfidence confidence) => Loc.Get("TplInfer_Confidence_" + confidence);

    /// <summary>行の読み上げ: オフセット、名前、型、種類、確度 (確度 低 の注意と「手で変更」の印も文字で。TPL-40 の仕様 8)。</summary>
    public static string RowName(InferenceRow row)
    {
        string text = Loc.Format("TplInfer_RowName", "0x" + row.Offset.ToString("X2", CultureInfo.InvariantCulture), row.Name ?? Loc.Get("TplInfer_NoName"),
            row.TypeDisplay, KindText(row.Kind), ConfidenceText(row.Confidence));
        if (row.IsLowConfidence)
        {
            text += " " + Loc.Get("TplInfer_LowWarning");
        }

        if (row.IsManual)
        {
            text += " " + Loc.Get("TplInfer_Manual");
        }

        return text;
    }

    private ListViewItem MakeRow(InferenceRow row)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        double[] widths = [48, 36, 110, 120, 96, 80];
        string?[] texts =
        [
            "0x" + row.Offset.ToString("X2", CultureInfo.InvariantCulture),
            row.Size.ToString(CultureInfo.CurrentCulture),
            row.Name ?? "—",
            row.TypeDisplay,
            KindText(row.Kind),
            (row.IsLowConfidence ? "⚠ " : string.Empty) + ConfidenceText(row.Confidence) + (row.IsManual ? " · " + Loc.Get("TplInfer_ManualShort") : string.Empty),
        ];
        for (int c = 0; c < widths.Length; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(widths[c]) });
            var t = new TextBlock { Text = texts[c], TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (c is 0 or 3)
            {
                t.FontFamily = DialogParts.Mono;
            }

            if (c == 5 && row.IsLowConfidence)
            {
                t.Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            }

            Grid.SetColumn(t, c);
            grid.Children.Add(t);
        }

        if (!_narrow)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var reason = new TextBlock
            {
                Text = Loc.Format(row.Reason.Key, row.Reason.Args),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            Grid.SetColumn(reason, widths.Length);
            grid.Children.Add(reason);
        }

        var item = new ListViewItem { Content = grid, Tag = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, RowName(row));
        AutomationProperties.SetAutomationId(item, "TplInfer_Row_" + row.Offset.ToString("X2", CultureInfo.InvariantCulture));
        return item;
    }

    /// <summary>選んだ行の理由の全文とほかの候補 (TPL-40 の仕様 3「行を開くと」)。</summary>
    private void UpdateDetails()
    {
        if (SelectedRow is not { } row)
        {
            _details.Text = string.Empty;
            _details.Visibility = Visibility.Collapsed;
            return;
        }

        var lines = new List<string> { Loc.Format(row.Reason.Key, row.Reason.Args) };
        if (row.NearMiss is { } near)
        {
            lines.Add(Loc.Format(near.Key, near.Args));
        }

        if (row.Candidates.Count > 0)
        {
            lines.Add(Loc.Format("TplInfer_Alternatives", string.Join(" / ", row.Candidates.Select(c => KindText(c.Kind) + " (" + ConfidenceText(c.Confidence) + ")"))));
        }

        _details.Text = string.Join("\n", lines);
        _details.Visibility = Visibility.Visible;
    }

    public InferenceRow? SelectedRow => _table.SelectedIndex >= 0 && _session is { } s && _table.SelectedIndex < s.Table.Rows.Count
        ? s.Table.Rows[_table.SelectedIndex] : null;

    /// <summary>表の行を選ぶ (Hex ビューのカーソルに合わせる。表から Hex ビューへは知らせない)。</summary>
    public void SelectRowQuietly(int index)
    {
        if (index == _table.SelectedIndex || index < 0 || index >= _table.Items.Count)
        {
            return;
        }

        _updating = true;
        try
        {
            _table.SelectedIndex = index;
            _table.ScrollIntoView(_table.Items[index]);
        }
        finally
        {
            _updating = false;
        }

        UpdateDetails();
    }

    /// <summary>レコード長の入力欄にフォーカスを移す (固定長のレコードが見つからなかった。TPL-38 の仕様 4)。</summary>
    public void FocusRecordLength() => _recordLength.Focus(FocusState.Programmatic);

    public void FocusTable()
    {
        if (_table.SelectedIndex < 0 && _table.Items.Count > 0)
        {
            _table.SelectedIndex = 0;
        }

        _table.Focus(FocusState.Programmatic);
    }

    // ───────────── 行の操作 (名前・型・結合・分割) ─────────────

    private void BuildRowMenu()
    {
        AutomationProperties.SetAutomationId(_rowMenu, "TplInfer_RowMenu");
        _rowMenu.Opening += (_, _) => UpdateRowMenu();
        UpdateRowMenu();
    }

    internal void UpdateRowMenu()
    {
        _rowMenu.Items.Clear();
        int index = _table.SelectedIndex;
        InferenceTable? table = _session?.Table;
        bool has = table is not null && index >= 0 && index < table.Rows.Count;
        _rowMenu.Items.Add(MenuItem("TplInfer_Menu_Rename", "N", has && table!.Rows[index].Type.Id != "padding", () => _ = RenameAsync()));
        var type = new MenuFlyoutSubItem { Text = Loc.Get("TplInfer_Menu_Type"), IsEnabled = has };
        AutomationProperties.SetAutomationId(type, "TplInfer_Menu_Type");
        if (has)
        {
            foreach (InferenceTypeChoice choice in table!.TypeChoices(index))
            {
                var item = new ToggleMenuFlyoutItem
                {
                    Text = choice.Display + (choice.Rule is { } r ? "  (" + KindText(r.Kind) + ", " + ConfidenceText(r.Confidence) + ")" : string.Empty),
                    IsChecked = table.Rows[index].Type.Id == choice.Type.Id,
                };
                AutomationProperties.SetAutomationId(item, "TplInfer_Type_" + choice.Type.Id);
                InferenceTypeChoice c = choice;
                item.Click += (_, _) => SetType(index, c);
                type.Items.Add(item);
            }
        }

        _rowMenu.Items.Add(type);
        _rowMenu.Items.Add(new MenuFlyoutSeparator());
        _rowMenu.Items.Add(MenuItem("TplInfer_Menu_Merge", "M", has && table!.CanMergeWithPrevious(index), () => table!.MergeWithPrevious(index)));
        _rowMenu.Items.Add(MenuItem("TplInfer_Menu_Split", "S", has && table!.CanSplit(index), () => _ = SplitAsync()));
    }

    private static MenuFlyoutItem MenuItem(string key, string accessKey, bool enabled, Action action)
    {
        var item = new MenuFlyoutItem { Text = Loc.Get(key), IsEnabled = enabled, AccessKey = accessKey };
        AutomationProperties.SetAutomationId(item, key);
        item.Click += (_, _) => action();
        return item;
    }

    public bool SetType(int index, InferenceTypeChoice choice) => _session?.Table.SetType(index, choice) == true;

    /// <summary>「名前を変更」: 行の上に名前の入力欄を出す。正しくない名前は赤枠と理由を出し、確定しない (TPL-40 の仕様 3)。</summary>
    public Task RenameAsync()
    {
        if (_session is not { } s || SelectedRow is not { Name: { } current } || _table.SelectedIndex < 0)
        {
            return Task.CompletedTask;
        }

        int index = _table.SelectedIndex;
        var box = new TextBox { Text = current, MinWidth = 200, FontFamily = DialogParts.Mono };
        AutomationProperties.SetAutomationId(box, "TplInfer_RenameBox");
        AutomationProperties.SetName(box, Loc.Get("TplInfer_Menu_Rename"));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 260, Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] };
        AutomationProperties.SetAutomationId(error, "TplInfer_RenameError");
        var ok = new Button { Content = Loc.Get("Common_Ok") };
        AutomationProperties.SetAutomationId(ok, "TplInfer_RenameOk");
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(box);
        panel.Children.Add(error);
        panel.Children.Add(ok);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Bottom };
        Brush normal = box.BorderBrush;
        void Validate()
        {
            InferenceMessage? problem = s.Table.ValidateName(index, box.Text);
            error.Text = problem is null ? string.Empty : Loc.Format(problem.Key, problem.Args);
            box.BorderBrush = problem is null ? normal : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            ok.IsEnabled = problem is null;
        }

        void Commit()
        {
            Validate();
            if (ok.IsEnabled && s.Table.Rename(index, box.Text))
            {
                flyout.Hide();
                FocusTable();
            }
        }

        box.TextChanged += (_, _) => Validate();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
        ok.Click += (_, _) => Commit();
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        flyout.Closed += (_, _) =>
        {
            RenameBox = null;
            RenameError = null;
            RenameFlyout = null;
        };
        RenameBox = box;
        RenameError = error;
        RenameFlyout = flyout;
        Validate();
        FrameworkElement target = _table.ContainerFromIndex(index) as FrameworkElement ?? _table;
        flyout.ShowAt(target);
        return Task.CompletedTask;
    }

    /// <summary>テスト用の命令: 開いている名前の入力欄に文字を入れて Enter を押す。</summary>
    internal bool CommitRenameForTest(string text)
    {
        if (RenameBox is not { } box)
        {
            return false;
        }

        box.Text = text;
        int index = _table.SelectedIndex;
        if (_session is { } s && s.Table.ValidateName(index, text) is null && s.Table.Rename(index, text))
        {
            RenameFlyout?.Hide();
            FocusTable();
            return true;
        }

        return false;
    }

    /// <summary>「分割...」: 分ける位置 (フィールドの先頭からのバイト数) を尋ねる。</summary>
    public Task SplitAsync()
    {
        if (_session is not { } s || SelectedRow is not { } row)
        {
            return Task.CompletedTask;
        }

        int index = _table.SelectedIndex;
        var box = new TextBox { Text = (row.Size / 2).ToString(CultureInfo.CurrentCulture), MinWidth = 120, Header = Loc.Format("TplInfer_SplitAt", row.Size - 1) };
        AutomationProperties.SetAutomationId(box, "TplInfer_SplitBox");
        AutomationProperties.SetName(box, Loc.Format("TplInfer_SplitAt", row.Size - 1));
        var ok = new Button { Content = Loc.Get("Common_Ok") };
        AutomationProperties.SetAutomationId(ok, "TplInfer_SplitOk");
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var flyout = new Flyout { Content = panel };
        void Commit()
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int at) && s.Table.Split(index, at))
            {
                flyout.Hide();
                FocusTable();
            }
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                Commit();
            }
        };
        ok.Click += (_, _) => Commit();
        flyout.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) =>
        {
            SplitFlyout = null;
            SplitBox = null;
        };
        SplitFlyout = flyout;
        SplitBox = box;
        flyout.ShowAt(_table.ContainerFromIndex(index) as FrameworkElement ?? _table);
        return Task.CompletedTask;
    }

    /// <summary>テスト用の命令: 開いている分割の入力欄に位置を入れて確定する。</summary>
    internal bool CommitSplitForTest(int at)
    {
        if (SplitBox is null || _session is not { } s)
        {
            return false;
        }

        bool done = s.Table.Split(_table.SelectedIndex, at);
        SplitFlyout?.Hide();
        return done;
    }

    // ───────────── キー (TPL-40 の仕様 3・7) ─────────────

    /// <summary>
    /// パネルにフォーカスがあるときの Ctrl+Z / Ctrl+Y は表の変更の取り消し・やり直し (ドキュメントの Undo とは別の履歴)。入力欄の中では
    /// 入力欄の取り消しにする。
    /// </summary>
    public bool HandleEditorKey(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        // 入力欄 (パネルの中のもの) では入力欄の取り消しにする
        if (!ctrl || alt || _session is not { } s || XamlRoot is null
            || (FocusManager.GetFocusedElement(XamlRoot) is TextBox box && Contains(box)))
        {
            return false;
        }

        if (key == VirtualKey.Z && !shift)
        {
            return s.Table.Undo() || true;
        }

        if (key == VirtualKey.Y || (key == VirtualKey.Z && shift))
        {
            return s.Table.Redo() || true;
        }

        return false;
    }

    // ───────────── 部品 ─────────────

    private bool Contains(DependencyObject node)
    {
        for (DependencyObject? n = node; n is not null; n = VisualTreeHelper.GetParent(n))
        {
            if (ReferenceEquals(n, this))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddField(Panel parent, TextBox box, string key)
    {
        box.Header = Loc.Get(key);
        AutomationProperties.SetAutomationId(box, key);
        AutomationProperties.SetName(box, Loc.Get(key));
        box.Margin = new Thickness(0, 0, 8, 4);
        parent.Children.Add(box);
    }

    private static void SetButton(Button button, string key, Action action)
    {
        button.Content = Loc.Get(key);
        button.Margin = new Thickness(0, 0, 8, 4);
        AutomationProperties.SetAutomationId(button, key);
        button.Click += (_, _) => action();
    }
}
