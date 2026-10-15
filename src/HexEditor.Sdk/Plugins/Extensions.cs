namespace HexEditor.Sdk.Plugins;

/// <summary>
/// An extension registered by a plugin (AUTO-31). Data-proportional work (hashes, import, export, data transforms) receives the data
/// as chunks; the application never passes the whole data at once.
/// <ja>プラグインが登録する拡張ポイント (AUTO-31)。データ量に比例する処理 (ハッシュ、インポート・エクスポート、データ演算) はデータをチャンクで受け取る。全データを一度に渡すことはない。</ja>
/// </summary>
public interface IExtension
{
    /// <summary>The ID (<c>&lt;plugin ID&gt;.&lt;name&gt;</c>). <ja>ID (<c>&lt;プラグイン ID&gt;.&lt;名前&gt;</c>)。</ja></summary>
    string Id { get; }

    /// <summary>The display name in the UI language. <ja>表示言語での表示名。</ja></summary>
    string DisplayName { get; }
}

/// <summary>Cancellation and progress of a long operation. <ja>長い処理の取り消しと進捗。</ja></summary>
public sealed class PluginOperation(CancellationToken cancellationToken, IProgress<long>? progress = null)
{
    /// <summary>Requested when the user stops the operation. <ja>利用者が止めたときに要求される。</ja></summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Reports the processed bytes (the application also reports its own progress). <ja>処理したバイト数を知らせる (アプリも自分で進捗を出す)。</ja></summary>
    public IProgress<long>? Progress { get; } = progress;

    /// <summary>An operation that cannot be canceled. <ja>取り消せない処理。</ja></summary>
    public static PluginOperation None { get; } = new(CancellationToken.None);
}

/// <summary>Where the bytes produced by a plugin go. <ja>プラグインが作るバイト列の書き先。</ja></summary>
public interface IByteSink
{
    /// <summary>The offset of the next write. Setting it moves the write position (for formats with addresses). <ja>次に書く位置。代入すると書く位置を移す (アドレスを持つ形式用)。</ja></summary>
    long Position { get; set; }

    /// <summary>Writes bytes at <see cref="Position"/> and advances it. <ja><see cref="Position"/> に書き、その分進める。</ja></summary>
    void Write(ReadOnlySpan<byte> data);
}

/// <summary>A data inspector type (05, INSP). Called on the UI thread: finish within 10 ms. <ja>データインスペクタの型 (05)。UI スレッドで呼ばれる。10 ms 以内に終える。</ja></summary>
public interface IInspectorType : IExtension
{
    /// <summary>The number of bytes the type reads. <ja>型が読むバイト数。</ja></summary>
    int Size { get; }

    /// <summary>True when the value depends on the endianness. <ja>エンディアンで解釈が変わるか。</ja></summary>
    bool HasEndian => false;

    /// <summary>The text shown for <paramref name="data"/> (at least <see cref="Size"/> bytes). <ja><paramref name="data"/> (<see cref="Size"/> バイト以上) の表示文字列。</ja></summary>
    string Decode(ReadOnlySpan<byte> data, bool bigEndian);

    /// <summary>The bytes for a value text, or null when the type is read-only. <ja>値の文字列から作るバイト列。読み取り専用の型は null。</ja></summary>
    byte[]? Encode(string text, bool bigEndian) => null;
}

/// <summary>A hash or checksum algorithm (06, ANA-19). <ja>ハッシュ・チェックサムのアルゴリズム (06)。</ja></summary>
public interface IHashAlgorithm : IExtension
{
    /// <summary>The bit length of the value. <ja>値のビット数。</ja></summary>
    int Bits { get; }

    /// <summary>Starts one computation. <ja>1 回の計算を始める。</ja></summary>
    IHashComputation Create();
}

/// <summary>One running hash computation. <ja>計算中のハッシュ 1 つ。</ja></summary>
public interface IHashComputation
{
    /// <summary>Adds the next chunk. <ja>次のチャンクを渡す。</ja></summary>
    void Append(ReadOnlySpan<byte> data);

    /// <summary>Returns the value (numbers as big-endian bytes). <ja>値を返す (数値はビッグエンディアンのバイト列)。</ja></summary>
    byte[] Finish();
}

/// <summary>An import format: converts text or bytes into bytes (11). <ja>インポート形式: ファイル・文字列をバイト列に変換する (11)。</ja></summary>
public interface IImportFormat : IExtension
{
    /// <summary>File extensions (with the dot) suggested in the open dialog. <ja>ファイルを選ぶダイアログに出す拡張子 (ドット付き)。</ja></summary>
    IReadOnlyList<string> FileExtensions { get; }

    /// <summary>True when the head of the input (up to 64 KB) is in this format (format detection). <ja>入力の先頭 (64 KB まで) がこの形式か (形式の自動判定)。</ja></summary>
    bool Detect(ReadOnlySpan<byte> head) => false;

    /// <summary>Starts one conversion. <ja>1 回の変換を始める。</ja></summary>
    IImportSession Begin(PluginOperation operation);
}

/// <summary>One running import. <ja>実行中のインポート 1 つ。</ja></summary>
public interface IImportSession
{
    /// <summary>Converts the next chunk of the input. <ja>入力の次のチャンクを変換する。</ja></summary>
    void Write(ReadOnlySpan<byte> input, IByteSink output);

    /// <summary>Called after the last chunk. <ja>最後のチャンクの後に呼ばれる。</ja></summary>
    void Complete(IByteSink output);
}

/// <summary>What is exported. <ja>エクスポートするもの。</ja></summary>
/// <param name="Offset">The offset of the first byte. <ja>先頭のオフセット。</ja></param>
/// <param name="Length">The number of bytes. <ja>バイト数。</ja></param>
/// <param name="FileName">The document name. <ja>ドキュメントの名前。</ja></param>
public sealed record ExportInfo(long Offset, long Length, string FileName);

/// <summary>An export format, also offered by Copy As (03, 11). <ja>エクスポート形式。Copy As (03) の形式としても使える (03、11)。</ja></summary>
public interface IExportFormat : IExtension
{
    /// <summary>The file extension (with the dot). <ja>ファイルの拡張子 (ドット付き)。</ja></summary>
    string FileExtension { get; }

    /// <summary>True when the output is UTF-8 text (it can then be copied as text). <ja>出力が UTF-8 のテキストか (テキストとしてコピーできる)。</ja></summary>
    bool IsText { get; }

    /// <summary>Starts one export. <ja>1 回のエクスポートを始める。</ja></summary>
    IExportSession Begin(ExportInfo info, PluginOperation operation);
}

/// <summary>One running export. <ja>実行中のエクスポート 1 つ。</ja></summary>
public interface IExportSession
{
    /// <summary>Converts the next chunk (<paramref name="offset"/> is the document offset of its first byte). <ja>次のチャンクを変換する (<paramref name="offset"/> は先頭のバイトのドキュメントでの位置)。</ja></summary>
    void Write(long offset, ReadOnlySpan<byte> data, IByteSink output);

    /// <summary>Called after the last chunk. <ja>最後のチャンクの後に呼ばれる。</ja></summary>
    void Complete(IByteSink output);
}

/// <summary>A new kind of data source (01), for example a remote device or a container. <ja>新しい種類のデータソース (01)。例: リモートの機器、独自のコンテナ。</ja></summary>
public interface IByteSourceProvider : IExtension
{
    /// <summary>True when the opened data cannot be written. <ja>開いたデータに書き込めないか。</ja></summary>
    bool IsReadOnly { get; }

    /// <summary>True when the length can change. <ja>長さを変えられるか。</ja></summary>
    bool CanResize { get; }

    /// <summary>The label of the argument asked when opening (null = no argument). <ja>開くときに尋ねる引数の見出し (null なら尋ねない)。</ja></summary>
    string? ArgumentLabel => null;

    /// <summary>Opens the data. <ja>データを開く。</ja></summary>
    IPluginByteSource Open(string? argument);
}

/// <summary>Opened data of a <see cref="IByteSourceProvider"/>. Called from worker threads. <ja><see cref="IByteSourceProvider"/> で開いたデータ。作業用のスレッドから呼ばれる。</ja></summary>
public interface IPluginByteSource : IDisposable
{
    /// <summary>The display name of the document. <ja>ドキュメントの表示名。</ja></summary>
    string DisplayName { get; }

    /// <summary>The length in bytes. <ja>長さ (バイト)。</ja></summary>
    long Length { get; }

    /// <summary>Reads into <paramref name="buffer"/> and returns the number of bytes read. <ja><paramref name="buffer"/> に読み、読んだバイト数を返す。</ja></summary>
    int Read(long offset, Span<byte> buffer);

    /// <summary>Writes bytes (only when not read-only). <ja>書き込む (読み取り専用でない場合だけ)。</ja></summary>
    void Write(long offset, ReadOnlySpan<byte> data) => throw new NotSupportedException();

    /// <summary>Changes the length (only when the length can change). <ja>長さを変える (長さを変えられる場合だけ)。</ja></summary>
    void SetLength(long length) => throw new NotSupportedException();
}

/// <summary>
/// A transformed view of the data (03, EDIT-45), for example decrypted or bit-inverted. Writes go through <see cref="Inverse"/>.
/// <ja>元のデータを変換して見せるビュー (03 の EDIT-45)。例: 復号して見せる、ビット反転して見せる。書き込みは <see cref="Inverse"/> で元に戻す。</ja>
/// </summary>
public interface ITransformView : IExtension
{
    /// <summary>Transforms <paramref name="data"/> (document bytes from <paramref name="offset"/>) in place. <ja><paramref name="data"/> (<paramref name="offset"/> からのドキュメントのバイト) をその場で変換する。</ja></summary>
    void Forward(long offset, Span<byte> data);

    /// <summary>True when <see cref="Inverse"/> is implemented (otherwise the view is read-only). <ja><see cref="Inverse"/> を実装しているか (しなければ読み取り専用のビュー)。</ja></summary>
    bool CanInverse => false;

    /// <summary>Turns displayed bytes back into document bytes in place. <ja>表示上のバイトをその場で元のデータに戻す。</ja></summary>
    void Inverse(long offset, Span<byte> data) => throw new NotSupportedException();
}

/// <summary>A data operation offered in the data operations menu (03). The length does not change. <ja>データ演算 (03) のメニューに出る変換。長さは変えない。</ja></summary>
public interface IDataTransform : IExtension
{
    /// <summary>Transforms the next chunk in place (<paramref name="offset"/> is the document offset of its first byte). <ja>次のチャンクをその場で変換する (<paramref name="offset"/> は先頭のバイトのドキュメントでの位置)。</ja></summary>
    void Apply(long offset, Span<byte> data, PluginOperation operation);
}

/// <summary>The document a plugin command works on. <ja>プラグインのコマンドが対象にするドキュメント。</ja></summary>
public interface IPluginDocument
{
    /// <summary>The display name. <ja>表示名。</ja></summary>
    string Name { get; }

    /// <summary>The path, or null. <ja>パス。なければ null。</ja></summary>
    string? Path { get; }

    /// <summary>The length in bytes. <ja>長さ (バイト)。</ja></summary>
    long Length { get; }

    /// <summary>The cursor offset. <ja>カーソルのオフセット。</ja></summary>
    long Cursor { get; }

    /// <summary>The selection start and length (length 0 = no selection). <ja>選択範囲の開始と長さ (長さ 0 は選択なし)。</ja></summary>
    (long Start, long Length) Selection { get; }

    /// <summary>Reads up to 16 MB. <ja>16 MB まで読む。</ja></summary>
    byte[] Read(long offset, int count);

    /// <summary>Overwrites bytes as one undoable edit. <ja>上書きする (1 回の Undo で戻せる編集)。</ja></summary>
    void Write(long offset, ReadOnlySpan<byte> data);
}

/// <summary>What a command receives. <ja>コマンドが受け取るもの。</ja></summary>
public interface ICommandContext
{
    /// <summary>The active document, or null. <ja>作業中のドキュメント。なければ null。</ja></summary>
    IPluginDocument? Document { get; }

    /// <summary>The output of the plugin. <ja>プラグインの出力。</ja></summary>
    IPluginLog Log { get; }
}

/// <summary>A command (09): shown in the command palette and can get a shortcut. <ja>コマンド (09)。コマンドパレットに出て、ショートカットを割り当てられる。</ja></summary>
public interface ICommand : IExtension
{
    /// <summary>True when the command can run now. Called on the UI thread: finish within 10 ms. <ja>今実行できるか。UI スレッドで呼ばれる。10 ms 以内に終える。</ja></summary>
    bool CanExecute(ICommandContext context) => true;

    /// <summary>Runs the command (on the UI thread; start long work on another thread). <ja>実行する (UI スレッドで呼ばれる。長い処理は別のスレッドで行う)。</ja></summary>
    void Execute(ICommandContext context);
}

/// <summary>Where a menu contribution is placed. <ja>メニューの項目を置く場所。</ja></summary>
public enum MenuLocation
{
    /// <summary>Tools &gt; Plugins. <ja>ツール &gt; プラグイン。</ja></summary>
    ToolsPlugins,

    /// <summary>The Plugins submenu of the editor context menu. <ja>右クリックメニューの「プラグイン」。</ja></summary>
    ContextMenu,
}

/// <summary>A menu item that runs a command of the plugin (09). <ja>プラグインのコマンドを実行するメニューの項目 (09)。</ja></summary>
public interface IMenuContribution : IExtension
{
    /// <summary>The ID of a command registered by the same plugin. <ja>同じプラグインが登録したコマンドの ID。</ja></summary>
    string CommandId { get; }

    /// <summary>Where the item is placed. <ja>置く場所。</ja></summary>
    MenuLocation Location { get; }
}

/// <summary>A function callable from the template language (07). <ja>テンプレート言語から呼べる関数 (07)。</ja></summary>
public interface ITemplateFunction : IExtension
{
    /// <summary>The name used in templates. <ja>テンプレートで使う名前。</ja></summary>
    string FunctionName { get; }

    /// <summary>The number of parameters. <ja>引数の数。</ja></summary>
    int ParameterCount { get; }

    /// <summary>Calls the function. Values are long, double, bool, string, byte[] or null. <ja>呼び出す。値は long・double・bool・string・byte[]・null。</ja></summary>
    object? Invoke(IReadOnlyList<object?> args);
}

/// <summary>A detected file type. <ja>判定したファイル形式。</ja></summary>
/// <param name="Name">The format name. <ja>形式名。</ja></param>
/// <param name="Mime">The MIME type, or null. <ja>MIME タイプ。なければ null。</ja></param>
/// <param name="Extension">The usual extension (without the dot), or null. <ja>よく使う拡張子 (ドットなし)。なければ null。</ja></param>
public sealed record FileTypeMatch(string Name, string? Mime = null, string? Extension = null);

/// <summary>Detects file types by magic (07 automatic template selection, ANA-17). <ja>マジックによる形式の判定 (07 のテンプレートの自動適用、ANA-17)。</ja></summary>
public interface IFileTypeDetector : IExtension
{
    /// <summary>The formats that match the head of the file. <ja>ファイルの先頭に一致する形式。</ja></summary>
    IReadOnlyList<FileTypeMatch> Detect(ReadOnlySpan<byte> head);
}

/// <summary>One disassembled instruction. <ja>逆アセンブルした命令 1 つ。</ja></summary>
/// <param name="Address">The address. <ja>アドレス。</ja></param>
/// <param name="Length">The length in bytes. <ja>長さ (バイト)。</ja></param>
/// <param name="Text">The instruction text. <ja>命令の文字列。</ja></param>
public sealed record DisassembledInstruction(long Address, int Length, string Text);

/// <summary>An additional disassembler architecture (06). <ja>逆アセンブラの追加のアーキテクチャ (06)。</ja></summary>
public interface IDisassembler : IExtension
{
    /// <summary>The architecture name. <ja>アーキテクチャの名前。</ja></summary>
    string Architecture { get; }

    /// <summary>Disassembles at most <paramref name="maxInstructions"/> instructions. <ja>最大 <paramref name="maxInstructions"/> 個の命令を逆アセンブルする。</ja></summary>
    IReadOnlyList<DisassembledInstruction> Disassemble(ReadOnlySpan<byte> code, long address, int maxInstructions);
}

/// <summary>
/// A coloring rule (05, INSP-33) given as data: the application evaluates it like its own rules. <see cref="Kind"/> is
/// <c>byteValues</c>, <c>hexPattern</c>, <c>text</c> or <c>regex</c>; colors are 0xRRGGBB.
/// <ja>値に応じた色付けの規則 (05 の INSP-33)。データとして渡し、アプリが自分の規則と同じく評価する。<see cref="Kind"/> は <c>byteValues</c>・<c>hexPattern</c>・<c>text</c>・<c>regex</c>。色は 0xRRGGBB。</ja>
/// </summary>
public sealed record PluginColorRule(string Name, string Kind, string Pattern, int? Foreground, int? Background);

/// <summary>Provides coloring rules (05). <ja>色付けの規則を提供する (05)。</ja></summary>
public interface IColorRuleProvider : IExtension
{
    /// <summary>The rules. <ja>規則。</ja></summary>
    IReadOnlyList<PluginColorRule> Rules { get; }
}

/// <summary>Provides the object scripts get with <c>app.plugins.get("id").api</c> (AUTO-04). <ja>スクリプトが <c>app.plugins.get("id").api</c> で使うオブジェクトを提供する (AUTO-04)。</ja></summary>
public interface IScriptApiProvider : IExtension
{
    /// <summary>Creates the object (its public methods and properties are visible to scripts). <ja>オブジェクトを作る (公開のメソッドとプロパティがスクリプトから見える)。</ja></summary>
    object CreateApi();
}
