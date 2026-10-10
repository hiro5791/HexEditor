namespace HexEditor.Core.Statistics;

/// <summary>
/// 解析の結果が Hex ビューに付ける注釈 1 つ (共通の注釈レイヤー INSP-32 に渡す)。<see cref="Kind"/> は色・模様を決めるキー
/// (例: <c>class.encrypted</c>、<c>format.png</c>)。
/// </summary>
public sealed record AnalysisAnnotation(long Offset, long Length, string Label, string Kind, string? Description = null);

/// <summary>
/// 注釈の出どころが注釈を渡す先 (INSP-32 の共通の注釈レイヤー。データインスペクタの担当が実装する)。統計 (ANA-16 の分類) と
/// ファイル形式の判定 (ANA-17 の埋め込まれた形式) は、この最小の口だけを使う。出どころごとにまとめて置き換える。
/// </summary>
public interface IAnnotationSink
{
    /// <summary>出どころ <paramref name="source"/> (例: <c>statistics.classes</c>) の注釈を置き換える。空なら消す。</summary>
    void SetAnnotations(object document, string source, IReadOnlyList<AnalysisAnnotation> annotations);
}

/// <summary>注釈の出どころの名前。</summary>
public static class AnalysisAnnotationSources
{
    /// <summary>暗号化・圧縮データの検出 (ANA-16) の区間とシグネチャ。</summary>
    public const string Classes = "statistics.classes";

    /// <summary>埋め込まれた形式 (ANA-17 の仕様 6)。</summary>
    public const string EmbeddedFormats = "fileType.embedded";
}
