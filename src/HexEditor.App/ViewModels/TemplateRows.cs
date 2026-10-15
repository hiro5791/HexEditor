using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Presentation;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.ViewModels;

/// <summary>結果ツリーの列 (TPL-21 の仕様 1)。</summary>
public enum TemplateColumn
{
    Name,
    Value,
    Type,
    Offset,
    Size,
    Comment,
    Endian,
    End,
    Color,
}

/// <summary>
/// 結果ツリーの列の配置 (表示・幅・順序。TPL-21 の仕様 1)。行のテンプレートは列ごとの欄の位置・幅・表示をこの値に結び付ける。
/// 名前・値・型・オフセット・サイズ・コメントが既定で表示され、エンディアン・終わりのオフセット・色は選べる。state.json に保存する。
/// </summary>
public sealed partial class TemplateColumnLayout : ObservableObject
{
    public static readonly TemplateColumn[] DefaultOrder =
        [TemplateColumn.Name, TemplateColumn.Value, TemplateColumn.Type, TemplateColumn.Offset, TemplateColumn.Size, TemplateColumn.Comment,
            TemplateColumn.Endian, TemplateColumn.End, TemplateColumn.Color];

    private static readonly Dictionary<TemplateColumn, double> DefaultWidths = new()
    {
        [TemplateColumn.Name] = 180, [TemplateColumn.Value] = 160, [TemplateColumn.Type] = 90, [TemplateColumn.Offset] = 90,
        [TemplateColumn.Size] = 70, [TemplateColumn.Comment] = 160, [TemplateColumn.Endian] = 60, [TemplateColumn.End] = 90, [TemplateColumn.Color] = 40,
    };

    private readonly List<TemplateColumn> _order = [.. DefaultOrder];
    private readonly HashSet<TemplateColumn> _visible = [TemplateColumn.Name, TemplateColumn.Value, TemplateColumn.Type, TemplateColumn.Offset,
        TemplateColumn.Size, TemplateColumn.Comment];

    private readonly Dictionary<TemplateColumn, double> _widths = new(DefaultWidths);

    public TemplateColumnLayout() => Load();

    public IReadOnlyList<TemplateColumn> Order => _order;

    public bool IsVisible(TemplateColumn c) => _visible.Contains(c);

    public double WidthOf(TemplateColumn c) => _widths[c];

    public void SetVisible(TemplateColumn c, bool visible)
    {
        if (c == TemplateColumn.Name)
        {
            return; // 名前の列は常に出す
        }

        if (visible ? _visible.Add(c) : _visible.Remove(c))
        {
            Changed();
        }
    }

    public void SetWidth(TemplateColumn c, double width)
    {
        _widths[c] = Math.Clamp(width, 24, 2000);
        Changed();
    }

    /// <summary>列を左右に動かす (順序を変える)。</summary>
    public void Move(TemplateColumn c, int delta)
    {
        int i = _order.IndexOf(c);
        int j = Math.Clamp(i + delta, 0, _order.Count - 1);
        if (i != j)
        {
            _order.RemoveAt(i);
            _order.Insert(j, c);
            Changed();
        }
    }

    public void Reset()
    {
        _order.Clear();
        _order.AddRange(DefaultOrder);
        _visible.Clear();
        _visible.UnionWith([TemplateColumn.Name, TemplateColumn.Value, TemplateColumn.Type, TemplateColumn.Offset, TemplateColumn.Size, TemplateColumn.Comment]);
        foreach ((TemplateColumn c, double w) in DefaultWidths)
        {
            _widths[c] = w;
        }

        Changed();
    }

    private void Changed()
    {
        Save();
        OnPropertyChanged(string.Empty);
    }

    private void Load()
    {
        if (Commands.CommandService.State?.Get(TemplateService.ColumnsStateKey) is not JsonArray list)
        {
            return;
        }

        var order = new List<TemplateColumn>();
        var visible = new HashSet<TemplateColumn>();
        foreach (JsonNode? n in list)
        {
            if (n is JsonObject o && Enum.TryParse((string?)o["id"], ignoreCase: true, out TemplateColumn c) && !order.Contains(c))
            {
                order.Add(c);
                if ((bool?)o["visible"] ?? true)
                {
                    visible.Add(c);
                }

                if ((double?)o["width"] is { } w && w > 0)
                {
                    _widths[c] = w;
                }
            }
        }

        order.AddRange(DefaultOrder.Where(c => !order.Contains(c)));
        _order.Clear();
        _order.AddRange(order);
        _visible.Clear();
        _visible.UnionWith(visible);
        _visible.Add(TemplateColumn.Name);
    }

    private void Save() => Commands.CommandService.State?.Set(TemplateService.ColumnsStateKey,
        new JsonArray([.. _order.Select(c => (JsonNode?)new JsonObject { ["id"] = c.ToString(), ["visible"] = _visible.Contains(c), ["width"] = _widths[c] })]));

    // ---- 行のテンプレートが結び付ける値 ----

    private int ColumnOf(TemplateColumn c) => _order.IndexOf(c);

    private GridLength Width(TemplateColumn c) => _visible.Contains(c) ? new GridLength(_widths[c]) : new GridLength(0);

    private Visibility Shown(TemplateColumn c) => _visible.Contains(c) ? Visibility.Visible : Visibility.Collapsed;

    public GridLength Width0 => Width(_order[0]);

    public GridLength Width1 => Width(_order[1]);

    public GridLength Width2 => Width(_order[2]);

    public GridLength Width3 => Width(_order[3]);

    public GridLength Width4 => Width(_order[4]);

    public GridLength Width5 => Width(_order[5]);

    public GridLength Width6 => Width(_order[6]);

    public GridLength Width7 => Width(_order[7]);

    public GridLength Width8 => Width(_order[8]);

    public int NameColumn => ColumnOf(TemplateColumn.Name);

    public int ValueColumn => ColumnOf(TemplateColumn.Value);

    public int TypeColumn => ColumnOf(TemplateColumn.Type);

    public int OffsetColumn => ColumnOf(TemplateColumn.Offset);

    public int SizeColumn => ColumnOf(TemplateColumn.Size);

    public int CommentColumn => ColumnOf(TemplateColumn.Comment);

    public int EndianColumn => ColumnOf(TemplateColumn.Endian);

    public int EndColumn => ColumnOf(TemplateColumn.End);

    public int ColorColumn => ColumnOf(TemplateColumn.Color);

    public Visibility ValueVisibility => Shown(TemplateColumn.Value);

    public Visibility TypeVisibility => Shown(TemplateColumn.Type);

    public Visibility OffsetVisibility => Shown(TemplateColumn.Offset);

    public Visibility SizeVisibility => Shown(TemplateColumn.Size);

    public Visibility CommentVisibility => Shown(TemplateColumn.Comment);

    public Visibility EndianVisibility => Shown(TemplateColumn.Endian);

    public Visibility EndVisibility => Shown(TemplateColumn.End);

    public Visibility ColorVisibility => Shown(TemplateColumn.Color);
}

/// <summary>結果ツリーの 1 行の表示 (TPL-21 の仕様 1・2・8)。<see cref="TemplateTreeItem"/> の写しから作る。</summary>
public sealed partial class TemplateRowViewModel : ObservableObject
{
    public TemplateRowViewModel(TemplateTreeItem item, TemplateRowList owner)
    {
        Item = item;
        Owner = owner;
    }

    public TemplateTreeItem Item { get; }

    public TemplateRowList Owner { get; }

    public TemplateColumnLayout Layout => Owner.Layout;

    private TemplateRowData? Data => Item.Data;

    public Thickness Indent => new(Item.Depth * 16, 0, 0, 0);

    /// <summary>開閉の印 (子を持たない行は空)。</summary>
    public string ExpanderGlyph => !Item.HasChildren ? string.Empty : Item.IsExpanded ? "" : "";

    public string KindGlyph => Item.Kind == TemplateTreeItemKind.Group ? "" : GlyphOf(Data);

    private static string GlyphOf(TemplateRowData? d) => d is null ? string.Empty
        : d.Error is not null || d.Kind == TemplateNodeKind.Error ? ""
        : d.Warning is not null || d.UndefinedEnumValue ? ""
        : d.Kind switch
        {
            TemplateNodeKind.Application => "",
            TemplateNodeKind.Struct => "",
            TemplateNodeKind.Union => "",
            TemplateNodeKind.Array => "",
            TemplateNodeKind.Pointer => "",
            TemplateNodeKind.Enum => "",
            TemplateNodeKind.Flags => "",
            TemplateNodeKind.Bitfield or TemplateNodeKind.BitfieldMember => "",
            TemplateNodeKind.String or TemplateNodeKind.Char => "",
            TemplateNodeKind.Section => "",
            TemplateNodeKind.Computed => "",
            _ => "",
        };

    /// <summary>エラー・警告の文字 (色だけにしない。TPL-21 の「エラー」)。</summary>
    public string StatusText => Data is null ? string.Empty
        : Data.Error is not null || Data.Kind == TemplateNodeKind.Error ? Loc.Get("TplSeverity_Error")
        : Data.Warning is not null ? Loc.Get("TplSeverity_Warning")
        : Data.UndefinedEnumValue ? Loc.Get("Tpl_UndefinedValueShort")
        : Data.IsFollow ? Loc.Get("Tpl_FollowLabel")
        : string.Empty;

    public Visibility StatusVisibility => StatusText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Name => Item.Kind == TemplateTreeItemKind.Application ? Owner.ApplicationName(Item) : Data?.Name ?? "…";

    public string Value => Data?.Value ?? string.Empty;

    public string Type => Item.Kind == TemplateTreeItemKind.Group ? Loc.Format("Tpl_GroupCount", Item.GroupCount.ToString("N0", CultureInfo.CurrentCulture))
        : Data?.Type ?? string.Empty;

    public string OffsetText => Data is null ? string.Empty : Owner.FormatOffset(Data.Offset, Data.BitOffset);

    public string SizeText => Data is null ? string.Empty
        : Data.BitSize is { } bits ? Loc.Format("Tpl_BitSize", bits)
        : Owner.FormatNumber(Data.Size);

    public string EndText => Data is null ? string.Empty : Owner.FormatOffset(Data.End, null);

    public string EndianText => Data is null || Item.Kind == TemplateTreeItemKind.Group ? string.Empty : Data.BigEndian ? "BE" : "LE";

    public string Comment => Data?.Comment ?? string.Empty;

    public string ColorText => Data?.ColorName ?? string.Empty;

    /// <summary>名前の左の色見本 (<c>color</c> 属性の色)。</summary>
    public Brush? AttributeSwatch => Data?.ColorName is { } name && TemplateColoring.Parse(name) is { } c && Owner.Scope is { } scope
        ? TemplateBrushes.Swatch(c, scope) : null;

    public Visibility AttributeSwatchVisibility => AttributeSwatch is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>色の型の値の色見本 (rgb565 など。TC-TPL-03-04)。</summary>
    public Brush? ValueSwatch => Data?.ValueArgb is { } argb
        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb)) : null;

    public Visibility ValueSwatchVisibility => ValueSwatch is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>ツールチップ (パス・型・説明・コメント・日時・ビット・警告)。</summary>
    public string ToolTipText
    {
        get
        {
            if (Data is not { } d)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            if (d.Path.Length > 0)
            {
                sb.Append(d.Path).Append(" : ").Append(d.Type);
            }
            else
            {
                sb.Append(Name);
            }

            if (d.DocComment is { Length: > 0 } doc)
            {
                sb.Append('\n').Append(doc);
            }

            if (d.Docs is { Length: > 0 } docs)
            {
                sb.Append('\n').Append(docs);
            }

            if (d.Comment is { Length: > 0 } comment)
            {
                sb.Append('\n').Append(comment);
            }

            if (d.DateTimes is { } dt)
            {
                sb.Append('\n').Append(Loc.Format("Tpl_DateTimeTip", dt.Utc, dt.Local));
            }

            if (d.BitOffset is { } bo && d.BitSize is { } bs)
            {
                sb.Append('\n').Append(Loc.Format("Tpl_Bits", bo, bo + bs - 1));
            }

            if (d.UndefinedEnumValue)
            {
                sb.Append('\n').Append(Loc.Get("Tpl_UndefinedValue"));
            }

            if (d.Error is { } e)
            {
                sb.Append('\n').Append(Loc.Get("TplSeverity_Error")).Append(": ").Append(TemplateDiagnosticText.Message(e));
            }

            if (d.Warning is { } w)
            {
                sb.Append('\n').Append(Loc.Get("TplSeverity_Warning")).Append(": ").Append(TemplateDiagnosticText.Message(w));
            }

            if (d.PointerTarget is { } target)
            {
                sb.Append('\n').Append(Loc.Format("Tpl_PointsTo", Owner.FormatOffset(target, null)));
            }

            return sb.ToString();
        }
    }

    /// <summary>スクリーンリーダーの読み上げ (名前、型、値、オフセット、サイズ。警告は「警告: メッセージ」。TPL-21 の仕様 8)。</summary>
    public string AccessibleName
    {
        get
        {
            var parts = new List<string> { Name };
            if (Type.Length > 0)
            {
                parts.Add(Type);
            }

            if (Value.Length > 0)
            {
                parts.Add(Value);
            }

            if (Data is { } d)
            {
                parts.Add(Loc.Format("Tpl_A11yOffset", OffsetText));
                parts.Add(Loc.Format("Tpl_A11ySize", SizeText));
                if (d.Error is { } e)
                {
                    parts.Add(Loc.Get("TplSeverity_Error") + ": " + TemplateDiagnosticText.Message(e));
                }
                else if (d.Warning is { } w)
                {
                    parts.Add(Loc.Get("TplSeverity_Warning") + ": " + TemplateDiagnosticText.Message(w));
                }
                else if (d.UndefinedEnumValue)
                {
                    parts.Add(Loc.Get("TplSeverity_Warning") + ": " + Loc.Get("Tpl_UndefinedValue"));
                }
            }

            return string.Join(", ", parts);
        }
    }

    /// <summary>表示を更新する (行の内容・開閉・列の配置)。</summary>
    public void Update() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// 結果ツリーの行の一覧 (ListView の ItemsSource)。<see cref="TemplateTreeModel"/> の表示中の行を写し、行の表示用の項目は表示するときに
/// 作る (開いている行が 100 万行でも、項目は表示中の分だけ。TPL-21 の仕様 4)。
/// </summary>
public sealed class TemplateRowList : IList, IReadOnlyList<object>, INotifyCollectionChanged
{
    private readonly ConditionalWeakTable<TemplateTreeItem, TemplateRowViewModel> _rows = new();

    public TemplateRowList(TemplateTreeModel model, TemplateColumnLayout layout)
    {
        Model = model;
        Layout = layout;
        model.Changed += Model_Changed;
    }

    public TemplateTreeModel Model { get; }

    public TemplateColumnLayout Layout { get; }

    /// <summary>色見本のテーマを決める要素 (パネル)。</summary>
    public FrameworkElement? Scope { get; set; }

    /// <summary>オフセットの基数 (Hex ビューの設定に従う。TPL-21 の仕様 1)。</summary>
    public OffsetRadix Radix { get; set; } = OffsetRadix.Hex;

    /// <summary>適用の行の名前 (テンプレート名、開始オフセット、「(未保存)」)。</summary>
    public Func<TemplateTreeItem, string>? ApplicationNameOf { get; set; }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>行の内容がない行が表示された (読み込みを頼む)。</summary>
    public event EventHandler<TemplateRowViewModel>? NeedsData;

    public int Count => Model.RowCount;

    public object this[int index] => Row(Model.ItemAt(index));

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public TemplateRowViewModel Row(TemplateTreeItem item)
    {
        TemplateRowViewModel row = _rows.GetValue(item, i => new TemplateRowViewModel(i, this));
        if (item.Data is null)
        {
            NeedsData?.Invoke(this, row);
        }

        return row;
    }

    /// <summary>作ってある行の項目 (なければ null)。</summary>
    public TemplateRowViewModel? Existing(TemplateTreeItem item) => _rows.TryGetValue(item, out TemplateRowViewModel? row) ? row : null;

    public string ApplicationName(TemplateTreeItem item) => ApplicationNameOf?.Invoke(item) ?? item.Data?.Name ?? string.Empty;

    public string FormatNumber(long value) => Radix switch
    {
        OffsetRadix.Decimal => value.ToString("N0", CultureInfo.CurrentCulture),
        OffsetRadix.Octal => "0o" + Convert.ToString(value, 8),
        _ => "0x" + value.ToString("X", CultureInfo.InvariantCulture),
    };

    /// <summary>オフセット (ビットフィールドの要素は <c>0x10.3</c> の形。TPL-06 の仕様 6)。</summary>
    public string FormatOffset(long offset, int? bit) => FormatNumber(offset) + (bit is { } b ? "." + b.ToString(CultureInfo.InvariantCulture) : string.Empty);

    private void Model_Changed(object? sender, TemplateTreeChange change)
    {
        switch (change.Action)
        {
            case TemplateTreeChangeAction.Update:
                for (int i = change.Index; i < change.Index + change.Count && i < Count; i++)
                {
                    Existing(Model.ItemAt(i))?.Update();
                }

                break;
            default:
                // 行の追加・削除は作り直しで知らせる (一覧は行の数を位置の計算から読むので、1 行ずつの通知と数が食い違わないように)
                CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                break;
        }
    }

    /// <summary>作り直す (列の配置・基数が変わった)。</summary>
    public void Refresh() => CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    public int IndexOf(object? value) => value is TemplateRowViewModel r ? Model.IndexOf(r.Item) : -1;

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator<object> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool IsFixedSize => false;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }
}
