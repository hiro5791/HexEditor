using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Compare;
using CompareOptions = HexEditor.Core.Compare.CompareOptions;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>比較対象の候補 (ダイアログの「対象」の項目)。</summary>
public sealed record CompareCandidate(string Label, CompareSourceKind Kind, DocumentViewModel? Document);

/// <summary>
/// 「ファイルを比較」ダイアログの中身 (ANA-01 の「画面」)。左右 2 列に「対象」(ドキュメントの一覧・「保存済みの内容」・「ファイルを選択...」)、
/// 開始オフセット、長さ (00 の 9 章の入力欄: 解釈結果を横に出し、不正な入力は赤枠と説明文で示す)。中央に「左右を入れ替え」、下に比較方式と
/// 方式ごとのオプション。入力が正しいときだけ「比較」を押せる (<see cref="ValidityChanged"/>)。
/// </summary>
public sealed partial class CompareDialog : Grid
{
    private readonly IReadOnlyList<CompareCandidate> _candidates;
    private readonly Side _left;
    private readonly Side _right;
    private readonly RadioButton _simple;
    private readonly RadioButton _insertDelete;
    private readonly ComboBox _unit;
    private readonly TextBox _mergeGap;
    private readonly TextBox _window;
    private readonly TextBox _minMatch;
    private readonly TextBlock _optionsError;
    private readonly TextBlock _warning;
    private readonly StackPanel _insertDeleteOptions;

    public CompareDialog(IReadOnlyList<CompareCandidate> candidates, CompareOptions options, int leftIndex, int rightIndex)
    {
        _candidates = candidates;
        ColumnSpacing = 12;
        RowSpacing = 12;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _left = new Side(this, "Left", Loc.Get("Compare_Dialog_Left"), leftIndex);
        _right = new Side(this, "Right", Loc.Get("Compare_Dialog_Right"), rightIndex);
        Grid.SetColumn(_right.Panel, 2);
        Children.Add(_left.Panel);
        Children.Add(_right.Panel);

        var swap = new Button
        {
            Content = new FontIcon { Glyph = "" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        string swapName = Loc.Get("Compare_Dialog_Swap");
        AutomationProperties.SetName(swap, swapName);
        AutomationProperties.SetAutomationId(swap, "CompareDialog_Swap");
        ToolTipService.SetToolTip(swap, swapName);
        swap.Click += (_, _) => Swap();
        Grid.SetColumn(swap, 1);
        Children.Add(swap);

        // 比較方式とオプション (ANA-01 の仕様 3・4)。
        var methods = new StackPanel { Spacing = 4 };
        methods.Children.Add(new TextBlock { Text = Loc.Get("Compare_Dialog_Method"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        _simple = DialogParts.Radio("CompareDialog_Simple", Loc.Get("Compare_Method_Simple"), "CompareMethod", options.Method == CompareMethod.Simple);
        _insertDelete = DialogParts.Radio("CompareDialog_InsertDelete", Loc.Get("Compare_Method_InsertDelete"), "CompareMethod",
            options.Method == CompareMethod.InsertDelete);
        methods.Children.Add(_simple);
        methods.Children.Add(_insertDelete);
        _unit = DialogParts.Combo("CompareDialog_Unit", Loc.Get("Compare_Option_Unit"),
            CompareOptions.Units.Select(u => Loc.Format("Compare_Option_UnitValue", u)), Math.Max(0, CompareOptions.Units.ToList().IndexOf(options.Unit)));
        _mergeGap = DialogParts.Field("CompareDialog_MergeGap", Loc.Get("Compare_Option_MergeGap"), options.MergeGap.ToString(CultureInfo.InvariantCulture));
        _window = DialogParts.Field("CompareDialog_Window", Loc.Get("Compare_Option_Window"), options.Window.ToString(CultureInfo.InvariantCulture));
        _minMatch = DialogParts.Field("CompareDialog_MinMatch", Loc.Get("Compare_Option_MinMatch"), options.MinMatch.ToString(CultureInfo.InvariantCulture));
        _optionsError = DialogParts.Caption("CompareDialog_OptionsError");
        _insertDeleteOptions = new StackPanel { Spacing = 8 };
        _insertDeleteOptions.Children.Add(_window);
        _insertDeleteOptions.Children.Add(_minMatch);
        var optionPanel = new StackPanel { Spacing = 8 };
        optionPanel.Children.Add(_unit);
        optionPanel.Children.Add(_mergeGap);
        optionPanel.Children.Add(_insertDeleteOptions);
        optionPanel.Children.Add(_optionsError);
        var expander = new Expander
        {
            Header = Loc.Get("Compare_Dialog_Options"),
            Content = optionPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(expander, "CompareDialog_Options");
        methods.Children.Add(expander);
        Grid.SetRow(methods, 1);
        Grid.SetColumnSpan(methods, 3);
        Children.Add(methods);

        _warning = DialogParts.Caption("CompareDialog_Warning");
        Grid.SetRow(_warning, 2);
        Grid.SetColumnSpan(_warning, 3);
        Children.Add(_warning);

        foreach (TextBox box in new[] { _mergeGap, _window, _minMatch })
        {
            box.TextChanged += (_, _) => Validate();
        }

        _unit.SelectionChanged += (_, _) => Validate();
        _simple.Checked += (_, _) => Validate();
        _insertDelete.Checked += (_, _) => Validate();
        Validate();
    }

    /// <summary>入力の正しさが変わった (「比較」ボタンの有効・無効)。</summary>
    public event EventHandler? ValidityChanged;

    public bool IsValid { get; private set; }

    /// <summary>「ファイルを選択...」の「参照」でファイルを選ぶ処理 (ウィンドウが設定する)。</summary>
    public Func<Task<string?>>? BrowseFile { get; set; }

    /// <summary>左右の指定 (正しい入力のとき)。</summary>
    public (CompareTargetSpec Left, CompareTargetSpec Right) Targets => (_left.Spec!, _right.Spec!);

    /// <summary>方式とオプション (正しい入力のとき)。</summary>
    public CompareOptions Options { get; private set; } = new();

    // ---- テスト用の命令からも使う操作 ----

    public void SetTarget(bool right, int index) => (right ? _right : _left).Target.SelectedIndex = index;

    public void SetPath(bool right, string path) => (right ? _right : _left).Path.Text = path;

    public void SetRange(bool right, string? start, string? length)
    {
        Side side = right ? _right : _left;
        if (start is not null)
        {
            side.Start.Text = start;
        }

        if (length is not null)
        {
            side.Length.Text = length;
        }
    }

    public void SetMethod(CompareMethod method)
    {
        _simple.IsChecked = method == CompareMethod.Simple;
        _insertDelete.IsChecked = method == CompareMethod.InsertDelete;
        Validate();
    }

    public void SetOptions(string? window, string? minMatch, string? mergeGap, int? unit)
    {
        if (window is not null)
        {
            _window.Text = window;
        }

        if (minMatch is not null)
        {
            _minMatch.Text = minMatch;
        }

        if (mergeGap is not null)
        {
            _mergeGap.Text = mergeGap;
        }

        if (unit is { } u && CompareOptions.Units.ToList().IndexOf(u) is var i and >= 0)
        {
            _unit.SelectedIndex = i;
        }
    }

    /// <summary>「左右を入れ替え」(ANA-01 の受け入れ基準 4): 対象・パス・開始・長さをまとめて入れ替える。</summary>
    public void Swap()
    {
        (int ti, string p, string s, string l) = (_left.Target.SelectedIndex, _left.Path.Text, _left.Start.Text, _left.Length.Text);
        _left.Target.SelectedIndex = _right.Target.SelectedIndex;
        _left.Path.Text = _right.Path.Text;
        _left.Start.Text = _right.Start.Text;
        _left.Length.Text = _right.Length.Text;
        _right.Target.SelectedIndex = ti;
        _right.Path.Text = p;
        _right.Start.Text = s;
        _right.Length.Text = l;
        Validate();
    }

    /// <summary>今の入力の状態 (テスト用の読み出し)。</summary>
    public System.Text.Json.Nodes.JsonObject State()
    {
        System.Text.Json.Nodes.JsonObject SideState(Side side) => new()
        {
            ["target"] = side.Target.SelectedIndex,
            ["targetLabel"] = side.Candidate?.Label,
            ["path"] = side.Path.Text,
            ["start"] = side.Start.Text,
            ["length"] = side.Length.Text,
            ["startText"] = side.StartInfo.Text,
            ["lengthText"] = side.LengthInfo.Text,
            ["error"] = side.TargetError.Text,
        };
        return new System.Text.Json.Nodes.JsonObject
        {
            ["left"] = SideState(_left),
            ["right"] = SideState(_right),
            ["candidates"] = new System.Text.Json.Nodes.JsonArray([.. _candidates.Select(c => (System.Text.Json.Nodes.JsonNode?)c.Label)]),
            ["method"] = _insertDelete.IsChecked == true ? "insertDelete" : "simple",
            ["window"] = _window.Text,
            ["minMatch"] = _minMatch.Text,
            ["mergeGap"] = _mergeGap.Text,
            ["unit"] = _unit.SelectedIndex >= 0 ? CompareOptions.Units[_unit.SelectedIndex] : null,
            ["valid"] = IsValid,
            ["warning"] = _warning.Text,
            ["optionsError"] = _optionsError.Text,
        };
    }

    // ---- 検証 ----

    private void Validate()
    {
        if (_left is null || _right is null || _optionsError is null)
        {
            return;
        }

        bool ok = _left.Validate() & _right.Validate();
        _insertDeleteOptions.Visibility = _insertDelete.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        var context = new LengthContext(0);
        string? optionsError = null;
        long Number(TextBox box, long min, long max, string key)
        {
            if (!ExpressionEvaluator.TryEvaluate(box.Text, context, out long v, out _, DefaultRadix.Decimal) || v < min || v > max)
            {
                optionsError ??= Loc.Format(key, min.ToString("N0", CultureInfo.CurrentCulture), max.ToString("N0", CultureInfo.CurrentCulture));
                DialogParts.MarkInvalid(box, true);
                return min;
            }

            DialogParts.MarkInvalid(box, false);
            return v;
        }

        long gap = Number(_mergeGap, 0, CompareOptions.MaxMergeGap, "Compare_Option_MergeGapError");
        long window = _insertDelete.IsChecked == true ? Number(_window, CompareOptions.MinWindow, CompareOptions.MaxWindow, "Compare_Option_WindowError") : CompareOptions.DefaultWindow;
        long minMatch = _insertDelete.IsChecked == true ? Number(_minMatch, CompareOptions.MinMinMatch, CompareOptions.MaxMinMatch, "Compare_Option_MinMatchError") : CompareOptions.DefaultMinMatch;
        if (_insertDelete.IsChecked != true)
        {
            // 単純比較では使わない項目は、記憶した値をそのまま残す。
            window = long.TryParse(_window.Text, out long w) && w is >= CompareOptions.MinWindow and <= CompareOptions.MaxWindow ? w : window;
            minMatch = long.TryParse(_minMatch.Text, out long m) && m is >= CompareOptions.MinMinMatch and <= CompareOptions.MaxMinMatch ? m : minMatch;
        }

        _optionsError.Text = optionsError ?? string.Empty;
        ok &= optionsError is null;
        Options = new CompareOptions
        {
            Method = _insertDelete.IsChecked == true ? CompareMethod.InsertDelete : CompareMethod.Simple,
            Unit = _unit.SelectedIndex >= 0 ? CompareOptions.Units[_unit.SelectedIndex] : 1,
            MergeGap = (int)gap,
            Window = (int)window,
            MinMatch = (int)minMatch,
        };

        // 左右に同じドキュメントの同じ範囲 (ANA-01 の「エラー」): 警告するが実行は許す。
        _warning.Text = ok && _left.Spec is { } l && _right.Spec is { } r && l.Kind == r.Kind && ReferenceEquals(l.Document, r.Document)
            && string.Equals(l.Path, r.Path, StringComparison.OrdinalIgnoreCase) && l.Start == r.Start && l.Length == r.Length
            ? Loc.Get("Compare_Dialog_SameRange")
            : string.Empty;
        if (ok != IsValid)
        {
            IsValid = ok;
            ValidityChanged?.Invoke(this, EventArgs.Empty);
        }

        IsValid = ok;
    }

    /// <summary>長さだけを持つ入力式の文脈 (ディスク上のファイルの範囲の入力)。</summary>
    public sealed class LengthContext(long length) : IExpressionContext
    {
        public long Cursor => 0;

        public long Length => length;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }

    /// <summary>片側の入力欄。</summary>
    private sealed class Side
    {
        private readonly CompareDialog _owner;

        public Side(CompareDialog owner, string id, string header, int index)
        {
            _owner = owner;
            Panel = new StackPanel { Spacing = 6, MinWidth = 260 };
            Panel.Children.Add(new TextBlock { Text = header, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            Target = DialogParts.Combo($"CompareDialog_{id}Target", Loc.Get("Compare_Dialog_Target"), owner._candidates.Select(c => c.Label), index);
            Target.HorizontalAlignment = HorizontalAlignment.Stretch;
            Path = DialogParts.Field($"CompareDialog_{id}Path", Loc.Get("Compare_Dialog_Path"));
            var browse = new Button { Content = Loc.Get("Compare_Dialog_Browse") };
            AutomationProperties.SetAutomationId(browse, $"CompareDialog_{id}Browse");
            browse.Click += async (_, _) =>
            {
                if (_owner.BrowseFile is { } pick && await pick() is { } chosen)
                {
                    Path.Text = chosen;
                }
            };
            PathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            PathRow.Children.Add(Path);
            PathRow.Children.Add(browse);
            TargetError = DialogParts.Caption($"CompareDialog_{id}TargetError");
            TargetError.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            Start = DialogParts.Field($"CompareDialog_{id}Start", Loc.Get("Compare_Dialog_Start"), "0");
            StartInfo = DialogParts.Caption($"CompareDialog_{id}StartInfo", monospace: true);
            Length = DialogParts.Field($"CompareDialog_{id}Length", Loc.Get("Compare_Dialog_Length"));
            Length.PlaceholderText = Loc.Get("Compare_Dialog_ToEnd");
            LengthInfo = DialogParts.Caption($"CompareDialog_{id}LengthInfo", monospace: true);
            foreach (UIElement e in new UIElement[] { Target, PathRow, TargetError, Start, StartInfo, Length, LengthInfo })
            {
                Panel.Children.Add(e);
            }

            Target.SelectionChanged += (_, _) => _owner.Validate();
            Path.TextChanged += (_, _) => _owner.Validate();
            Start.TextChanged += (_, _) => _owner.Validate();
            Length.TextChanged += (_, _) => _owner.Validate();
        }

        public StackPanel Panel { get; }

        public ComboBox Target { get; }

        public TextBox Path { get; }

        public StackPanel PathRow { get; }

        public TextBlock TargetError { get; }

        public TextBox Start { get; }

        public TextBlock StartInfo { get; }

        public TextBox Length { get; }

        public TextBlock LengthInfo { get; }

        public CompareCandidate? Candidate =>
            Target.SelectedIndex >= 0 && Target.SelectedIndex < _owner._candidates.Count ? _owner._candidates[Target.SelectedIndex] : null;

        public CompareTargetSpec? Spec { get; private set; }

        public bool Validate()
        {
            Spec = null;
            TargetError.Text = string.Empty;
            CompareCandidate? candidate = Candidate;
            PathRow.Visibility = candidate?.Kind == CompareSourceKind.File ? Visibility.Visible : Visibility.Collapsed;
            if (candidate is null)
            {
                TargetError.Text = Loc.Get("Compare_Dialog_NoTarget");
                return false;
            }

            long length;
            IExpressionContext context;
            string? path = null;
            switch (candidate.Kind)
            {
                case CompareSourceKind.File:
                    path = Path.Text.Trim().Trim('"');
                    string? reason = FileProblem(path, out length);
                    if (reason is not null)
                    {
                        TargetError.Text = reason;
                        return false;
                    }

                    context = new LengthContext(length);
                    break;
                case CompareSourceKind.Saved:
                    path = candidate.Document!.FilePath;
                    string? savedReason = FileProblem(path ?? string.Empty, out length);
                    if (savedReason is not null)
                    {
                        TargetError.Text = savedReason;
                        return false;
                    }

                    context = new LengthContext(length);
                    break;
                default:
                    length = candidate.Document!.Document.Length;
                    context = new EditorExpressionContext(candidate.Document.Editor);
                    break;
            }

            bool ok = true;
            long start = 0;
            if (!DialogParts.TryEvaluate(Start.Text, context, out start, out ExpressionException? startError))
            {
                StartInfo.Text = startError is null ? string.Empty : DialogParts.ExpressionError(startError);
                ok = false;
            }
            else if (start < 0 || start > length)
            {
                StartInfo.Text = Loc.Format("Compare_Dialog_StartBeyond", "0x" + length.ToString("X", CultureInfo.InvariantCulture));
                ok = false;
            }
            else
            {
                StartInfo.Text = DialogParts.Interpretation(start);
            }

            DialogParts.MarkInvalid(Start, !ok);
            long? rangeLength = null;
            bool lengthOk = true;
            if (!string.IsNullOrWhiteSpace(Length.Text))
            {
                if (!DialogParts.TryEvaluate(Length.Text, context, out long l, out ExpressionException? lengthError))
                {
                    LengthInfo.Text = lengthError is null ? string.Empty : DialogParts.ExpressionError(lengthError);
                    lengthOk = false;
                }
                else if (l < 0 || (ok && l > length - start))
                {
                    LengthInfo.Text = Loc.Format("Compare_Dialog_LengthBeyond", "0x" + Math.Max(0, length - start).ToString("X", CultureInfo.InvariantCulture));
                    lengthOk = false;
                }
                else
                {
                    LengthInfo.Text = DialogParts.Interpretation(l);
                    rangeLength = l;
                }
            }
            else
            {
                LengthInfo.Text = Loc.Get("Compare_Dialog_ToEndInfo");
            }

            DialogParts.MarkInvalid(Length, !lengthOk);
            if (!ok || !lengthOk)
            {
                return false;
            }

            Spec = new CompareTargetSpec(candidate.Kind, candidate.Document, path, start, rangeLength);
            return true;
        }

        /// <summary>ファイルを開けない理由 (存在しない・アクセス拒否など。ANA-01 の「エラー」)。開けるなら null。</summary>
        private static string? FileProblem(string path, out long length)
        {
            length = 0;
            if (string.IsNullOrWhiteSpace(path))
            {
                return Loc.Get("Compare_Dialog_NoPath");
            }

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return Loc.Get("Compare_Dialog_NotFound");
                }

                using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                length = stream.Length;
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return Loc.Get("Compare_Dialog_AccessDenied");
            }
            catch (IOException ex)
            {
                return Loc.Format("Compare_Dialog_CannotOpen", ex.Message);
            }
            catch (ArgumentException)
            {
                return Loc.Get("Compare_Dialog_NotFound");
            }
        }
    }
}
