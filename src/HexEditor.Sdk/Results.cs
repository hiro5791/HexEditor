namespace HexEditor.Sdk;

/// <summary>
/// The results of a bulk search: the matched ranges in order. <see cref="Truncated"/> is true when the search stopped at the maximum count.
/// <ja>一括の検索の結果 (一致した範囲の並び)。上限の件数で打ち切った場合は <see cref="Truncated"/> が true。</ja>
/// </summary>
public sealed class SearchResults(IReadOnlyList<ByteRange> items, bool truncated) : IReadOnlyList<ByteRange>
{
    /// <summary>True when the results were cut at the maximum count. <ja>上限の件数で打ち切ったか。</ja></summary>
    public bool Truncated { get; } = truncated;

    /// <summary>The number of results. <ja>件数。</ja></summary>
    public int Count => items.Count;

    /// <summary>The result at <paramref name="index"/>. <ja><paramref name="index"/> 番目の結果。</ja></summary>
    public ByteRange this[int index] => items[index];

    /// <summary>Enumerates the results. <ja>結果を列挙する。</ja></summary>
    public IEnumerator<ByteRange> GetEnumerator() => items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One chunk read by <see cref="IDocument.Chunks"/>. <ja><see cref="IDocument.Chunks"/> が読んだ 1 つのチャンク。</ja></summary>
/// <param name="Offset">Offset of the first byte. <ja>先頭のバイトのオフセット。</ja></param>
/// <param name="Data">The bytes. <ja>バイト列。</ja></param>
public sealed record DataChunk(long Offset, byte[] Data);

/// <summary>A command of the application. <ja>アプリのコマンド。</ja></summary>
/// <param name="Id">The fixed English identifier (for example <c>edit.fill</c>). <ja>固定の英語の識別子 (例: <c>edit.fill</c>)。</ja></param>
/// <param name="Name">The display name in the UI language. <ja>表示言語での表示名。</ja></param>
/// <param name="Category">The category. <ja>分類。</ja></param>
/// <param name="Arguments">Names of the arguments the command accepts. <ja>受け付ける引数の名前。</ja></param>
public sealed record CommandInfo(string Id, string Name, string Category, IReadOnlyList<string> Arguments);

/// <summary>A data inspector type. <ja>データインスペクタの型。</ja></summary>
/// <param name="Id">Type ID. <ja>型の ID。</ja></param>
/// <param name="Name">Display name. <ja>表示名。</ja></param>
/// <param name="Size">Size in bytes (0 when variable). <ja>サイズ (バイト。可変長は 0)。</ja></param>
public sealed record InspectorTypeInfo(string Id, string Name, int Size);

/// <summary>A decoded inspector value. <ja>インスペクタで解釈した値。</ja></summary>
/// <param name="Text">The text shown in the inspector panel. <ja>インスペクタパネルに表示する文字列。</ja></param>
/// <param name="Value">The raw value (number, bigint, string or boolean; null when it cannot be decoded). <ja>生の値 (数値・bigint・文字列・真偽値。解釈できなければ null)。</ja></param>
public sealed record InspectorValue(string Text, [property: TsType("number | bigint | string | boolean | null")] object? Value);

/// <summary>A text encoding the application supports. <ja>アプリが対応する文字コード。</ja></summary>
/// <param name="CodePage">Code page number. <ja>コードページ番号。</ja></param>
/// <param name="Name">Name (for example <c>shift_jis</c>). <ja>名前 (例: <c>shift_jis</c>)。</ja></param>
/// <param name="DisplayName">Display name. <ja>表示名。</ja></param>
public sealed record EncodingInfo(int CodePage, string Name, string DisplayName);

/// <summary>A structure template. <ja>構造体テンプレート。</ja></summary>
/// <param name="Name">Template name. <ja>テンプレートの名前。</ja></param>
/// <param name="Path">Template file path. <ja>テンプレートのファイルのパス。</ja></param>
/// <param name="Extensions">File extensions the template is for. <ja>対応する拡張子。</ja></param>
public sealed record TemplateInfo(string Name, string Path, IReadOnlyList<string> Extensions);

/// <summary>A hash algorithm. <ja>ハッシュのアルゴリズム。</ja></summary>
/// <param name="Id">Algorithm ID (for example <c>sha256</c>). <ja>アルゴリズムの ID (例: <c>sha256</c>)。</ja></param>
/// <param name="Name">Display name. <ja>表示名。</ja></param>
/// <param name="Bits">Output size in bits. <ja>出力のビット数。</ja></param>
public sealed record HashAlgorithmInfo(string Id, string Name, int Bits);

/// <summary>A hash result. <ja>ハッシュの結果。</ja></summary>
/// <param name="Algorithm">Algorithm ID. <ja>アルゴリズムの ID。</ja></param>
/// <param name="Hex">The value as lowercase Hex. <ja>値 (小文字の Hex)。</ja></param>
/// <param name="Bytes">The value as bytes. <ja>値 (バイト列)。</ja></param>
public sealed record HashResult(string Algorithm, string Hex, byte[] Bytes);

/// <summary>A string found by <see cref="IDocument.StringsAsync"/>. <ja><see cref="IDocument.StringsAsync"/> が見つけた文字列。</ja></summary>
/// <param name="Start">Start offset. <ja>開始オフセット。</ja></param>
/// <param name="Length">Length in bytes. <ja>長さ (バイト)。</ja></param>
/// <param name="Text">The text. <ja>文字列。</ja></param>
/// <param name="Encoding">The encoding name. <ja>文字コードの名前。</ja></param>
public sealed record FoundString(long Start, long Length, string Text, string Encoding);

/// <summary>File information. <ja>ファイルの情報。</ja></summary>
/// <param name="Size">Size in bytes (0 for folders). <ja>サイズ (バイト。フォルダは 0)。</ja></param>
/// <param name="Modified">Last write time (ISO 8601, UTC). <ja>最終更新日時 (ISO 8601、UTC)。</ja></param>
/// <param name="IsFolder">True for folders. <ja>フォルダか。</ja></param>
public sealed record FileStat(long Size, string Modified, bool IsFolder);

/// <summary>The response of <see cref="INet.FetchAsync"/>. <ja><see cref="INet.FetchAsync"/> の応答。</ja></summary>
/// <param name="Status">HTTP status code. <ja>HTTP の状態コード。</ja></param>
/// <param name="Headers">Response headers. <ja>応答のヘッダー。</ja></param>
/// <param name="Body">The body (<c>Uint8Array</c> or string, by <see cref="FetchOptions.ResponseType"/>). <ja>本文 (<see cref="FetchOptions.ResponseType"/> により <c>Uint8Array</c> か文字列)。</ja></param>
public sealed record FetchResponse(int Status, IReadOnlyDictionary<string, string> Headers, [property: TsType("Uint8Array | string")] object Body);

/// <summary>The result of <see cref="IProcessRunner.RunAsync"/>. <ja><see cref="IProcessRunner.RunAsync"/> の結果。</ja></summary>
/// <param name="ExitCode">Exit code. <ja>終了コード。</ja></param>
/// <param name="Stdout">Standard output. <ja>標準出力。</ja></param>
/// <param name="Stderr">Standard error. <ja>標準エラー出力。</ja></param>
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>A new bookmark for <see cref="IBookmarks.Add"/>. <ja><see cref="IBookmarks.Add"/> に渡す新しいブックマーク。</ja></summary>
public sealed class BookmarkSpec
{
    /// <summary>Start offset. <ja>開始オフセット。</ja></summary>
    public OffsetArg Start { get; init; }

    /// <summary>Length (default 1). <ja>長さ (既定 1)。</ja></summary>
    public long Length { get; init; } = 1;

    /// <summary>Name. <ja>名前。</ja></summary>
    public string? Name { get; init; }

    /// <summary>A named color of the bookmark palette (for example <c>"red"</c>). <ja>ブックマークの色パレットの名前 (例: <c>"red"</c>)。</ja></summary>
    public string? Color { get; init; }

    /// <summary>Comment. <ja>コメント。</ja></summary>
    public string? Comment { get; init; }
}

/// <summary>
/// The argument passed to event handlers (<see cref="IApp.On"/>).
/// <ja>イベントのハンドラに渡す引数 (<see cref="IApp.On"/>)。</ja>
/// </summary>
public abstract class ScriptEventArgs
{
    /// <summary>The event name (for example <c>beforeSave</c>). <ja>イベントの名前 (例: <c>beforeSave</c>)。</ja></summary>
    public abstract string Name { get; }

    /// <summary>The document the event is about, if any. <ja>イベントの対象のドキュメント (あれば)。</ja></summary>
    public abstract IDocument? Document { get; }

    /// <summary>
    /// Cancels the operation (<c>beforeSave</c> only). The reason is shown to the user.
    /// <ja>操作を中止する (<c>beforeSave</c> のみ)。理由は利用者に表示する。</ja>
    /// </summary>
    public abstract void Cancel(string reason);
}

/// <summary>A subscription returned by <see cref="IApp.On"/>. <ja><see cref="IApp.On"/> が返す購読。</ja></summary>
public interface IEventSubscription
{
    /// <summary>Stops receiving the event. <ja>イベントの受け取りをやめる。</ja></summary>
    void Off();
}
