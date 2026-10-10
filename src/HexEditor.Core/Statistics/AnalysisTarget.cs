using HexEditor.Core.Expressions;
using HexEditor.Core.Hashing;
using HexEditor.Core.View;

namespace HexEditor.Core.Statistics;

/// <summary>共通の「対象範囲」選択欄の選択肢 (06 の 0.1)。</summary>
public enum AnalysisTargetKind
{
    Selection,
    MultiSelection,
    WholeDocument,
    Custom,
}

/// <summary>
/// 共通の「対象範囲」(06 の 0.1) の解釈。選択肢の既定 (選択範囲・マルチ選択の有無で決まる) と、選択肢と入力からドキュメントの範囲を求める。
/// </summary>
public static class AnalysisTarget
{
    /// <summary>既定の選択肢 (0.1 の「既定になる条件」)。<paramref name="multiCount"/> はマルチ選択の範囲の数。</summary>
    public static AnalysisTargetKind DefaultKind(bool hasSelection, int multiCount) =>
        multiCount >= 2 ? AnalysisTargetKind.MultiSelection : hasSelection ? AnalysisTargetKind.Selection : AnalysisTargetKind.WholeDocument;

    /// <summary>
    /// 対象の範囲。「範囲を指定」は開始と長さ (<paramref name="usesEnd"/> なら終了 (このバイトを含む)) を入力式で解釈する。長さが空なら末尾まで。
    /// 入力が正しくない・範囲外なら null。
    /// </summary>
    public static IReadOnlyList<HashRange>? Resolve(AnalysisTargetKind kind, EditorState editor, IReadOnlyList<HashRange>? multi,
        string customStart, string customLength, bool usesEnd)
    {
        long length = editor.Document.Length;
        switch (kind)
        {
            case AnalysisTargetKind.MultiSelection when multi is { Count: > 0 }:
                return multi;
            case AnalysisTargetKind.Selection or AnalysisTargetKind.MultiSelection when editor.HasSelection:
                return [new HashRange(editor.SelectionStart, editor.SelectionLength)];
            case AnalysisTargetKind.Selection or AnalysisTargetKind.MultiSelection or AnalysisTargetKind.WholeDocument:
                return [new HashRange(0, length)];
        }

        var context = new EditorExpressionContext(editor);
        if (!ExpressionEvaluator.TryEvaluate(customStart, context, out long start, out _) || start < 0 || start > length)
        {
            return null;
        }

        long count = length - start;
        if (customLength.Trim().Length > 0)
        {
            if (!ExpressionEvaluator.TryEvaluate(customLength, context, out long value, out _))
            {
                return null;
            }

            count = usesEnd ? value - start + 1 : value;
            if (count < 0 || (usesEnd && value >= length) || start + count > length)
            {
                return null;
            }
        }

        return [new HashRange(start, count)];
    }
}
