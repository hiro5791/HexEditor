using System.Globalization;

namespace HexEditor.Core.Templates.Presentation;

/// <summary>
/// 結果ツリーの 1 行に表示する内容 (TPL-21 の仕様 1・2・8)。評価器のスレッドでまとめて作り (<see cref="TemplateTreeModel.LoadRowDataAsync"/>)、
/// UI スレッドではこの写しだけを読む (描画のたびに評価器を待たない)。文言はすべて画面の言語に依存しない材料で、画面側が
/// リソースで組み立てる。
/// </summary>
public sealed record TemplateRowData
{
    public required string Name { get; init; }

    public required string Value { get; init; }

    public required string Type { get; init; }

    public long Offset { get; init; }

    public long Size { get; init; }

    public int? BitOffset { get; init; }

    public int? BitSize { get; init; }

    public string? Comment { get; init; }

    public TemplateNodeKind Kind { get; init; }

    public bool BigEndian { get; init; }

    /// <summary>パス (<c>chunks[3].header.width</c>)。適用・グループの行は空。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary><c>color("…")</c> の名前 (色見本と Hex ビューの色。TPL-21 の仕様 2)。</summary>
    public string? ColorName { get; init; }

    /// <summary>色の型の値 (0xAARRGGBB。値の色見本。TC-TPL-03-04)。</summary>
    public uint? ValueArgb { get; init; }

    /// <summary>このノードの最初のエラー (あれば)。</summary>
    public TemplateDiagnostic? Error { get; init; }

    /// <summary>このノードの最初の警告 (あれば)。</summary>
    public TemplateDiagnostic? Warning { get; init; }

    /// <summary>列挙型の値に名前がない (警告アイコンと「定義されていない値」。TPL-05 の仕様 3)。</summary>
    public bool UndefinedEnumValue { get; init; }

    /// <summary><c>///</c> のコメント (ツールチップ。TPL-02 の仕様 1)。</summary>
    public string? DocComment { get; init; }

    /// <summary><c>docs("…")</c> (型の説明。ツールチップ)。</summary>
    public string? Docs { get; init; }

    /// <summary>日時の型の UTC と地域の時刻 (ツールチップ。TPL-03 の仕様 5)。</summary>
    public (string Utc, string Local)? DateTimes { get; init; }

    public TemplateEditBlock EditBlock { get; init; } = TemplateEditBlock.NotAValue;

    /// <summary><c>read</c> だけの変換 (編集は生の値になる。TPL-14 の仕様 5)。</summary>
    public bool EditsRawValueOnly { get; init; }

    /// <summary>セクションの中のパターンならそのセクションの名前。</summary>
    public string? SectionName { get; init; }

    /// <summary>ポインタの指す先 (null・範囲外・未評価は null)。</summary>
    public long? PointerTarget { get; init; }

    public bool PointerOutOfRange { get; init; }

    /// <summary>既出のパターンを指すポインタ (「→ 既出」) のパス。</summary>
    public string? PointerDuplicateOf { get; init; }

    /// <summary>適用の行: テンプレートのファイル名 (型の列)、未保存のテンプレート、追従。</summary>
    public bool IsFollow { get; init; }

    /// <summary>グループの行 (<c>[0 … 999]</c>) の要素の数。</summary>
    public long? GroupCount { get; init; }

    /// <summary>終わりのオフセット (<c>endof</c>。この位置のバイトを含まない)。</summary>
    public long End => Offset + Size;

    /// <summary>
    /// ノードの行の内容を作る (評価器のスレッドで呼ぶ。<see cref="TemplateResult.Invoke{T}(Func{T})"/> の中)。値の表示は
    /// <paramref name="context"/> の地域設定に従う。
    /// </summary>
    public static TemplateRowData FromNode(TemplateNode node, TemplateDisplayContext context)
    {
        bool application = node.Kind == TemplateNodeKind.Application;
        string value = application || node.Kind is TemplateNodeKind.Section ? string.Empty : SafeDisplay(node, context);
        IReadOnlyList<TemplateDiagnostic> diagnostics = node.Diagnostics;
        TemplateNode? swatchSource = node;
        uint? argb = null;
        if (node.Kind == TemplateNodeKind.Color)
        {
            try
            {
                argb = swatchSource.ColorArgb;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                argb = null;
            }
        }

        bool undefinedEnum = false;
        if (node.Kind == TemplateNodeKind.Enum)
        {
            try
            {
                undefinedEnum = node.IsUndefinedEnumValue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                undefinedEnum = false;
            }
        }

        TemplatePointerInfo? pointer = node.Pointer;
        return new TemplateRowData
        {
            Name = node.DisplayName,
            Value = value,
            Type = node.TypeName,
            Offset = node.Offset,
            Size = node.Size,
            BitOffset = node.BitOffset,
            BitSize = node.BitSize,
            Comment = application ? null : SafeComment(node),
            Kind = node.Kind,
            BigEndian = node.BigEndian,
            Path = application ? string.Empty : node.Path,
            ColorName = node.Attributes.Color,
            ValueArgb = argb,
            Error = diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Error),
            Warning = diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Warning),
            UndefinedEnumValue = undefinedEnum,
            DocComment = node.DocComment,
            Docs = node.Attributes.Docs,
            DateTimes = node.Kind == TemplateNodeKind.DateTime ? SafeDateTimes(node, context) : null,
            EditBlock = application ? TemplateEditBlock.NotAValue : node.EditBlock,
            EditsRawValueOnly = !application && node.EditsRawValueOnly,
            SectionName = node.Section?.Name,
            PointerTarget = pointer is { IsNull: false, OutOfRange: false } ? pointer.TargetOffset : null,
            PointerOutOfRange = pointer?.OutOfRange == true,
            PointerDuplicateOf = pointer?.DuplicateOf,
            IsFollow = application && node.Result.Options.TypeName is not null,
        };
    }

    /// <summary>配列のグループの行 (<c>[0 … 999]</c>。TPL-21 の仕様 3)。</summary>
    public static TemplateRowData ForGroup(long start, long count, long offset, long size) => new()
    {
        Name = GroupName(start, count),
        Value = string.Empty,
        Type = string.Empty,
        Offset = offset,
        Size = size,
        Kind = TemplateNodeKind.Array,
        GroupCount = count,
    };

    /// <summary>グループの名前 (例: <c>[0 … 999999]</c>)。</summary>
    public static string GroupName(long start, long count) =>
        "[" + start.ToString(CultureInfo.InvariantCulture) + " … " + (start + count - 1).ToString(CultureInfo.InvariantCulture) + "]";

    private static string SafeDisplay(TemplateNode node, TemplateDisplayContext context)
    {
        try
        {
            return node.GetDisplay(context);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "<" + ex.Message + ">";
        }
    }

    private static string? SafeComment(TemplateNode node)
    {
        try
        {
            return node.Comment;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static (string, string)? SafeDateTimes(TemplateNode node, TemplateDisplayContext context)
    {
        try
        {
            return node.DateTimeTooltip(context);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
