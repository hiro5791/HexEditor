namespace HexEditor.Sdk;

/// <summary>
/// Options every asynchronous API accepts (AUTO-05). When omitted, the call follows the cancellation of the whole script
/// and reports its progress to the operation center.
/// <ja>非同期 API が共通で受け取るオプション (AUTO-05)。省略した場合はスクリプト全体のキャンセルに従い、進捗を処理センターに出す。</ja>
/// </summary>
public class AsyncOptions
{
    /// <summary>
    /// Cancellation (JavaScript: an <c>AbortSignal</c> such as <c>script.signal</c> or <c>new AbortController().signal</c>).
    /// <ja>キャンセル (JavaScript では <c>script.signal</c> や <c>new AbortController().signal</c> などの <c>AbortSignal</c>)。</ja>
    /// </summary>
    [TsType("AbortSignal")]
    public CancellationToken? Signal { get; init; }

    /// <summary>
    /// Receives the progress: the fraction done (0-1) and a description.
    /// <ja>進捗を受け取る関数。終わった割合 (0〜1) と説明。</ja>
    /// </summary>
    [TsType("(fraction: number, text?: string) => void")]
    public Action<double, string?>? Progress { get; init; }
}

/// <summary>Options of <see cref="IApp.OpenAsync"/>. <ja><see cref="IApp.OpenAsync"/> のオプション。</ja></summary>
public sealed class OpenOptions
{
    /// <summary>Open read-only. <ja>読み取り専用で開く。</ja></summary>
    public bool ReadOnly { get; init; }

    /// <summary>Move the cursor to this offset after opening. <ja>開いた後にカーソルを置くオフセット。</ja></summary>
    public OffsetArg? Offset { get; init; }

    /// <summary>Select this range after opening. <ja>開いた後に選択する範囲。</ja></summary>
    public RangeArg? Select { get; init; }

    /// <summary>The text column encoding. <ja>テキスト列の文字コード。</ja></summary>
    public EncodingArg? Encoding { get; init; }

    /// <summary>Do not activate the new tab. <ja>タブをアクティブにしない。</ja></summary>
    public bool Background { get; init; }
}

/// <summary>Options of <see cref="IApp.NewDocument"/>. <ja><see cref="IApp.NewDocument"/> のオプション。</ja></summary>
public sealed class NewDocumentOptions
{
    /// <summary>Initial length in bytes. <ja>初期の長さ (バイト)。</ja></summary>
    public long Length { get; init; }

    /// <summary>Pattern for the initial content (default zero bytes). <ja>初期内容のパターン (既定は 0)。</ja></summary>
    public BytePattern? Fill { get; init; }
}

/// <summary>Options of <see cref="IDocument.CloseAsync"/>. <ja><see cref="IDocument.CloseAsync"/> のオプション。</ja></summary>
public sealed class CloseOptions
{
    /// <summary>Discard unsaved changes without asking. <ja>未保存の変更を確認なしで破棄する。</ja></summary>
    public bool Discard { get; init; }
}

/// <summary>Options of <see cref="IDocument.ReadString"/>. <ja><see cref="IDocument.ReadString"/> のオプション。</ja></summary>
public sealed class ReadStringOptions
{
    /// <summary>Stop at the first NUL character (default true). <ja>最初の NUL 文字で止める (既定 true)。</ja></summary>
    public bool StopAtNull { get; init; } = true;
}

/// <summary>Options of <see cref="IDocument.Fill"/>. <ja><see cref="IDocument.Fill"/> のオプション。</ja></summary>
public sealed class FillOptions
{
    /// <summary>Insert instead of overwriting. <ja>上書きでなく挿入する。</ja></summary>
    public bool Insert { get; init; }
}

/// <summary>Options of <see cref="IDocument.TransformAsync"/>. <ja><see cref="IDocument.TransformAsync"/> のオプション。</ja></summary>
public sealed class TransformOptions : AsyncOptions
{
    /// <summary>Element size: 1, 2, 4 or 8 bytes (default 1). <ja>要素のサイズ: 1・2・4・8 バイト (既定 1)。</ja></summary>
    public int Unit { get; init; } = 1;

    /// <summary><c>"le"</c> or <c>"be"</c> (default <c>"le"</c>). <ja><c>"le"</c> か <c>"be"</c> (既定 <c>"le"</c>)。</ja></summary>
    public string? Endian { get; init; }

    /// <summary><c>"wrap"</c> (default) or <c>"saturate"</c>. <ja><c>"wrap"</c> (既定) か <c>"saturate"</c>。</ja></summary>
    public string? Overflow { get; init; }
}

/// <summary>Options of the search APIs. <ja>検索の API のオプション。</ja></summary>
public sealed class FindOptions : AsyncOptions
{
    /// <summary>
    /// How a string pattern is read: <c>"hex"</c> (default; wildcards allowed), <c>"text"</c>, <c>"regex"</c>, <c>"int"</c> or <c>"float"</c>.
    /// Byte patterns are searched as they are.
    /// <ja>文字列のパターンの解釈: <c>"hex"</c> (既定。ワイルドカード可)、<c>"text"</c>、<c>"regex"</c>、<c>"int"</c>、<c>"float"</c>。バイト列のパターンはそのまま探す。</ja>
    /// </summary>
    public string? Type { get; init; }

    /// <summary>Text encoding for <c>text</c> and <c>regex</c>. <ja><c>text</c> と <c>regex</c> の文字コード。</ja></summary>
    public EncodingArg? Encoding { get; init; }

    /// <summary>Match case (default false). <ja>大文字と小文字を区別する (既定 false)。</ja></summary>
    public bool CaseSensitive { get; init; }

    /// <summary>Search only this range. <ja>この範囲だけを検索する。</ja></summary>
    public RangeArg? Range { get; init; }

    /// <summary>Only matches whose start is a multiple of this value. <ja>開始がこの値の倍数の一致だけ。</ja></summary>
    public int Alignment { get; init; } = 1;

    /// <summary>Maximum number of results (default 1,000,000). <ja>結果の最大件数 (既定 1,000,000)。</ja></summary>
    public long Max { get; init; } = 1_000_000;

    /// <summary>Start offset of <see cref="IDocument.FindAsync"/>. <ja><see cref="IDocument.FindAsync"/> の開始位置。</ja></summary>
    public OffsetArg? From { get; init; }

    /// <summary>Search backward (<see cref="IDocument.FindAsync"/>). <ja>後ろ向きに検索する (<see cref="IDocument.FindAsync"/>)。</ja></summary>
    public bool Backward { get; init; }

    /// <summary>Integer or float size in bytes for <c>int</c> / <c>float</c> (default 4). <ja><c>int</c> / <c>float</c> の値のサイズ (既定 4)。</ja></summary>
    public int Size { get; init; } = 4;

    /// <summary><c>"le"</c> or <c>"be"</c> for <c>int</c> / <c>float</c>. <ja><c>int</c> / <c>float</c> のエンディアン。</ja></summary>
    public string? Endian { get; init; }
}

/// <summary>Options of <see cref="IDocument.CopyRangeAsync"/>. <ja><see cref="IDocument.CopyRangeAsync"/> のオプション。</ja></summary>
public sealed class CopyRangeOptions : AsyncOptions
{
    /// <summary>Insert instead of overwriting. <ja>上書きでなく挿入する。</ja></summary>
    public bool Insert { get; init; }
}

/// <summary>Options of <see cref="IDocument.ExportRangeAsync"/>. <ja><see cref="IDocument.ExportRangeAsync"/> のオプション。</ja></summary>
public sealed class ExportRangeOptions : AsyncOptions
{
    /// <summary>Overwrite an existing file (default false). <ja>既存のファイルを上書きする (既定 false)。</ja></summary>
    public bool Overwrite { get; init; }

    /// <summary>
    /// Export format ID, the same as <c>hexed convert --to</c> (for example <c>ihex</c>, <c>srec</c>, <c>base64</c>, <c>hextext</c>, <c>c</c>; default <c>bin</c>).
    /// <ja>エクスポートの形式の ID。<c>hexed convert --to</c> と同じ (例: <c>ihex</c>、<c>srec</c>、<c>base64</c>、<c>hextext</c>、<c>c</c>。既定 <c>bin</c>)。</ja>
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Options of the format, by the names of the export dialog (for example <c>recordLength</c>, <c>startAddress</c>, <c>lineLength</c>).
    /// <ja>形式の設定。エクスポートのダイアログの項目名で指定する (例: <c>recordLength</c>、<c>startAddress</c>、<c>lineLength</c>)。</ja>
    /// </summary>
    public IReadOnlyDictionary<string, string>? FormatOptions { get; init; }
}

/// <summary>Options of <see cref="IDocument.EntropyAsync"/>. <ja><see cref="IDocument.EntropyAsync"/> のオプション。</ja></summary>
public sealed class EntropyOptions : AsyncOptions
{
    /// <summary>When set, returns one value per block of this size. <ja>指定するとこの大きさのブロックごとの値の配列を返す。</ja></summary>
    public long BlockSize { get; init; }
}

/// <summary>Options of <see cref="IDocument.CompareAsync"/>. <ja><see cref="IDocument.CompareAsync"/> のオプション。</ja></summary>
public sealed class CompareOptions : AsyncOptions
{
    /// <summary>Maximum number of difference ranges (default 1,000,000). <ja>差分の範囲の最大件数 (既定 1,000,000)。</ja></summary>
    public long Max { get; init; } = 1_000_000;
}

/// <summary>Options of <see cref="IDocument.StringsAsync"/>. <ja><see cref="IDocument.StringsAsync"/> のオプション。</ja></summary>
public sealed class StringsOptions : AsyncOptions
{
    /// <summary>Minimum length in characters (default 4). <ja>最小の文字数 (既定 4)。</ja></summary>
    public int MinLength { get; init; } = 4;

    /// <summary>Encodings to look for (default ASCII and UTF-16LE). <ja>探す文字コード (既定 ASCII と UTF-16LE)。</ja></summary>
    public IReadOnlyList<EncodingArg>? Encodings { get; init; }

    /// <summary>Search only this range. <ja>この範囲だけを対象にする。</ja></summary>
    public RangeArg? Range { get; init; }

    /// <summary>Maximum number of results (default 1,000,000). <ja>結果の最大件数 (既定 1,000,000)。</ja></summary>
    public long Max { get; init; } = 1_000_000;
}

/// <summary>Options of <see cref="IDocument.ApplyTemplateAsync"/>. <ja><see cref="IDocument.ApplyTemplateAsync"/> のオプション。</ja></summary>
public sealed class ApplyTemplateOptions : AsyncOptions
{
    /// <summary>Offset to apply the template at (default 0). <ja>テンプレートを適用するオフセット (既定 0)。</ja></summary>
    public OffsetArg? Offset { get; init; }

    /// <summary>Show the result in the template panel (default true). <ja>テンプレートパネルに結果を表示する (既定 true)。</ja></summary>
    public bool Show { get; init; } = true;
}

/// <summary>Options of <see cref="ITemplates.RegisterExtension"/>. <ja><see cref="ITemplates.RegisterExtension"/> のオプション。</ja></summary>
public sealed class TemplateExtensionOptions
{
    /// <summary>
    /// Extension point: <c>"format"</c>, <c>"read"</c>, <c>"write"</c>, <c>"pointerMap"</c>, <c>"section"</c> or <c>"function"</c> (default).
    /// <ja>拡張点: <c>"format"</c>、<c>"read"</c>、<c>"write"</c>、<c>"pointerMap"</c>、<c>"section"</c>、<c>"function"</c> (既定)。</ja>
    /// </summary>
    public string Point { get; init; } = "function";

    /// <summary>
    /// Parameter types: <c>"integer"</c>, <c>"float"</c>, <c>"bool"</c>, <c>"string"</c>, <c>"bytes"</c>, <c>"pattern"</c> or <c>"any"</c>.
    /// <ja>引数の型: <c>"integer"</c>、<c>"float"</c>、<c>"bool"</c>、<c>"string"</c>、<c>"bytes"</c>、<c>"pattern"</c>、<c>"any"</c>。</ja>
    /// </summary>
    public IReadOnlyList<string>? Params { get; init; }

    /// <summary>Return type (the same names as <see cref="Params"/>; default <c>"any"</c>). <ja>戻り値の型 (<see cref="Params"/> と同じ名前。既定 <c>"any"</c>)。</ja></summary>
    public string Returns { get; init; } = "any";
}

/// <summary>Options of the inspector APIs. <ja>インスペクタの API のオプション。</ja></summary>
public sealed class InspectorOptions
{
    /// <summary><c>"le"</c> or <c>"be"</c> (default: the inspector setting). <ja><c>"le"</c> か <c>"be"</c> (既定はインスペクタの設定)。</ja></summary>
    public string? Endian { get; init; }
}

/// <summary>Options of <see cref="IText.Decode"/>. <ja><see cref="IText.Decode"/> のオプション。</ja></summary>
public sealed class DecodeOptions
{
    /// <summary>
    /// <c>"replace"</c> (default; invalid bytes become U+FFFD) or <c>"error"</c> (throw <see cref="ArgumentError"/>).
    /// <ja><c>"replace"</c> (既定。不正なバイトを置換文字にする) か <c>"error"</c> (<see cref="ArgumentError"/> を投げる)。</ja>
    /// </summary>
    public string? Invalid { get; init; }
}

/// <summary>Options of <see cref="IClipboard.CopyAs"/> and <see cref="IClipboard.FormatAs"/>. <ja>Copy As のオプション。</ja></summary>
public sealed class CopyAsOptions
{
    /// <summary>Bytes per line (0: the Copy As setting). <ja>1 行のバイト数 (0 は Copy As の設定)。</ja></summary>
    public int BytesPerLine { get; init; }

    /// <summary>Uppercase Hex digits (null: the Copy As setting). <ja>Hex を大文字にする (null は Copy As の設定)。</ja></summary>
    public bool? Uppercase { get; init; }
}

/// <summary>Options of <see cref="IUi.MessageAsync"/>. <ja><see cref="IUi.MessageAsync"/> のオプション。</ja></summary>
public sealed class MessageOptions
{
    /// <summary><c>"info"</c> (default), <c>"warning"</c> or <c>"error"</c>. <ja><c>"info"</c> (既定)・<c>"warning"</c>・<c>"error"</c>。</ja></summary>
    public string? Kind { get; init; }

    /// <summary>Show a dialog instead of an InfoBar. <ja>InfoBar でなくダイアログで表示する。</ja></summary>
    public bool Modal { get; init; }
}

/// <summary>Options of <see cref="IUi.PromptAsync"/> and one field of <see cref="IUi.FormAsync"/>. <ja>入力欄の定義。</ja></summary>
public class PromptOptions
{
    /// <summary>
    /// <c>"text"</c> (default), <c>"number"</c> (input expressions), <c>"offset"</c>, <c>"range"</c>, <c>"hex"</c>, <c>"choice"</c>
    /// and, in forms, <c>"checkbox"</c>.
    /// <ja><c>"text"</c> (既定)、<c>"number"</c> (入力式)、<c>"offset"</c>、<c>"range"</c>、<c>"hex"</c>、<c>"choice"</c>。フォームでは <c>"checkbox"</c> も使える。</ja>
    /// </summary>
    public string? Type { get; init; }

    /// <summary>The default value. <ja>既定値。</ja></summary>
    [TsType("unknown")]
    public object? Default { get; init; }

    /// <summary>The choices of a <c>choice</c> field. <ja><c>choice</c> の選択肢。</ja></summary>
    public IReadOnlyList<string>? Choices { get; init; }
}

/// <summary>One field of <see cref="IUi.FormAsync"/>. <ja><see cref="IUi.FormAsync"/> の 1 つの項目。</ja></summary>
public sealed class FormField : PromptOptions
{
    /// <summary>The key of the value in the result. <ja>結果のオブジェクトでの値のキー。</ja></summary>
    public string Name { get; init; } = "";

    /// <summary>The label shown to the user. <ja>利用者に見せるラベル。</ja></summary>
    public string? Label { get; init; }
}

/// <summary>Options of the file pickers. <ja>ファイル選択のオプション。</ja></summary>
public sealed class PickOptions
{
    /// <summary>File type filters such as <c>"*.bin"</c>. <ja><c>"*.bin"</c> などの種類の絞り込み。</ja></summary>
    public IReadOnlyList<string>? Filters { get; init; }

    /// <summary>Suggested file name (save dialog). <ja>提案するファイル名 (保存のダイアログ)。</ja></summary>
    public string? SuggestedName { get; init; }

    /// <summary>The dialog title. <ja>ダイアログのタイトル。</ja></summary>
    public string? Title { get; init; }
}

/// <summary>Options of <see cref="INet.FetchAsync"/>. <ja><see cref="INet.FetchAsync"/> のオプション。</ja></summary>
public sealed class FetchOptions : AsyncOptions
{
    /// <summary><c>"GET"</c> (default) or <c>"POST"</c>. <ja><c>"GET"</c> (既定) か <c>"POST"</c>。</ja></summary>
    public string? Method { get; init; }

    /// <summary>Request headers. <ja>要求のヘッダー。</ja></summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Request body (POST). <ja>要求の本文 (POST)。</ja></summary>
    public BytePattern? Body { get; init; }

    /// <summary>Request body as text (POST). <ja>要求の本文の文字列 (POST)。</ja></summary>
    public string? BodyText { get; init; }

    /// <summary><c>"text"</c> (default) or <c>"bytes"</c>. <ja><c>"text"</c> (既定) か <c>"bytes"</c>。</ja></summary>
    public string? ResponseType { get; init; }
}

/// <summary>Options of <see cref="IProcessRunner.RunAsync"/>. <ja><see cref="IProcessRunner.RunAsync"/> のオプション。</ja></summary>
public sealed class ProcessOptions : AsyncOptions
{
    /// <summary>Time limit in seconds (default 60). <ja>制限時間 (秒、既定 60)。</ja></summary>
    public double Timeout { get; init; } = 60;

    /// <summary>The working folder. <ja>作業フォルダ。</ja></summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Text written to the standard input. <ja>標準入力に書く文字列。</ja></summary>
    public string? Input { get; init; }
}
