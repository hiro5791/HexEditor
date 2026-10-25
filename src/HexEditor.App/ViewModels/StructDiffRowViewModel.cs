using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Compare.Structure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.ViewModels;

/// <summary>構造の差分パネルの 1 行 (ANA-32 の仕様 2)。表示する行の分だけ作る。</summary>
public sealed partial class StructDiffRowViewModel : ObservableObject
{
    internal StructDiffRowViewModel(StructDiffRow row, StructDiffView view, StructCompareResult result, bool hexOffsets, FrameworkElement scope)
    {
        Row = row;
        StructDiffNode? n = row.Node;
        Node = n;
        Indent = new Thickness(Math.Max(0, row.Depth) * 16, 0, 0, 0);
        ExpanderGlyph = !row.HasChildren ? string.Empty : row.IsExpanded ? "" : "";
        if (n is null)
        {
            // 「構造の外の差分」の行 (ANA-31 の仕様 8)
            StructOutsideDiffs o = result.Outside ?? default;
            Name = Loc.Get("StructDiff_Outside");
            KindText = Loc.Format("StructDiff_OutsideValue", o.Count.ToString("N0", CultureInfo.CurrentCulture), o.Bytes.ToString("N0", CultureInfo.CurrentCulture));
            AccessibleName = Name + ", " + KindText;
            AutomationId = "StructDiff_Row_outside";
            return;
        }

        Name = n.Name;
        AutomationId = "StructDiff_Row_" + (n.HasLeft || !n.HasRight ? n.LeftPath : n.RightPath);
        bool container = n.Kind is StructDiffKind.None || (row.HasChildren && n.Kind == StructDiffKind.Same);
        if (container)
        {
            IReadOnlyDictionary<StructDiffKind, long> counts = view.CountsOf(n);
            KindText = string.Join(Loc.Get("StructDiff_CountSeparator"), counts.Select(c => KindName(c.Key) + " " + c.Value.ToString("N0", CultureInfo.CurrentCulture)));
            SwatchVisibility = Visibility.Collapsed;
        }
        else
        {
            KindText = KindName(n.Kind) + (n.IsPadding ? " " + Loc.Get("StructDiff_PaddingMark") : string.Empty)
                + (n.OutsideRange ? " " + Loc.Get("StructDiff_OutsideMark") : string.Empty);
            KindGlyph = GlyphOf(n.Kind);
            (Swatch, MarkBrush) = BrushesOf(n.Kind, scope);
        }

        string Off(long v) => hexOffsets ? "0x" + v.ToString("X", CultureInfo.InvariantCulture) : v.ToString("N0", CultureInfo.CurrentCulture);
        LeftOffset = n.HasLeft || n.Kind is StructDiffKind.Added ? Off(n.LeftOffset) : string.Empty;
        RightOffset = n.HasRight || n.Kind is StructDiffKind.Removed ? Off(n.RightOffset) : string.Empty;
        SizeText = n.HasLeft && n.HasRight && n.LeftSize != n.RightSize ? Off(n.LeftSize) + " → " + Off(n.RightSize)
            : Off(n.HasLeft ? n.LeftSize : n.RightSize);
        TypeText = n.RightTypeName is { } rt && rt != n.TypeName ? n.TypeName + " → " + rt : n.TypeName;
        MatchText = n.Array is { } info ? MatchLabel(info) : string.Empty;
        LeftValue = n.Kind == StructDiffKind.Unparsed ? UnparsedText(n, right: false) : n.LeftDisplay ?? string.Empty;
        RightValue = n.Kind == StructDiffKind.Unparsed ? UnparsedText(n, right: true) : n.RightDisplay ?? string.Empty;
        if (n.Kind == StructDiffKind.Same && !n.IsContainer && n.LeftDisplay is null)
        {
            // 一致した行の値は評価の結果から求める (評価器のスレッドで。UI を待たせない)
            _ = Task.Run(() => (result.DisplayOf(n, false), result.DisplayOf(n, true))).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    scope.DispatcherQueue.TryEnqueue(() =>
                    {
                        LeftValue = t.Result.Item1;
                        RightValue = t.Result.Item2;
                    });
                }
            }, TaskScheduler.Default);
        }

        AccessibleName = container
            ? Loc.Format("StructDiff_ContainerName", Name, KindText)
            : Loc.Format("StructDiff_LeafName", KindName(n.Kind), n.DisplayPath, LeftValue, RightValue, LeftOffset, RightOffset);
        ToolTipText = n.Kind == StructDiffKind.Unparsed && n.Diagnostic is { } d ? d.ToString() : n.DisplayPath;
    }

    public StructDiffRow Row { get; }

    public StructDiffNode? Node { get; }

    public Thickness Indent { get; }

    public string ExpanderGlyph { get; }

    public string Name { get; }

    public string KindText { get; } = string.Empty;

    public string KindGlyph { get; } = string.Empty;

    public Brush? Swatch { get; }

    public Brush? MarkBrush { get; }

    public Visibility SwatchVisibility { get; } = Visibility.Visible;

    [ObservableProperty]
    public partial string LeftValue { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RightValue { get; set; } = string.Empty;

    public string TypeText { get; } = string.Empty;

    public string LeftOffset { get; } = string.Empty;

    public string RightOffset { get; } = string.Empty;

    public string SizeText { get; } = string.Empty;

    public string MatchText { get; } = string.Empty;

    public string ToolTipText { get; } = string.Empty;

    /// <summary>スクリーンリーダー向けの名前 (ANA-32 の仕様 9)。</summary>
    public string AccessibleName { get; }

    public string AutomationId { get; }

    /// <summary>種類の表示名 (表示言語)。</summary>
    public static string KindName(StructDiffKind kind) => Loc.Get(kind switch
    {
        StructDiffKind.Changed => "StructDiff_Kind_Changed",
        StructDiffKind.Added => "StructDiff_Kind_Added",
        StructDiffKind.Removed => "StructDiff_Kind_Removed",
        StructDiffKind.Moved => "StructDiff_Kind_Moved",
        StructDiffKind.MovedChanged => "StructDiff_Kind_MovedChanged",
        StructDiffKind.TypeChanged => "StructDiff_Kind_TypeChanged",
        StructDiffKind.Unparsed => "StructDiff_Kind_Unparsed",
        _ => "StructDiff_Kind_Same",
    });

    /// <summary>対応付けの列の文字 (「キー: type」「内容」「位置」)。</summary>
    public static string MatchLabel(StructArrayInfo info)
    {
        string text = info.Method switch
        {
            StructMatchMethod.Key => Loc.Format("StructDiff_Match_Key", info.KeyField ?? string.Empty),
            StructMatchMethod.Content => Loc.Get("StructDiff_Match_Content"),
            _ => Loc.Get("StructDiff_Match_Index"),
        };
        return info.TooManyForContent ? text + " " + Loc.Get("StructDiff_TooManyForContent") : text;
    }

    /// <summary>「解析できない」の行の値の列: 原因の側に理由を書く (「解析できない (右: エラーの理由)」)。</summary>
    private static string UnparsedText(StructDiffNode n, bool right)
    {
        bool cause = right ? n.ReasonRight : n.ReasonLeft;
        if (!cause)
        {
            return string.Empty;
        }

        return n.Reason switch
        {
            StructUnparsedReason.Limit => Loc.Get("StructDiff_Reason_Limit"),
            StructUnparsedReason.TooDeep => Loc.Get("StructDiff_Reason_TooDeep"),
            _ => n.Diagnostic is { } d ? d.Code + " " + Templates.TemplateDiagnosticText.Message(d) : Loc.Get("StructDiff_Reason_Error"),
        };
    }

    private static string GlyphOf(StructDiffKind kind) => kind switch
    {
        StructDiffKind.Changed => "",
        StructDiffKind.Added => "",
        StructDiffKind.Removed => "",
        StructDiffKind.Moved => "",
        StructDiffKind.MovedChanged => "",
        StructDiffKind.TypeChanged => "",
        StructDiffKind.Unparsed => "",
        _ => "",
    };

    private static (Brush?, Brush?) BrushesOf(StructDiffKind kind, FrameworkElement scope)
    {
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        Core.Compare.DiffKind? diff = kind switch
        {
            StructDiffKind.Changed or StructDiffKind.MovedChanged => Core.Compare.DiffKind.Changed,
            StructDiffKind.Added => Core.Compare.DiffKind.Inserted,
            StructDiffKind.Removed => Core.Compare.DiffKind.Deleted,
            StructDiffKind.Same => null,
            _ => Core.Compare.DiffKind.Unreadable,
        };
        if (diff is not { } d)
        {
            return (null, (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]);
        }

        return (CompareBrushes.Background(d, scope, highContrast, null), CompareBrushes.Mark(d, scope, highContrast));
    }
}

/// <summary>
/// 構造の差分パネルの行の一覧 (仮想化)。行の数だけを持ち、行の表示用の写しは一覧が読んだときに作る (100 万行でも行のオブジェクトを全部は作らない)。
/// </summary>
public sealed partial class StructDiffRowList : IList, INotifyCollectionChanged
{
    private readonly Dictionary<int, StructDiffRowViewModel> _cache = [];
    private readonly Func<int, StructDiffRowViewModel> _factory;
    private int _count;

    public StructDiffRowList(Func<int, StructDiffRowViewModel> factory) => _factory = factory;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>行の数を変えて、作った行を捨てる (表示し直す)。</summary>
    public void Reset(int count)
    {
        _count = count;
        _cache.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
            {
                return null;
            }

            if (!_cache.TryGetValue(index, out StructDiffRowViewModel? row))
            {
                if (_cache.Count > 2048)
                {
                    _cache.Clear();
                }

                row = _factory(index);
                _cache[index] = row;
            }

            return row;
        }

        set => throw new NotSupportedException();
    }

    public int Count => _count;

    public bool IsFixedSize => false;

    public bool IsReadOnly => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public int IndexOf(object? value) => value is StructDiffRowViewModel row && row.Row.Index < _count && _cache.TryGetValue(row.Row.Index, out StructDiffRowViewModel? cached)
        && ReferenceEquals(cached, row) ? row.Row.Index : -1;

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < _count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }
}
