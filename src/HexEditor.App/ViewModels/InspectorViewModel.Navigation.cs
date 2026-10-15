using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Expressions;
using HexEditor.Core.Inspector;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>
/// インスペクタからの移動: 整数の行の値を位置として移動する「ポインタとして移動」(INSP-20) と、選んだ型を配列の要素とみなして前後に
/// 移る配列モード (INSP-21)。
/// </summary>
public sealed partial class InspectorViewModel
{
    /// <summary>最後に使った「ポインタとして移動」の方法 (サブメニューの既定にする。アプリ全体)。</summary>
    public static PointerMode LastPointerMode { get; private set; } = PointerMode.AbsoluteOffset;

    /// <summary>移動できなかった (範囲外など)。InfoBar に出す文。</summary>
    public event EventHandler<string>? NavigationFailed;

    /// <summary>「ポインタとして移動」の対象の行か (INSP-20 の仕様 1)。</summary>
    public static bool CanFollowPointer(InspectorItemViewModel? item) =>
        item is { Kind: InspectorItemKind.Row } && InspectorNavigation.IsPointerType(item.TypeId);

    /// <summary>
    /// 行の値を位置として移動する (INSP-20)。移動はジャンプ履歴に記録する (仕様 4)。範囲外なら移動せず InfoBar で知らせる (仕様 5)。
    /// <paramref name="baseText"/> は「基準を指定して相対」の基準 (入力式)。移動したら true。
    /// </summary>
    public bool FollowPointer(InspectorItemViewModel item, PointerMode mode, string? baseText = null)
    {
        if (_document is not { } doc || !CanFollowPointer(item))
        {
            return false;
        }

        EditorState editor = doc.Editor;
        if (InspectorNavigation.ReadInteger(item.TypeId, _data, item.Endian, out int size) is not { } value)
        {
            NavigationFailed?.Invoke(this, Loc.Get("Inspector_PointerNoValue"));
            return false;
        }

        long relativeBase = 0;
        if (mode == PointerMode.RelativeToBase)
        {
            if (!ExpressionEvaluator.TryEvaluate(baseText ?? string.Empty, new EditorExpressionContext(editor), out relativeBase, out _))
            {
                NavigationFailed?.Invoke(this, Loc.Get("Inspector_PointerBadBase"));
                return false;
            }
        }

        LastPointerMode = mode;
        PointerTarget target = InspectorNavigation.Resolve(mode, value, Origin, size, editor.View.BaseAddress, relativeBase, editor.Layout.MaxCursor);
        if (!target.InRange)
        {
            NavigationFailed?.Invoke(this, Loc.Format("Inspector_PointerOutOfRange", HexText(target.Target), HexText(doc.Document.Length)));
            return false;
        }

        editor.GoTo((long)target.Target);
        return true;
    }

    private static string HexText(Int128 value) => value < 0
        ? "-0x" + (-value).ToString("X", CultureInfo.InvariantCulture)
        : "0x" + value.ToString("X", CultureInfo.InvariantCulture);

    // ---- 配列モード (INSP-21) ----

    private InspectorArrayMode? _array;

    /// <summary>配列モード中か。</summary>
    [ObservableProperty]
    public partial bool IsArrayMode { get; set; }

    /// <summary>要素番号の表示 (境界でなければ「(要素の境界ではありません)」)。</summary>
    [ObservableProperty]
    public partial string ArrayIndexText { get; set; } = string.Empty;

    /// <summary>要素のサイズ (間隔) の入力欄の文字列。</summary>
    [ObservableProperty]
    public partial string ArrayStrideText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanPreviousElement { get; set; }

    [ObservableProperty]
    public partial bool CanNextElement { get; set; }

    /// <summary>配列モードで選んだ型 (その行を強調する)。</summary>
    public string? ArrayTypeId => _array?.TypeId;

    /// <summary>配列モードの状態 (Hex ビューの境界線)。</summary>
    public InspectorArrayMode? ArrayMode => _array;

    /// <summary>「この型で配列として移動」(仕様 1・2): 今の起点を要素 0、型のサイズを間隔にして配列モードに入る。</summary>
    public bool EnterArrayMode(InspectorItemViewModel item)
    {
        if (_document is null || item.Kind != InspectorItemKind.Row || InspectorTypes.Find(item.TypeId) is not { } type)
        {
            return false;
        }

        long stride = Math.Max(1, item.ByteCount > 0 && type.Variable ? item.ByteCount : type.Size);
        _array = new InspectorArrayMode(item.TypeId, Origin, stride);
        ArrayStrideText = "0x" + stride.ToString("X", CultureInfo.InvariantCulture);
        IsArrayMode = true;
        UpdateArrayState();
        foreach (InspectorItemViewModel i in Items)
        {
            i.IsArrayType = i.Kind == InspectorItemKind.Row && i.TypeId == item.TypeId;
        }

        HighlightChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>配列モードを終える (Esc、「配列モードを終了」。仕様 6)。</summary>
    public void ExitArrayMode()
    {
        if (_array is null)
        {
            return;
        }

        _array = null;
        IsArrayMode = false;
        ArrayIndexText = string.Empty;
        foreach (InspectorItemViewModel i in Items)
        {
            i.IsArrayType = false;
        }

        HighlightChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>「前の要素」「次の要素」。ジャンプ履歴には記録しない (仕様 7)。</summary>
    public bool StepElement(int direction)
    {
        if (_array is not { } array || _document is not { } doc)
        {
            return false;
        }

        EditorState editor = doc.Editor;
        if (array.Step(editor.Cursor, direction, editor.Layout.MaxCursor) is not { } target)
        {
            return false;
        }

        editor.MoveCursorWithoutHistory(target);
        return true;
    }

    /// <summary>要素番号の欄に番号を入れて確定した (負の番号も可。仕様 4)。移動できたら true。</summary>
    public bool GoToElement(string text)
    {
        if (_array is not { } array || _document is not { } doc)
        {
            return false;
        }

        EditorState editor = doc.Editor;
        if (!long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long index)
            && !ExpressionEvaluator.TryEvaluate(text, new EditorExpressionContext(editor), out index, out _, DefaultRadix.Decimal))
        {
            UpdateArrayState();
            return false;
        }

        if (array.OffsetOf(index) is not { } target || target > editor.Layout.MaxCursor)
        {
            NavigationFailed?.Invoke(this, Loc.Format("Inspector_ArrayOutOfRange", index.ToString(CultureInfo.InvariantCulture)));
            UpdateArrayState();
            return false;
        }

        editor.MoveCursorWithoutHistory(target);
        return true;
    }

    /// <summary>要素のサイズ (間隔) を入力式で変える (例: <c>0x28</c>。仕様 2)。不正な値なら false。</summary>
    public bool SetStride(string text)
    {
        if (_array is not { } array || _document is not { } doc)
        {
            return false;
        }

        if (!ExpressionEvaluator.TryEvaluate(text, new EditorExpressionContext(doc.Editor), out long stride, out _) || stride <= 0)
        {
            return false;
        }

        array.Stride = stride;
        ArrayStrideText = "0x" + stride.ToString("X", CultureInfo.InvariantCulture);
        UpdateArrayState();
        HighlightChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>要素番号の表示とボタンの有効・無効をカーソルの位置に合わせる (Refresh から呼ぶ)。</summary>
    private void UpdateArrayState()
    {
        if (_array is not { } array || _document is not { } doc)
        {
            return;
        }

        EditorState editor = doc.Editor;
        ArrayIndexText = array.IndexOf(editor.Cursor) is { } index
            ? index.ToString(CultureInfo.InvariantCulture)
            : Loc.Get("Inspector_ArrayNotBoundary");
        CanPreviousElement = array.Step(editor.Cursor, -1, editor.Layout.MaxCursor) is not null;
        CanNextElement = array.Step(editor.Cursor, 1, editor.Layout.MaxCursor) is not null;
    }
}
