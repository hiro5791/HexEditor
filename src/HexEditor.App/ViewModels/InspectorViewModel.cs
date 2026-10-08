using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;
using HexEditor.Core.Settings;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>インスペクタの一覧の項目の種類: グループの見出し、行、GUID の構成要素。</summary>
public enum InspectorItemKind
{
    Group,
    Row,
    Component,
}

/// <summary>2 進の行のビット 1 つ (INSP-08)。<see cref="Index"/> は 0 が最下位。</summary>
public sealed partial class InspectorBitViewModel(InspectorItemViewModel row, int index) : ObservableObject
{
    public InspectorItemViewModel Row { get; } = row;

    public int Index { get; } = index;

    [ObservableProperty]
    public partial string Text { get; set; } = "0";

    public string ToolTip => Loc.Format("Inspector_BitToolTip", Index);

    public string AutomationId => $"Inspector_Bit_{Row.TypeId}_{Index}";
}

/// <summary>インスペクタの一覧の 1 項目。</summary>
public sealed partial class InspectorItemViewModel : ObservableObject
{
    public InspectorItemViewModel(InspectorItemKind kind, InspectorGroup group, string typeId = "", bool opposite = false, string componentId = "")
    {
        Kind = kind;
        Group = group;
        TypeId = typeId;
        Opposite = opposite;
        ComponentId = componentId;
        if (kind == InspectorItemKind.Row && InspectorTypes.IsBinary(typeId))
        {
            BitCount = InspectorTypes.Get(typeId).Size * 8;
            for (int i = BitCount - 1; i >= 0; i--)
            {
                Bits.Add(new InspectorBitViewModel(this, i));
            }
        }
    }

    public InspectorItemKind Kind { get; }

    public InspectorGroup Group { get; }

    public string TypeId { get; }

    public bool Opposite { get; }

    public string ComponentId { get; }

    /// <summary>項目を区別する鍵 (更新で同じ項目を使い回す)。</summary>
    public string Key => $"{Kind}/{Group}/{TypeId}/{Opposite}/{ComponentId}";

    public bool IsGroup => Kind == InspectorItemKind.Group;

    public bool IsBinary => BitCount > 0;

    public int BitCount { get; }

    public ObservableCollection<InspectorBitViewModel> Bits { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string Value { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ToolTip { get; set; }

    [ObservableProperty]
    public partial InspectorStatus Status { get; set; }

    /// <summary>この項目が解釈しているバイトの、起点からの位置と長さ (強調の範囲。INSP-18)。</summary>
    public int ByteOffset { get; set; }

    public int ByteCount { get; set; }

    /// <summary>エンディアン (この行の解釈に使ったもの)。</summary>
    public Endianness Endian { get; set; }

    /// <summary>グループの見出しを折りたたんでいる (INSP-01 の仕様 5)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool IsCollapsed { get; set; }

    /// <summary>構成要素を展開できる行 (GUID / UUID) か、展開しているか (INSP-11 の仕様 3)。</summary>
    [ObservableProperty]
    public partial bool CanExpand { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    public partial bool IsExpanded { get; set; }

    /// <summary>折りたたみ・展開の印: 閉じているなら ▸、開いているなら ▾。</summary>
    public string Chevron => (IsGroup ? IsCollapsed : !IsExpanded) ? "" : "";

    // ---- 書き換え (INSP-17) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditing))]
    public partial bool IsEditing { get; set; }

    public bool IsNotEditing => !IsEditing;

    [ObservableProperty]
    public partial string EditText { get; set; } = string.Empty;

    /// <summary>書き込まれるバイト列 (Hex)。</summary>
    [ObservableProperty]
    public partial string Preview { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = string.Empty;

    public bool HasError => Error.Length > 0;

    /// <summary>読み上げ用の名前: 「int32, 12,345」(INSP-01 の受け入れ基準 5)。</summary>
    public string AutomationName => IsGroup ? Name : $"{Name}, {Value}";

    public string AutomationId => Kind switch
    {
        InspectorItemKind.Group => "Inspector_Group_" + Group,
        InspectorItemKind.Component => $"Inspector_Component_{TypeId}_{ComponentId}",
        _ => "Inspector_Row_" + TypeId + (Opposite ? "_Opposite" : string.Empty),
    };

    /// <summary>2 進の行のビットの表示を値に合わせる。</summary>
    public void UpdateBits(ulong value, bool valid)
    {
        foreach (InspectorBitViewModel bit in Bits)
        {
            bit.Text = valid ? (((value >> bit.Index) & 1) != 0 ? "1" : "0") : "-";
        }
    }
}

/// <summary>
/// データインスペクタのパネル (INSP-01〜INSP-19)。起点のバイトを行の構成に従って解釈し、一覧の項目にする。表示の条件
/// (表示形式・タイムゾーン・行の構成) はアプリ全体の設定、エンディアンの選択はドキュメントごとの付随データ。
/// パネルを閉じている間は計算しない (INSP-01 の仕様 10)。
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    // ---- 設定のキー (settings.json。UI-23) ----
    public const string LayoutKey = "inspector.rows";
    public const string IntegerBaseKey = "inspector.integerBase";
    public const string FloatFormatKey = "inspector.floatFormat";
    public const string TimeZoneKey = "inspector.timeZone";
    public const string DateFormatKey = "inspector.dateFormat";
    public const string DigitGroupingKey = "inspector.digitGrouping";
    public const string HighlightKey = "inspector.highlightTarget";
    public const string CursorWithSelectionKey = "inspector.useCursorWithSelection";

    /// <summary>ドキュメントのエンディアン (VIEW-11 はフェーズ 2。それまではリトルエンディアン)。</summary>
    public const Endianness DocumentEndian = Endianness.Little;

    private readonly SettingsStore _settings;
    private readonly Dictionary<string, InspectorItemViewModel> _byKey = [];
    private readonly HashSet<InspectorGroup> _collapsed = [];
    private readonly HashSet<string> _expanded = [];
    private DocumentViewModel? _document;
    private DocumentAnnotations? _annotations;
    private InspectorItemViewModel? _hovered;
    private byte[] _data = [];
    private ByteState[] _states = [];

    public InspectorViewModel(SettingsStore settings)
    {
        _settings = settings;
        Layout = InspectorLayout.Parse(settings.GetString(LayoutKey, string.Empty));
    }

    /// <summary>一覧の項目 (見出し・行・構成要素を平らに並べたもの)。</summary>
    public ObservableCollection<InspectorItemViewModel> Items { get; } = [];

    /// <summary>強調する範囲 (INSP-18) が変わった。</summary>
    public event EventHandler? HighlightChanged;

    /// <summary>行の構成が変わった (行の設定の画面を作り直す)。</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>すべての行を新しい起点の値にした (性能のテストで更新までの時間を測る)。</summary>
    public event EventHandler? Refreshed;

    public InspectorLayout Layout { get; private set; }

    /// <summary>パネルが表示されている (計算する)。</summary>
    public bool IsActive { get; set; }

    public DocumentViewModel? Document => _document;

    [ObservableProperty]
    public partial string OriginText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EndianText { get; set; } = "LE";

    /// <summary>パネルの下部の「格納される値」(INSP-05 の仕様 4、INSP-15 の仕様 5)。</summary>
    [ObservableProperty]
    public partial string StoredText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InspectorItemViewModel? Selected { get; set; }

    /// <summary>起点 (選択範囲の先頭またはカーソル位置。INSP-01 の仕様 1)。</summary>
    public long Origin { get; private set; }

    public bool ReadOnly => _document is null || _document.Editor.ReadOnly || _document.Document.IsEditLocked;

    // ---- 表示の条件 ----

    public IntegerBase IntegerBase
    {
        get => _settings.GetString(IntegerBaseKey, "decimal") switch { "hex" => IntegerBase.Hexadecimal, "octal" => IntegerBase.Octal, _ => IntegerBase.Decimal };
        set
        {
            _settings.SetString(IntegerBaseKey, value switch { IntegerBase.Hexadecimal => "hex", IntegerBase.Octal => "octal", _ => "decimal" }, "decimal");
            Refresh();
        }
    }

    public FloatFormat FloatFormat
    {
        get => _settings.GetString(FloatFormatKey, "shortest") switch { "exponent" => FloatFormat.Exponent, "hex" => FloatFormat.HexFloat, _ => FloatFormat.Shortest };
        set
        {
            _settings.SetString(FloatFormatKey, value switch { FloatFormat.Exponent => "exponent", FloatFormat.HexFloat => "hex", _ => "shortest" }, "shortest");
            Refresh();
        }
    }

    public DateTimeZoneMode TimeZoneMode
    {
        get => _settings.GetString(TimeZoneKey, "local") == "utc" ? DateTimeZoneMode.Utc : DateTimeZoneMode.Local;
        set
        {
            _settings.SetString(TimeZoneKey, value == DateTimeZoneMode.Utc ? "utc" : "local", "local");
            Refresh();
        }
    }

    public DateTimeStyle DateTimeStyle
    {
        get => _settings.GetString(DateFormatKey, "regional") == "iso" ? DateTimeStyle.Iso8601 : DateTimeStyle.Regional;
        set
        {
            _settings.SetString(DateFormatKey, value == DateTimeStyle.Iso8601 ? "iso" : "regional", "regional");
            Refresh();
        }
    }

    /// <summary>設定「インスペクタの対象を強調する」(INSP-18 の仕様 4。既定オン)。</summary>
    public bool HighlightTarget => _settings.GetBool(HighlightKey, true);

    /// <summary>エンディアンの選択 (ドキュメントごと)。</summary>
    public InspectorEndianMode EndianMode
    {
        get => _annotations?.InspectorEndian ?? InspectorEndianMode.Document;
        set
        {
            if (_annotations is not null)
            {
                _annotations.InspectorEndian = value;
            }

            Refresh();
        }
    }

    public Endianness Endian => EndianMode switch
    {
        InspectorEndianMode.Little => Endianness.Little,
        InspectorEndianMode.Big => Endianness.Big,
        _ => DocumentEndian,
    };

    /// <summary>「ローカル時刻」の表示に使うタイムゾーン (テスト用のビルドでは差し替えられる)。</summary>
    public static TimeZoneInfo LocalTimeZone { get; set; } = TimeZoneInfo.Local;

    public InspectorOptions Options => new()
    {
        IntegerBase = IntegerBase,
        DigitGrouping = _settings.GetBool(DigitGroupingKey, true),
        FloatFormat = FloatFormat,
        TimeZoneMode = TimeZoneMode,
        DateTimeStyle = DateTimeStyle,
        Culture = CultureInfo.CurrentCulture,
        LocalTimeZone = LocalTimeZone,
        AnsiEncoding = AnsiEncoding,
        Text = InspectorStrings.Current,
        Time = TestableTime,
    };

    /// <summary>入力の <c>now</c> に使う時計 (テスト用のビルドでは固定できる)。</summary>
    public static TimeProvider TestableTime { get; set; } = TimeProvider.System;

    /// <summary>ANSI の行の文字コード: 表示中の文字コードが Unicode 以外ならそれ、Unicode ならシステムの ANSI (INSP-09 の仕様 1)。</summary>
    private Encoding? AnsiEncoding
    {
        get
        {
            if (_document?.Editor.TextEncoding is not { } encoding)
            {
                return null;
            }

            if (encoding.IsAscii)
            {
                return Encoding.ASCII;
            }

            int cp = encoding.CodePage;
            return cp is 65001 or 1200 or 1201 or 12000 or 12001 ? null : Encoding.GetEncoding(cp);
        }
    }

    // ---- ドキュメント ----

    public void Attach(DocumentViewModel? document, DocumentAnnotations? annotations)
    {
        CancelEdit();
        _document = document;
        _annotations = annotations;
        StoredText = string.Empty;
        Refresh();
    }

    /// <summary>行の構成を変える (アプリ全体。INSP-19 の仕様 6)。</summary>
    public void SetLayout(InspectorLayout layout)
    {
        Layout = layout;
        _settings.SetString(LayoutKey, layout.Serialize(), InspectorLayout.Default.Serialize());
        Refresh();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>設定ファイルの外部の編集を読み直す。</summary>
    public void ReloadSettings()
    {
        InspectorLayout layout = InspectorLayout.Parse(_settings.GetString(LayoutKey, string.Empty));
        if (!layout.Equals(Layout))
        {
            Layout = layout;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        Refresh();
    }

    /// <summary>起点のデータを読み直して、すべての行を更新する。</summary>
    public void Refresh()
    {
        UpdateEndianText();
        if (!IsActive || _document is not { } doc || doc.Document.IsDisposed)
        {
            if (_document is null)
            {
                Items.Clear();
                _byKey.Clear();
                OriginText = string.Empty;
            }

            return;
        }

        EditorState editor = doc.Editor;
        bool useCursor = !editor.HasSelection || _settings.GetBool(CursorWithSelectionKey, false);
        Origin = useCursor ? editor.Cursor : editor.SelectionStart;
        OriginText = Loc.Format("Inspector_Origin", Origin.ToString(Origin > uint.MaxValue ? "X16" : "X8", CultureInfo.InvariantCulture));

        DocumentSnapshot snapshot = doc.Document.Current;
        int count = (int)Math.Clamp(snapshot.Length - Origin, 0, InspectorDecoder.ReadLength);
        _data = new byte[count];
        _states = new ByteState[count];
        if (count > 0)
        {
            count = snapshot.ReadForDisplay(Origin, _data, _states);
            _data = _data[..count];
            _states = _states[..count];
        }

        InspectorOptions options = Options;
        IReadOnlyList<InspectorGroupResult> groups = DataInspector.Evaluate(_data, _states, Layout, Endian, options);
        var wanted = new List<InspectorItemViewModel>();
        foreach (InspectorGroupResult group in groups)
        {
            InspectorItemViewModel header = Item(new InspectorItemViewModel(InspectorItemKind.Group, group.Group));
            header.Name = Loc.Get("Inspector_Group_" + group.Group);
            header.IsCollapsed = _collapsed.Contains(group.Group);
            wanted.Add(header);
            if (header.IsCollapsed)
            {
                continue;
            }

            foreach (InspectorRowResult r in group.Rows)
            {
                InspectorItemViewModel row = Item(new InspectorItemViewModel(InspectorItemKind.Row, group.Group, r.TypeId, r.Opposite));
                row.Name = RowName(r.TypeId, r.Opposite, r.Endian);
                row.Endian = r.Endian;
                row.ByteOffset = 0;
                row.ByteCount = r.Value.ByteCount;
                row.Status = r.Value.Status;
                if (!row.IsEditing)
                {
                    row.Value = r.Value.Text;
                }

                row.ToolTip = r.Value.ToolTip ?? (ReadOnly ? Loc.Get("Inspector_ReadOnlyTip") : null);
                if (row.IsBinary)
                {
                    bool valid = r.Value.Status == InspectorStatus.Ok;
                    row.UpdateBits(valid ? InspectorDecoder.ReadBits(_data.AsSpan(0, row.BitCount / 8), r.Endian) : 0, valid);
                }

                row.CanExpand = r.Value.Components is { Count: > 0 };
                row.IsExpanded = _expanded.Contains(row.Key);
                wanted.Add(row);
                if (row.IsExpanded && r.Value.Components is { } components)
                {
                    foreach (InspectorComponent c in components)
                    {
                        InspectorItemViewModel part = Item(new InspectorItemViewModel(InspectorItemKind.Component, group.Group, r.TypeId, r.Opposite, c.Id));
                        part.Name = c.Name;
                        part.Value = c.Value;
                        part.ByteOffset = c.Offset;
                        part.ByteCount = c.Length;
                        wanted.Add(part);
                    }
                }
            }
        }

        Sync(wanted);
        HighlightChanged?.Invoke(this, EventArgs.Empty);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    private InspectorItemViewModel Item(InspectorItemViewModel candidate)
    {
        if (_byKey.TryGetValue(candidate.Key, out InspectorItemViewModel? existing))
        {
            return existing;
        }

        _byKey[candidate.Key] = candidate;
        return candidate;
    }

    /// <summary>一覧を新しい並びにする。同じ項目は使い回す (選択とフォーカスを保つ)。</summary>
    private void Sync(List<InspectorItemViewModel> wanted)
    {
        bool same = wanted.Count == Items.Count && wanted.Zip(Items).All(p => ReferenceEquals(p.First, p.Second));
        if (same)
        {
            return;
        }

        InspectorItemViewModel? selected = Selected;
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], wanted[i]))
            {
                continue;
            }

            int existing = Items.IndexOf(wanted[i]);
            if (existing > i)
            {
                Items.Move(existing, i);
            }
            else
            {
                Items.Insert(i, wanted[i]);
            }
        }

        while (Items.Count > wanted.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        if (selected is not null && Items.Contains(selected))
        {
            Selected = selected;
        }
    }

    /// <summary>行の名前: 型の名前、反対のエンディアンの行は「int32 (BE)」、エンディアンに従わない行は「(固定)」付き。</summary>
    public static string RowName(string typeId, bool opposite, Endianness endian)
    {
        string name = Loc.Get("Inspector_Row_" + typeId);
        if (opposite)
        {
            return Loc.Format("Inspector_RowOpposite", name, endian == Endianness.Big ? "BE" : "LE");
        }

        return InspectorTypes.Get(typeId).FixedEndian ? Loc.Format("Inspector_RowFixed", name) : name;
    }

    private void UpdateEndianText()
    {
        string doc = DocumentEndian == Endianness.Big ? "BE" : "LE";
        EndianText = EndianMode switch
        {
            InspectorEndianMode.Little => Loc.Format("Inspector_EndianFixed", "LE"),
            InspectorEndianMode.Big => Loc.Format("Inspector_EndianFixed", "BE"),
            _ => doc,
        };
    }

    // ---- 折りたたみ・展開 ----

    public void ToggleGroup(InspectorItemViewModel header)
    {
        if (!_collapsed.Remove(header.Group))
        {
            _collapsed.Add(header.Group);
        }

        Refresh();
    }

    public void ToggleExpand(InspectorItemViewModel row)
    {
        if (!row.CanExpand)
        {
            return;
        }

        if (!_expanded.Remove(row.Key))
        {
            _expanded.Add(row.Key);
        }

        Refresh();
    }

    // ---- 強調 (INSP-18) ----

    /// <summary>マウスを重ねている項目。</summary>
    public InspectorItemViewModel? Hovered
    {
        get => _hovered;
        set
        {
            if (_hovered != value)
            {
                _hovered = value;
                HighlightChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    partial void OnSelectedChanged(InspectorItemViewModel? value) => HighlightChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>強調する範囲 (起点からの絶対位置)。なければ null。入力中は書き込む範囲 (INSP-17 の仕様 8)。</summary>
    public (long Offset, long Length)? HighlightRange
    {
        get
        {
            if (!IsActive || !HighlightTarget || _document is null)
            {
                return null;
            }

            InspectorItemViewModel? item = Items.FirstOrDefault(i => i.IsEditing) ?? Hovered ?? Selected;
            if (item is null || item.IsGroup || item.ByteCount <= 0)
            {
                return null;
            }

            int count = item.IsEditing && _editBytes is { } bytes ? bytes.Length : item.ByteCount;
            long start = Origin + item.ByteOffset;
            long length = Math.Min(count, Math.Max(0, _document.Document.Length - start));
            return length > 0 ? (start, length) : null;
        }
    }

    // ---- 書き換え (INSP-17) ----

    private byte[]? _editBytes;

    /// <summary>行の値の入力を始める。書き換えられない行・読み取り専用では始めない。</summary>
    public bool BeginEdit(InspectorItemViewModel item)
    {
        if (item.Kind != InspectorItemKind.Row || ReadOnly || _document is null)
        {
            return false;
        }

        CancelEdit();
        item.EditText = InspectorDecoder.EditText(item.TypeId, _data, _states, item.Endian, Options);
        item.IsEditing = true;
        UpdateEdit(item, item.EditText);
        return true;
    }

    /// <summary>入力が変わった: 書き込むバイト列と誤りを更新する。</summary>
    public void UpdateEdit(InspectorItemViewModel item, string text)
    {
        if (!item.IsEditing || _document is null)
        {
            return;
        }

        item.EditText = text;
        InspectorEncodeResult result = Encode(item, text);
        _editBytes = result.Bytes;
        item.Preview = result.Bytes is { } b ? string.Join(' ', b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture))) : string.Empty;
        item.Error = result.Error ?? string.Empty;
        HighlightChanged?.Invoke(this, EventArgs.Empty);
    }

    private InspectorEncodeResult Encode(InspectorItemViewModel item, string text)
    {
        DocumentViewModel doc = _document!;
        long available = doc.Document.Length - Origin;
        return InspectorEncoder.Encode(item.TypeId, text, item.Endian, Options, available, new EditorExpressionContext(doc.Editor));
    }

    /// <summary>
    /// 確定する (Enter)。不正な入力なら false (入力欄のまま)。書き込みは編集モードに関係なく上書きで、1 回の Undo で戻せる。
    /// カーソルは動かさない (INSP-17 の仕様 3・5・6)。
    /// </summary>
    public bool CommitEdit(InspectorItemViewModel item)
    {
        if (!item.IsEditing || _document is not { } doc)
        {
            return false;
        }

        InspectorEncodeResult result = Encode(item, item.EditText);
        if (result.Bytes is not { } bytes || ReadOnly)
        {
            item.Error = result.Error ?? Loc.Get("Inspector_ReadOnlyTip");
            return false;
        }

        item.IsEditing = false;
        _editBytes = null;
        Write(doc, bytes);
        StoredText = result.StoredText is { } stored ? Loc.Format("Inspector_StoredValue", stored) : string.Empty;
        Refresh();
        return true;
    }

    /// <summary>入力を取り消す (Esc、フォーカスが外れた)。</summary>
    public void CancelEdit()
    {
        foreach (InspectorItemViewModel item in Items.Where(i => i.IsEditing).ToList())
        {
            item.IsEditing = false;
            item.Error = string.Empty;
            item.Preview = string.Empty;
        }

        _editBytes = null;
        HighlightChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>2 進の行のビットを反転して書き込む (INSP-08 の仕様 3。1 回の Undo)。</summary>
    public bool FlipBit(InspectorItemViewModel row, int bit)
    {
        if (!row.IsBinary || ReadOnly || _document is not { } doc || row.Status != InspectorStatus.Ok)
        {
            return false;
        }

        int size = row.BitCount / 8;
        byte[] flipped = InspectorEncoder.FlipBit(_data.AsSpan(0, size), bit, row.Endian);
        Write(doc, flipped);
        Refresh();
        return true;
    }

    private void Write(DocumentViewModel doc, byte[] bytes)
    {
        // 上書きで書き、長さを変えない。末尾を越える書き込みは入力の段階で誤りにしてある。
        doc.Document.Overwrite(Origin, bytes, Loc.Get("Inspector_UndoDescription"));
    }

    // ---- コピー (INSP-01 の仕様 7) ----

    public static string CopyText(InspectorItemViewModel item) => item.Value;

    /// <summary>すべての行 (タブ区切りのテキスト)。</summary>
    public string AllRowsText() =>
        string.Join("\r\n", Items.Where(i => i.Kind != InspectorItemKind.Group).Select(i => $"{i.Name}\t{i.Value}"));

    public void HideRow(InspectorItemViewModel item)
    {
        if (item.Kind == InspectorItemKind.Row)
        {
            SetLayout(Layout.WithVisible(item.TypeId, false));
        }
    }
}

/// <summary>インスペクタの表示・誤りの文を、UI の言語のリソースから作る。</summary>
public static class InspectorStrings
{
    private static InspectorText? _current;

    public static InspectorText Current => _current ??= new InspectorText
    {
        NotEnoughTip = Loc.Get("Inspector_NotEnough"),
        Unreadable = Loc.Get("Inspector_Unreadable"),
        Invalid = Loc.Get("Inspector_Invalid"),
        ReasonContinuation = Loc.Get("Inspector_ReasonContinuation"),
        ReasonIncomplete = Loc.Get("Inspector_ReasonIncomplete"),
        ReasonOverlong = Loc.Get("Inspector_ReasonOverlong"),
        ReasonSurrogate = Loc.Get("Inspector_ReasonSurrogate"),
        ReasonTooLarge = Loc.Get("Inspector_ReasonTooLarge"),
        ReasonBadLead = Loc.Get("Inspector_ReasonBadLead"),
        ReasonMissingContinuation = Loc.Get("Inspector_ReasonMissingContinuation"),
        ReasonUnpairedHigh = Loc.Get("Inspector_ReasonUnpairedHigh"),
        ReasonUnpairedLow = Loc.Get("Inspector_ReasonUnpairedLow"),
        ReasonUndefined = Loc.Get("Inspector_ReasonUndefined"),
        OneByte = Loc.Get("Inspector_OneByte"),
        Bytes = Loc.Get("Inspector_Bytes"),
        Denormal = Loc.Get("Inspector_Denormal"),
        OutOfRange = Loc.Get("Inspector_OutOfRange"),
        FieldMonth = Loc.Get("Inspector_FieldMonth"),
        FieldDay = Loc.Get("Inspector_FieldDay"),
        FieldHour = Loc.Get("Inspector_FieldHour"),
        FieldMinute = Loc.Get("Inspector_FieldMinute"),
        FieldSecond = Loc.Get("Inspector_FieldSecond"),
        NoTimeZone = Loc.Get("Inspector_NoTimeZone"),
        Utc = Loc.Get("Inspector_Utc"),
        UtcOffset = Loc.Get("Inspector_UtcOffset"),
        ComponentVersion = Loc.Get("Inspector_ComponentVersion"),
        ComponentVariant = Loc.Get("Inspector_ComponentVariant"),
        ComponentTimestamp = Loc.Get("Inspector_ComponentTimestamp"),
        ComponentClockSequence = Loc.Get("Inspector_ComponentClockSequence"),
        ComponentNode = Loc.Get("Inspector_ComponentNode"),
        ComponentUnixTime = Loc.Get("Inspector_ComponentUnixTime"),
        RandomNode = Loc.Get("Inspector_RandomNode"),
        VersionUnknown = Loc.Get("Inspector_VersionUnknown"),
        VariantReserved = Loc.Get("Inspector_VariantReserved"),
        ErrorIntegerRange = Loc.Get("Inspector_ErrorIntegerRange"),
        ErrorInteger = Loc.Get("Inspector_ErrorInteger"),
        ErrorFloat = Loc.Get("Inspector_ErrorFloat"),
        ErrorFloatOverflow = Loc.Get("Inspector_ErrorFloatOverflow"),
        ErrorDate = Loc.Get("Inspector_ErrorDate"),
        ErrorDateRange = Loc.Get("Inspector_ErrorDateRange"),
        ErrorGuid = Loc.Get("Inspector_ErrorGuid"),
        ErrorBinary = Loc.Get("Inspector_ErrorBinary"),
        ErrorChar = Loc.Get("Inspector_ErrorChar"),
        ErrorNotEncodable = Loc.Get("Inspector_ErrorNotEncodable"),
        ErrorPastEnd = Loc.Get("Inspector_ErrorPastEnd"),
        ErrorReadOnly = Loc.Get("Inspector_ReadOnlyTip"),
    };
}
