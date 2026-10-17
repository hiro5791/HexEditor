using System.Text.Json.Nodes;

namespace HexEditor.Sdk;

/// <summary>
/// The bookmarks of a document (AUTO-11). Enumerable like an array; bookmarks added by scripts are saved like the user's.
/// <ja>ドキュメントのブックマーク (AUTO-11)。配列のように列挙できる。スクリプトが追加したものも利用者のものと同じく保存される。</ja>
/// </summary>
public interface IBookmarks : IReadOnlyList<IBookmark>
{
    /// <summary>Adds a bookmark. <ja>ブックマークを追加する。</ja></summary>
    /// <exception cref="RangeError">Out of the document. <ja>範囲外。</ja></exception>
    /// <exception cref="ArgumentError">Unknown color name. <ja>存在しない色の名前。</ja></exception>
    IBookmark Add(BookmarkSpec bookmark);

    /// <summary>Removes a bookmark. <ja>ブックマークを削除する。</ja></summary>
    void Remove(IBookmark bookmark);

    /// <summary>Removes all bookmarks. <ja>すべて削除する。</ja></summary>
    void Clear();

    /// <summary>Finds a bookmark by name, or null. <ja>名前でブックマークを探す。なければ null。</ja></summary>
    IBookmark? Find(string name);

    /// <summary>
    /// Adds one bookmark per result (at most 1,000,000). <c>{index}</c> in <paramref name="nameFormat"/> becomes 1, 2, 3, ...
    /// <ja>結果ごとにブックマークを追加する (100 万件まで)。<paramref name="nameFormat"/> の <c>{index}</c> は 1 から始まる連番。</ja>
    /// </summary>
    long AddFromResults(IReadOnlyList<ByteRange> results, string nameFormat = "{index}");
}

/// <summary>One bookmark. <ja>1 つのブックマーク。</ja></summary>
public interface IBookmark
{
    /// <summary>Name. <ja>名前。</ja></summary>
    string Name { get; }

    /// <summary>Start offset. <ja>開始オフセット。</ja></summary>
    long Start { get; }

    /// <summary>Length. <ja>長さ。</ja></summary>
    long Length { get; }

    /// <summary>Named color. <ja>色の名前。</ja></summary>
    string Color { get; }

    /// <summary>Comment. <ja>コメント。</ja></summary>
    string Comment { get; }

    /// <summary>The number of a numbered bookmark (1-9), or null. <ja>番号付きブックマークの番号 (1〜9)。なければ null。</ja></summary>
    int? Number { get; }
}

/// <summary>Data inspector types and value conversion (AUTO-12). <ja>データインスペクタの型と値の変換 (AUTO-12)。</ja></summary>
public interface IInspector
{
    /// <summary>The available types, including plugin types. <ja>使える型の一覧 (プラグインの型を含む)。</ja></summary>
    IReadOnlyList<InspectorTypeInfo> Types { get; }

    /// <summary>Decodes the value at <paramref name="offset"/> as the inspector panel shows it. <ja><paramref name="offset"/> の値をインスペクタパネルと同じく解釈する。</ja></summary>
    /// <exception cref="ArgumentError">Unknown type. <ja>存在しない型。</ja></exception>
    InspectorValue Decode(IDocument document, OffsetArg offset, string typeId, InspectorOptions? options = null);

    /// <summary>Encodes a value text as the inspector does when editing. <ja>値の文字列からバイト列を作る (インスペクタで書き換える操作と同じ解釈)。</ja></summary>
    byte[] Encode(string typeId, string valueText, InspectorOptions? options = null);

    /// <summary>Shows a type in the inspector panel. <ja>インスペクタパネルに型を表示する。</ja></summary>
    void Show(string typeId);

    /// <summary>Hides a type in the inspector panel. <ja>インスペクタパネルの型を隠す。</ja></summary>
    void Hide(string typeId);
}

/// <summary>Text encoding conversion (AUTO-12). <ja>文字コードの変換 (AUTO-12)。</ja></summary>
public interface IText
{
    /// <summary>Encodes a string. <ja>文字列をバイト列にする。</ja></summary>
    /// <exception cref="ArgumentError">Unknown encoding. <ja>存在しない文字コード。</ja></exception>
    byte[] Encode(string text, EncodingArg encoding);

    /// <summary>Decodes bytes. <ja>バイト列を文字列にする。</ja></summary>
    string Decode(BytePattern bytes, EncodingArg encoding, DecodeOptions? options = null);

    /// <summary>All encodings the application supports. <ja>使える文字コードの一覧。</ja></summary>
    IReadOnlyList<EncodingInfo> Encodings { get; }
}

/// <summary>Structure templates (AUTO-13). <ja>構造体テンプレート (AUTO-13)。</ja></summary>
public interface ITemplates
{
    /// <summary>The available templates. <ja>使えるテンプレートの一覧。</ja></summary>
    IReadOnlyList<TemplateInfo> List();

    /// <summary>Templates that match the document by the same rules as automatic application. <ja>自動適用と同じ判定で、合うテンプレートの候補を返す。</ja></summary>
    IReadOnlyList<TemplateInfo> Detect(IDocument document);

    /// <summary>
    /// Registers a function that templates can call (template extension, TPL-19). The script stays resident while it is registered;
    /// <see cref="IEventSubscription.Off"/> or the end of the script removes it.
    /// <ja>テンプレートから呼べる関数 (テンプレート拡張。TPL-19) を登録する。登録している間スクリプトは常駐し、
    /// <see cref="IEventSubscription.Off"/> かスクリプトの終了で登録を外す。</ja>
    /// </summary>
    IEventSubscription RegisterExtension(string name, [TsType("(...args: any[]) => any")] Func<IReadOnlyList<object?>, object?> fn,
        TemplateExtensionOptions? options = null);
}

/// <summary>One node of a template result tree (AUTO-13). <ja>テンプレートの解析結果のノード (AUTO-13)。</ja></summary>
public interface ITemplateNode
{
    /// <summary>Name. <ja>名前。</ja></summary>
    string Name { get; }

    /// <summary>Type name. <ja>型の名前。</ja></summary>
    string Type { get; }

    /// <summary>Start offset. <ja>開始オフセット。</ja></summary>
    long Start { get; }

    /// <summary>Length. <ja>長さ。</ja></summary>
    long Length { get; }

    /// <summary>
    /// The value (number, string ...). Setting it writes the document as editing in the template tree does (undoable).
    /// <ja>値 (数値・文字列など)。代入するとテンプレートのツリーから編集した場合と同じくドキュメントを書き換える (Undo できる)。</ja>
    /// </summary>
    [TsType("unknown")]
    object? Value { get; set; }

    /// <summary>The value formatted by the template. <ja>テンプレートの書式を適用した値の文字列。</ja></summary>
    string DisplayValue { get; }

    /// <summary>The child nodes. <ja>子のノード。</ja></summary>
    IReadOnlyList<ITemplateNode> Children { get; }

    /// <summary>Finds a descendant by a dotted path (for example <c>"header.e_lfanew"</c>), or null. <ja>ドット区切りのパスで子孫を探す (例: <c>"header.e_lfanew"</c>)。なければ null。</ja></summary>
    ITemplateNode? Get(string path);

    /// <summary>The node as JSON (the same form as the command line <c>template</c> output). <ja>JSON にした内容 (コマンドラインの <c>template</c> の出力と同じ形)。</ja></summary>
    [ScriptName("toJSON")]
    [TsType("unknown")]
    JsonNode ToJson();
}

/// <summary>Hashes and checksums (AUTO-14). <ja>ハッシュとチェックサム (AUTO-14)。</ja></summary>
public interface IHash
{
    /// <summary>The available algorithms. <ja>使えるアルゴリズムの一覧。</ja></summary>
    IReadOnlyList<HashAlgorithmInfo> Algorithms { get; }

    /// <summary>
    /// Computes one or more hashes over a range of a document, reading the data once.
    /// <ja>ドキュメントの範囲のハッシュを計算する。複数のアルゴリズムを指定するとデータを 1 回だけ読んで同時に計算する。</ja>
    /// </summary>
    /// <exception cref="ArgumentError">Unknown algorithm. <ja>存在しないアルゴリズム。</ja></exception>
    Task<IReadOnlyList<HashResult>> ComputeAsync(IDocument document, RangeArg? range, IReadOnlyList<HashAlgorithmSpec> algorithms, AsyncOptions? options = null);

    /// <summary>Hash of bytes (synchronous, up to 16 MB). <ja>バイト列のハッシュ (同期、16 MB まで)。</ja></summary>
    HashResult ComputeBytes(BytePattern data, HashAlgorithmSpec algorithm);

    /// <summary>
    /// Hashes a file without opening it (permission <c>file-read</c> or a path the user picked).
    /// <ja>ファイルを開かずに計算する (許可 <c>file-read</c> または利用者が選んだパス)。</ja>
    /// </summary>
    [RequiresPermission("file-read")]
    Task<IReadOnlyList<HashResult>> ComputeFileAsync(string path, IReadOnlyList<HashAlgorithmSpec> algorithms, AsyncOptions? options = null);
}

/// <summary>The clipboard (AUTO-15). <ja>クリップボード (AUTO-15)。</ja></summary>
public interface IClipboard
{
    /// <summary>Puts text. <ja>文字列を置く。</ja></summary>
    void SetText(string text);

    /// <summary>Puts bytes (binary format and Hex text). <ja>バイト列を置く (バイナリ形式と Hex 文字列の両方)。</ja></summary>
    void SetBytes(BytePattern data);

    /// <summary>Reads text, or null. <ja>文字列を読む。なければ null。</ja></summary>
    [RequiresPermission("clipboard-read")]
    string? GetText();

    /// <summary>Reads bytes, or null. <ja>バイト列を読む。なければ null。</ja></summary>
    [RequiresPermission("clipboard-read")]
    byte[]? GetBytes();

    /// <summary>Copies a range in a Copy As format (for example <c>hex</c>, <c>c-array</c>, <c>base64</c>). <ja>Copy As と同じ形式で書き出す (例: <c>hex</c>、<c>c-array</c>、<c>base64</c>)。</ja></summary>
    void CopyAs(IDocument document, RangeArg range, string formatId, CopyAsOptions? options = null);

    /// <summary>Returns the Copy As text without using the clipboard. <ja>Copy As と同じ形式の文字列を、クリップボードに置かずに返す。</ja></summary>
    string FormatAs(IDocument document, RangeArg range, string formatId, CopyAsOptions? options = null);
}

/// <summary>Dialogs, progress and output (AUTO-16). <ja>ダイアログ・進捗・出力 (AUTO-16)。</ja></summary>
public interface IUi
{
    /// <summary>Shows a message (InfoBar by default, a dialog with <c>modal</c>). <ja>メッセージを表示する (既定は InfoBar、<c>modal</c> ならダイアログ)。</ja></summary>
    Task MessageAsync(string text, MessageOptions? options = null);

    /// <summary>Asks the user to choose a button; returns its index, or -1 for Esc. <ja>ボタンを選ばせ、その添字を返す。Esc は -1。</ja></summary>
    Task<int> ConfirmAsync(string text, IReadOnlyList<string> buttons);

    /// <summary>Asks for one value; null when canceled. <ja>1 つの値を入力させる。キャンセルは null。</ja></summary>
    [TsType("Promise<unknown>")]
    Task<object?> PromptAsync(string label, PromptOptions? options = null);

    /// <summary>Shows a form; returns the values by field name, or null when canceled. <ja>複数の項目の入力ダイアログ。項目名ごとの値のオブジェクトを返す。キャンセルは null。</ja></summary>
    [TsType("Promise<Record<string, unknown> | null>")]
    Task<IReadOnlyDictionary<string, object?>?> FormAsync(IReadOnlyList<FormField> fields);

    /// <summary>Lets the user pick a file to read; the path is readable during this run without permission. <ja>読むファイルを選ばせる。選んだパスはこの実行中に限り許可なしで読める。</ja></summary>
    Task<string?> PickFileAsync(PickOptions? options = null);

    /// <summary>Lets the user pick files to read. <ja>読むファイルを複数選ばせる。</ja></summary>
    Task<IReadOnlyList<string>?> PickFilesAsync(PickOptions? options = null);

    /// <summary>Lets the user pick a file to write. <ja>書き込むファイルを選ばせる。</ja></summary>
    Task<string?> SaveFileAsync(PickOptions? options = null);

    /// <summary>Lets the user pick a folder. <ja>フォルダを選ばせる。</ja></summary>
    Task<string?> PickFolderAsync(PickOptions? options = null);

    /// <summary>Progress in the operation center. <ja>処理センターの進捗。</ja></summary>
    IUiProgress Progress { get; }

    /// <summary>Shows a text in the status bar for 5 seconds. <ja>ステータスバーに 5 秒間文字列を表示する。</ja></summary>
    void Status(string text);

    /// <summary>
    /// Shows rows in the common results panel. Rows with <c>start</c> and <c>length</c> jump to the offset when clicked.
    /// <ja>共通の結果一覧パネルに出す。<c>start</c> と <c>length</c> のある行はクリックでジャンプできる。</ja>
    /// </summary>
    void ShowResults(string title, [TsType("Record<string, unknown>[]")] IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<string>? columns = null);

    /// <summary>Writes a line to the output panel. <paramref name="kind"/>: <c>info</c>, <c>warning</c>, <c>error</c>. <ja>出力パネルに 1 行書く。<paramref name="kind"/>: <c>info</c>・<c>warning</c>・<c>error</c>。</ja></summary>
    void Output(string text, string? kind = null);
}

/// <summary>Operation center progress of the running script. <ja>実行中のスクリプトの処理センターの進捗。</ja></summary>
public interface IUiProgress
{
    /// <summary>Reports the fraction done (0-1) and a description. <ja>終わった割合 (0〜1) と説明を報告する。</ja></summary>
    void Report(double fraction, string? text = null);
}

/// <summary>Application settings (AUTO-17). <ja>アプリの設定 (AUTO-17)。</ja></summary>
public interface ISettings
{
    /// <summary>Reads a setting (keys of the settings reference). <ja>設定値を読む (キーは設定のキー)。</ja></summary>
    /// <exception cref="ArgumentError">Unknown key. <ja>存在しないキー。</ja></exception>
    [TsType("unknown")]
    object? Get(string key);

    /// <summary>Changes a setting; takes effect at once. <ja>設定を変える。その場で反映する。</ja></summary>
    [RequiresPermission("settings")]
    void Set(string key, [TsType("unknown")] object? value);
}

/// <summary>The running script (AUTO-17). <ja>実行中のスクリプト (AUTO-17)。</ja></summary>
public interface IScript
{
    /// <summary>Display name. <ja>表示名。</ja></summary>
    string Name { get; }

    /// <summary>File path, or null (console, unsaved editor). <ja>ファイルのパス。コンソール・未保存のエディタでは null。</ja></summary>
    string? Path { get; }

    /// <summary>Parameter values (AUTO-26) and, for hooks, <c>event</c>. <ja>パラメータの値 (AUTO-26)。フックでは <c>event</c> も入る。</ja></summary>
    [TsType("Record<string, unknown>")]
    IReadOnlyDictionary<string, object?> Args { get; }

    /// <summary>Canceled when the user stops the script. <ja>利用者がスクリプトを止めるとキャンセル状態になる。</ja></summary>
    [TsType("AbortSignal")]
    CancellationToken Signal { get; }

    /// <summary>Persistent storage of this script (1 MB). <ja>このスクリプトの永続的な保存領域 (1 MB)。</ja></summary>
    IScriptStorage Storage { get; }
}

/// <summary>Persistent key-value storage of one script (AUTO-17). <ja>スクリプトごとの永続的な保存領域 (AUTO-17)。</ja></summary>
public interface IScriptStorage
{
    /// <summary>Reads a value, or null. <ja>値を読む。なければ null。</ja></summary>
    [TsType("unknown")]
    object? Get(string key);

    /// <summary>Stores a JSON-compatible value. <ja>JSON にできる値を保存する。</ja></summary>
    /// <exception cref="LimitError">The storage would exceed 1 MB. <ja>1 MB を超える。</ja></exception>
    void Set(string key, [TsType("unknown")] object? value);

    /// <summary>Removes a value. <ja>値を削除する。</ja></summary>
    void Remove(string key);
}

/// <summary>
/// Files (AUTO-18). Needs <c>file-read</c> / <c>file-write</c>, except for paths the user picked and reading below the script folder.
/// <ja>ファイル (AUTO-18)。許可 <c>file-read</c> / <c>file-write</c> が必要。利用者が選んだパスと、スクリプトのフォルダ以下の読み取りは許可なしで使える。</ja>
/// </summary>
public interface IFileSystem
{
    /// <summary>Reads up to 16 MB of a file. <ja>ファイルを読む (1 回 16 MB まで)。</ja></summary>
    [RequiresPermission("file-read")]
    byte[] ReadBytes(string path, long offset = 0, long length = 16 * 1024 * 1024);

    /// <summary>Reads a text file. <ja>テキストファイルを読む。</ja></summary>
    [RequiresPermission("file-read")]
    string ReadText(string path, EncodingArg? encoding = null);

    /// <summary>Writes a file. <ja>ファイルに書く。</ja></summary>
    [RequiresPermission("file-write")]
    void WriteBytes(string path, BytePattern data);

    /// <summary>Writes a text file. <ja>テキストファイルに書く。</ja></summary>
    [RequiresPermission("file-write")]
    void WriteText(string path, string text, EncodingArg? encoding = null);

    /// <summary>Lists the entries of a folder. <ja>フォルダの中を一覧する。</ja></summary>
    [RequiresPermission("file-read")]
    IReadOnlyList<string> List(string folder, string? pattern = null);

    /// <summary>True when the file or folder exists. <ja>ファイル・フォルダがあるか。</ja></summary>
    [RequiresPermission("file-read")]
    bool Exists(string path);

    /// <summary>File information. <ja>ファイルの情報。</ja></summary>
    [RequiresPermission("file-read")]
    FileStat Stat(string path);

    /// <summary>Deletes a file. <ja>ファイルを削除する。</ja></summary>
    [RequiresPermission("file-write")]
    void Delete(string path);

    /// <summary>Creates a folder. <ja>フォルダを作る。</ja></summary>
    [RequiresPermission("file-write")]
    void CreateFolder(string path);
}

/// <summary>Network access of scripts (AUTO-18, permission <c>network</c>). <ja>スクリプトの通信 (AUTO-18、許可 <c>network</c>)。</ja></summary>
public interface INet
{
    /// <summary>HTTP(S) GET or POST. The response body is limited to 100 MB. <ja>HTTPS / HTTP の GET・POST。応答の本文は 100 MB まで。</ja></summary>
    [RequiresPermission("network")]
    Task<FetchResponse> FetchAsync(string url, FetchOptions? options = null);
}

/// <summary>External programs (AUTO-18, permission <c>process</c>). <ja>外部プログラム (AUTO-18、許可 <c>process</c>)。</ja></summary>
public interface IProcessRunner
{
    /// <summary>
    /// Runs a program without a shell; each element of <paramref name="args"/> is passed as one argument.
    /// <ja>シェルを経由せずにプログラムを実行する。<paramref name="args"/> の各要素がそのまま 1 つの引数になる。</ja>
    /// </summary>
    [RequiresPermission("process")]
    Task<ProcessResult> RunAsync(string path, IReadOnlyList<string> args, ProcessOptions? options = null);
}
