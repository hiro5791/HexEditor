namespace HexEditor.Core.Clipboard;

/// <summary>システムのクリップボードに入れるテキスト形式 (`CF_UNICODETEXT`) の中身。</summary>
public enum ClipboardTextKind
{
    /// <summary>テキスト形式を入れない (テキストだけが上限を超えた。EDIT-22 の仕様 6)。</summary>
    None,

    /// <summary>選択範囲を Hex 文字列または文字列にしたもの (EDIT-22 の仕様 2)。</summary>
    Data,

    /// <summary>「[HexEditor: … のデータ。HexEditor 内でのみ貼り付けできます]」の 1 行 (選択範囲が上限を超えた。EDIT-22 の仕様 5)。</summary>
    TooLargeLine,
}

/// <summary>
/// コピーでシステムのクリップボードに入れる形式 (EDIT-22 の仕様 2・4〜6)。`HexEditor.Meta` は常に入れる。
/// </summary>
/// <param name="Binary">`HexEditor.Binary` (バイト列そのもの) を入れるか。</param>
/// <param name="Text">テキスト形式の中身。</param>
public sealed record ClipboardPlan(bool Binary, ClipboardTextKind Text)
{
    /// <summary>システムのクリップボードに入れる最大サイズの既定値 (EDIT-22 の仕様 4)。</summary>
    public const long DefaultLimit = 64L * 1024 * 1024;

    public const string BinaryFormat = "HexEditor.Binary";

    public const string MetaFormat = "HexEditor.Meta";

    /// <summary>選択範囲が上限を超え、HexEditor 内でだけ貼り付けられる (InfoBar で知らせる。仕様 5)。</summary>
    public bool InAppOnly => !Binary;

    /// <summary>バイナリ形式は入れたが、テキスト形式だけが上限を超えたので入れなかった (InfoBar で知らせる。仕様 6)。</summary>
    public bool TextOmitted => Binary && Text == ClipboardTextKind.None;

    /// <summary>
    /// 入れる形式を決める。バイナリ形式はバイト数、テキスト形式は生成後の文字数 × 2 バイトで上限と比べる (仕様 4)。
    /// </summary>
    /// <param name="length">選択範囲のバイト数。</param>
    /// <param name="textChars">テキスト形式の文字数を返す関数。選択範囲が上限以内のときだけ呼ぶ (上限を超える範囲は読まない)。</param>
    /// <param name="limit">設定「クリップボードに入れる最大サイズ」。</param>
    public static ClipboardPlan For(long length, Func<long> textChars, long limit = DefaultLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > limit)
        {
            return new ClipboardPlan(false, ClipboardTextKind.TooLargeLine);
        }

        long chars = textChars();
        return new ClipboardPlan(true, chars > limit / 2 ? ClipboardTextKind.None : ClipboardTextKind.Data);
    }

    /// <summary>
    /// Hex 列からコピーしたときのテキスト形式の文字数 (既定の書式 `DE AD BE EF`: 1 バイトあたり 2 桁と区切りの空白)。
    /// <see cref="HexText.Format"/> の長さと同じで、データを読まずに求められる。
    /// </summary>
    public static long HexTextLength(long length) => length <= 0 ? 0 : length * 3 - 1;
}
