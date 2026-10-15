using System.Numerics;

namespace HexEditor.Sdk;

/// <summary>
/// One open document (AUTO-07). Synchronous reads and writes are limited to 16 MB per call (AUTO-05); use
/// <see cref="Chunks"/> and the bulk APIs (AUTO-09) for more.
/// <ja>開いている 1 つのドキュメント (AUTO-07)。同期の読み書きは 1 回 16 MB まで (AUTO-05)。それより大きい量は <see cref="Chunks"/> と一括処理 API (AUTO-09) を使う。</ja>
/// </summary>
public interface IDocument
{
    /// <summary>Identifier unique during the session. <ja>セッション中で一意な識別子。</ja></summary>
    string Id { get; }

    /// <summary><c>file</c>, <c>disk</c>, <c>volume</c>, <c>process</c> or <c>memory</c> (new). <ja><c>file</c>・<c>disk</c>・<c>volume</c>・<c>process</c>・<c>memory</c> (新規)。</ja></summary>
    string Kind { get; }

    /// <summary>The path, or null for a new document. <ja>パス。新規ドキュメントでは null。</ja></summary>
    string? Path { get; }

    /// <summary>The display name. <ja>表示名。</ja></summary>
    string Name { get; }

    /// <summary>Current length in bytes. <ja>現在の長さ (バイト)。</ja></summary>
    long Length { get; }

    /// <summary>True when there are unsaved changes. <ja>未保存の変更があるか。</ja></summary>
    bool IsModified { get; }

    /// <summary>True when the document is read-only. <ja>読み取り専用か。</ja></summary>
    bool IsReadOnly { get; }

    /// <summary>True when the length can change (false for disks). <ja>長さを変えられるか (ディスクなどは false)。</ja></summary>
    bool CanResize { get; }

    /// <summary>The address shown for offset 0. <ja>オフセット 0 に表示するアドレス。</ja></summary>
    long BaseAddress { get; }

    /// <summary>The sector size. <ja>セクタサイズ。</ja></summary>
    int SectorSize { get; }

    /// <summary>The text column encoding name. <ja>テキスト列の文字コードの名前。</ja></summary>
    string Encoding { get; }

    /// <summary>The cursor offset. Setting it moves the cursor (not recorded in the jump history; use <see cref="GoTo"/> for that). <ja>カーソルのオフセット。代入するとカーソルを移動する (ジャンプ履歴には記録しない。記録する場合は <see cref="GoTo"/>)。</ja></summary>
    long Cursor { get; set; }

    /// <summary>The selection (AUTO-10). <ja>選択範囲 (AUTO-10)。</ja></summary>
    ISelection Selection { get; }

    /// <summary><c>"hex"</c> or <c>"text"</c>. Setting it switches the active column. <ja><c>"hex"</c> か <c>"text"</c>。代入で切り替える。</ja></summary>
    string ActiveColumn { get; set; }

    /// <summary>Bookmarks (AUTO-11). <ja>ブックマーク (AUTO-11)。</ja></summary>
    IBookmarks Bookmarks { get; }

    /// <summary>Temporary highlights removed when the script ends. <ja>スクリプトの終了で消える一時的な強調表示。</ja></summary>
    IHighlights Highlights { get; }

    /// <summary>Saves with the same safe method as the editor. <ja>エディタと同じ安全な方式で保存する。</ja></summary>
    /// <exception cref="ReadOnlyError">The document is read-only. <ja>読み取り専用。</ja></exception>
    /// <exception cref="IoError">Saving failed. <ja>保存に失敗した。</ja></exception>
    Task SaveAsync(AsyncOptions? options = null);

    /// <summary>Saves to another path. <ja>名前を付けて保存する。</ja></summary>
    Task SaveAsAsync(string path, AsyncOptions? options = null);

    /// <summary>Closes the document. <ja>閉じる。</ja></summary>
    Task CloseAsync(CloseOptions? options = null);

    /// <summary>Undoes the last edit. <ja>直前の編集を元に戻す。</ja></summary>
    void Undo();

    /// <summary>Redoes the undone edit. <ja>元に戻した編集をやり直す。</ja></summary>
    void Redo();

    /// <summary>Starts one undo unit named <paramref name="name"/>. <ja><paramref name="name"/> という 1 つの Undo 単位を始める。</ja></summary>
    void BeginUndoGroup(string name);

    /// <summary>Ends the undo unit started by <see cref="BeginUndoGroup"/>. <ja><see cref="BeginUndoGroup"/> で始めた単位を終える。</ja></summary>
    void EndUndoGroup();

    /// <summary>Moves the cursor and records the jump history. <ja>カーソルを移動し、ジャンプ履歴に記録する。</ja></summary>
    void GoTo(OffsetArg offset);

    /// <summary>Scrolls the view without moving the cursor. <ja>カーソルを動かさずに表示位置だけを変える。</ja></summary>
    void ScrollTo(OffsetArg offset);

    /// <summary>Opens the range in another tab as a read-only view. <ja>範囲を別のタブで開く (読み取り専用のビュー)。</ja></summary>
    Task<IDocument> OpenRangeAsync(RangeArg range);

    // ---- AUTO-08 データの読み書き ----

    /// <summary>
    /// Reads bytes (up to 16 MB). Bytes past the end are not returned (the result is shorter).
    /// <ja>バイト列を読む (16 MB まで)。末尾を越える部分は返さない (返す長さが短くなる)。</ja>
    /// </summary>
    /// <exception cref="LimitError">More than 16 MB. <ja>16 MB を超える。</ja></exception>
    /// <exception cref="IoError">The data cannot be read (for example a disk read error). <ja>読めない (ディスクの読み取りエラーなど)。</ja></exception>
    byte[] ReadBytes(OffsetArg offset, long length);

    /// <summary>Reads one byte. <ja>1 バイト読む。</ja></summary>
    int ReadByte(OffsetArg offset);

    /// <summary>
    /// Reads an integer. <paramref name="type"/>: <c>i8 u8 i16 u16 i24 u24 i32 u32 i64 u64</c>. <paramref name="endian"/>: <c>"le"</c> or
    /// <c>"be"</c> (default: the document setting). JavaScript receives 64-bit values as <c>bigint</c>.
    /// <ja>整数として読む。<paramref name="type"/>: <c>i8 u8 i16 u16 i24 u24 i32 u32 i64 u64</c>。<paramref name="endian"/>: <c>"le"</c> / <c>"be"</c> (省略時はドキュメントの既定)。64 bit の値は JavaScript では <c>bigint</c>。</ja>
    /// </summary>
    [WideIntegerResult("type")]
    [TsType("number | bigint")]
    BigInteger ReadInt(OffsetArg offset, string type, string? endian = null);

    /// <summary>Reads a floating point value. <paramref name="type"/>: <c>f16 f32 f64</c>. <ja>浮動小数点として読む。<paramref name="type"/>: <c>f16 f32 f64</c>。</ja></summary>
    double ReadFloat(OffsetArg offset, string type, string? endian = null);

    /// <summary>Reads a string of at most <paramref name="maxLength"/> bytes. <ja>最大 <paramref name="maxLength"/> バイトを文字列として読む。</ja></summary>
    string ReadString(OffsetArg offset, long maxLength, EncodingArg? encoding = null, ReadStringOptions? options = null);

    /// <summary>
    /// Overwrites bytes (the length does not change). Data past the end is appended when the document can resize, otherwise <see cref="RangeError"/>.
    /// <ja>上書きする (長さは変わらない)。末尾を越える部分は、長さを変えられるドキュメントなら追加し、変えられないなら <see cref="RangeError"/>。</ja>
    /// </summary>
    void WriteBytes(OffsetArg offset, BytePattern data);

    /// <summary>Writes an integer (arguments as in <see cref="ReadInt"/>). <ja>整数を書く (引数は <see cref="ReadInt"/> と対応)。</ja></summary>
    void WriteInt(OffsetArg offset, string type, [TsType("number | bigint")] BigInteger value, string? endian = null);

    /// <summary>Writes a floating point value. <ja>浮動小数点の値を書く。</ja></summary>
    void WriteFloat(OffsetArg offset, string type, double value, string? endian = null);

    /// <summary>Writes a string and returns the number of bytes written. <ja>文字列を書き、書いたバイト数を返す。</ja></summary>
    long WriteString(OffsetArg offset, string text, EncodingArg? encoding = null);

    /// <summary>Inserts bytes. <ja>挿入する。</ja></summary>
    /// <exception cref="ReadOnlyError">The document cannot resize. <ja>長さを変えられない。</ja></exception>
    void InsertBytes(OffsetArg offset, BytePattern data);

    /// <summary>Deletes a range. <ja>範囲を削除する。</ja></summary>
    void Delete(RangeArg range);

    /// <summary>
    /// Fills a range with a pattern (bytes, Hex or <c>"random"</c>). Finishes within 100 ms regardless of the size.
    /// <ja>パターン (バイト列・Hex・<c>"random"</c>) で塗りつぶす。データ量に関係なく 100 ms 以内に終わる。</ja>
    /// </summary>
    void Fill(RangeArg range, BytePattern pattern, FillOptions? options = null);

    /// <summary>Changes the length (truncates or extends with <paramref name="fill"/>). <ja>長さを変える (切り詰め・<paramref name="fill"/> で延長)。</ja></summary>
    void Resize(OffsetArg newLength, BytePattern? fill = null);

    // ---- AUTO-09 一括処理 ----

    /// <summary>
    /// Data operation over a range. <paramref name="op"/>: <c>xor and or not add sub mul div shl shr rol ror neg swap reverse</c>.
    /// <ja>範囲のデータ演算。<paramref name="op"/>: <c>xor and or not add sub mul div shl shr rol ror neg swap reverse</c>。</ja>
    /// </summary>
    Task TransformAsync(RangeArg range, string op, TransformOperand? operand = null, TransformOptions? options = null);

    /// <summary>Finds all matches (at most <c>options.max</c>). <ja>すべて検索する (最大 <c>options.max</c> 件)。</ja></summary>
    Task<SearchResults> FindAllAsync(BytePattern pattern, FindOptions? options = null);

    /// <summary>Finds the first match, or null. <ja>最初の 1 件を探す。見つからなければ null。</ja></summary>
    Task<ByteRange?> FindAsync(BytePattern pattern, FindOptions? options = null);

    /// <summary>Replaces all matches and returns the count. <ja>すべて置換し、置換した件数を返す。</ja></summary>
    Task<long> ReplaceAllAsync(BytePattern pattern, BytePattern replacement, FindOptions? options = null);

    /// <summary>Counts the matches. <ja>一致の件数だけを数える。</ja></summary>
    Task<long> CountAsync(BytePattern pattern, FindOptions? options = null);

    /// <summary>
    /// Copies a range to <paramref name="destination"/> by reference (no data is duplicated).
    /// <ja>範囲を <paramref name="destination"/> にコピーする。データエンジンの参照で行い、実データを複製しない。</ja>
    /// </summary>
    Task CopyRangeAsync(RangeArg source, IDocument destination, OffsetArg destinationOffset, CopyRangeOptions? options = null);

    /// <summary>
    /// Writes a range to a file (permission <c>file-write</c> or a path the user picked).
    /// <ja>範囲をファイルに書き出す (許可 <c>file-write</c> または利用者が選んだパス)。</ja>
    /// </summary>
    [RequiresPermission("file-write")]
    Task ExportRangeAsync(RangeArg range, string path, ExportRangeOptions? options = null);

    /// <summary>
    /// Reads a range in chunks of <paramref name="chunkSize"/> (default 1 MB, at most 16 MB).
    /// JavaScript: <c>for await (const { offset, data } of doc.chunks(range))</c>.
    /// <ja>範囲を <paramref name="chunkSize"/> (既定 1 MB、最大 16 MB) ごとに読む。JavaScript では <c>for await (const { offset, data } of doc.chunks(range))</c>。</ja>
    /// </summary>
    IAsyncEnumerable<DataChunk> Chunks(RangeArg? range = null, long chunkSize = 1024 * 1024);

    /// <summary>
    /// Reads a range chunk by chunk and overwrites each chunk with what <paramref name="transform"/> returns (same length).
    /// <ja>範囲をチャンクごとに読み、<paramref name="transform"/> が返したバイト列 (同じ長さ) で上書きする。</ja>
    /// </summary>
    /// <exception cref="ArgumentError">The function returned a different length; earlier chunks stay changed and can be undone. <ja>長さの違うバイト列を返した。それより前の変更は残り、Undo できる。</ja></exception>
    Task TransformChunksAsync(RangeArg range,
        [TsType("(data: Uint8Array, offset: number) => Uint8Array | number[]")] Func<byte[], long, byte[]> transform, AsyncOptions? options = null);

    /// <summary>Counts each byte value 0-255. <ja>バイト値 0〜255 の出現数 (長さ 256 の配列)。</ja></summary>
    Task<long[]> HistogramAsync(RangeArg? range = null, AsyncOptions? options = null);

    /// <summary>
    /// Shannon entropy in bits per byte; with <c>options.blockSize</c>, one value per block.
    /// <ja>エントロピー (ビット / バイト)。<c>options.blockSize</c> を指定するとブロックごとの配列。</ja>
    /// </summary>
    [TsType("Promise<number | number[]>")]
    Task<object> EntropyAsync(RangeArg? range = null, EntropyOptions? options = null);

    /// <summary>Compares with another document and returns the differing ranges. <ja>別のドキュメントと比較し、差分の範囲の配列を返す。</ja></summary>
    Task<IReadOnlyList<ByteRange>> CompareAsync(IDocument other, CompareOptions? options = null);

    /// <summary>Extracts strings. <ja>文字列を抽出する。</ja></summary>
    Task<IReadOnlyList<FoundString>> StringsAsync(StringsOptions? options = null);

    /// <summary>
    /// Applies a structure template (AUTO-13) and returns the root of the result tree.
    /// <ja>構造体テンプレートを適用し (AUTO-13)、解析結果のツリーの根を返す。</ja>
    /// </summary>
    Task<ITemplateNode> ApplyTemplateAsync(string nameOrPath, ApplyTemplateOptions? options = null);
}

/// <summary>The cursor selection including multi and rectangular selections (AUTO-10). <ja>選択範囲 (マルチ選択・矩形選択を含む。AUTO-10)。</ja></summary>
public interface ISelection
{
    /// <summary>Start of the primary range. <ja>主選択範囲の開始。</ja></summary>
    long Start { get; }

    /// <summary>End of the primary range (exclusive). <ja>主選択範囲の終了 (含まない)。</ja></summary>
    long End { get; }

    /// <summary>Length of the primary range. <ja>主選択範囲の長さ。</ja></summary>
    long Length { get; }

    /// <summary>All selected ranges; empty when nothing is selected. <ja>すべての選択範囲。選択がなければ空。</ja></summary>
    IReadOnlyList<ByteRange> Ranges { get; }

    /// <summary>True when nothing is selected. <ja>選択がないか。</ja></summary>
    bool IsEmpty { get; }

    /// <summary>True for a rectangular selection. <ja>矩形選択か。</ja></summary>
    bool IsRectangular { get; }

    /// <summary>Selects a range. <ja>範囲を選択する。</ja></summary>
    void Set(RangeArg range);

    /// <summary>Adds a range to the multi-selection. <ja>マルチ選択に範囲を追加する。</ja></summary>
    void Add(RangeArg range);

    /// <summary>Clears the selection. <ja>選択を解除する。</ja></summary>
    void Clear();

    /// <summary>Selects a rectangle. <ja>矩形選択する。</ja></summary>
    void SetRectangle(OffsetArg startOffset, long rows, long columns);

    /// <summary>Turns results of a bulk API into a multi-selection (at most 1,000,000). <ja>一括処理の結果をマルチ選択にする (100 万件まで)。</ja></summary>
    /// <exception cref="LimitError">More than 1,000,000 ranges. <ja>100 万件を超える。</ja></exception>
    void FromResults(IReadOnlyList<ByteRange> results);
}

/// <summary>Temporary highlights (AUTO-07). <ja>一時的な強調表示 (AUTO-07)。</ja></summary>
public interface IHighlights
{
    /// <summary>Highlights a range with a named color. <ja>範囲を名前付きの色で強調表示する。</ja></summary>
    void Add(RangeArg range, string? color = null, string? label = null);

    /// <summary>Removes all highlights of this script. <ja>このスクリプトの強調表示をすべて消す。</ja></summary>
    void Clear();
}
