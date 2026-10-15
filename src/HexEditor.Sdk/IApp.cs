namespace HexEditor.Sdk;

/// <summary>
/// The whole application (AUTO-06). JavaScript: the global <c>app</c>. C#: the global <c>App</c>.
/// <ja>アプリ全体 (AUTO-06)。JavaScript ではグローバルの <c>app</c>、C# では <c>App</c>。</ja>
/// </summary>
public interface IApp
{
    /// <summary>The application version. <ja>アプリのバージョン。</ja></summary>
    string Version { get; }

    /// <summary>The automation API version (for example <c>1.0</c>). <ja>自動化 API のバージョン (例: <c>1.0</c>)。</ja></summary>
    string ApiVersion { get; }

    /// <summary>The UI language tag (for example <c>ja</c>). <ja>UI の表示言語のタグ (例: <c>ja</c>)。</ja></summary>
    string Language { get; }

    /// <summary>The open documents. <ja>開いているドキュメントの一覧。</ja></summary>
    IReadOnlyList<IDocument> Documents { get; }

    /// <summary>
    /// The active document, or null. Setting it activates that tab.
    /// <ja>アクティブなドキュメント。ない場合は null。代入するとそのタブをアクティブにする。</ja>
    /// </summary>
    IDocument? ActiveDocument { get; set; }

    /// <summary>Opens a file. Returns at once regardless of the file size. <ja>ファイルを開く。サイズに関係なくすぐに戻る。</ja></summary>
    /// <exception cref="IoError">The file cannot be opened. <ja>開けない。</ja></exception>
    Task<IDocument> OpenAsync(string path, OpenOptions? options = null);

    /// <summary>Opens a disk or volume by its ID. <ja>ディスク・ボリュームを ID で開く。</ja></summary>
    [RequiresPermission("disk")]
    Task<IDocument> OpenDiskAsync(string id, OpenOptions? options = null);

    /// <summary>Opens the memory of a process. <ja>プロセスのメモリを開く。</ja></summary>
    [RequiresPermission("disk")]
    Task<IDocument> OpenProcessAsync(int pid, OpenOptions? options = null);

    /// <summary>Creates a new document. <ja>新規ドキュメントを作る。</ja></summary>
    IDocument NewDocument(NewDocumentOptions? options = null);

    /// <summary>Runs application commands. <ja>アプリのコマンドの実行。</ja></summary>
    ICommands Commands { get; }

    /// <summary>The data inspector. <ja>データインスペクタ。</ja></summary>
    IInspector Inspector { get; }

    /// <summary>Text encoding conversion. <ja>文字コードの変換。</ja></summary>
    IText Text { get; }

    /// <summary>Structure templates. <ja>構造体テンプレート。</ja></summary>
    ITemplates Templates { get; }

    /// <summary>Hashes and checksums. <ja>ハッシュとチェックサム。</ja></summary>
    IHash Hash { get; }

    /// <summary>The clipboard. <ja>クリップボード。</ja></summary>
    IClipboard Clipboard { get; }

    /// <summary>Dialogs, progress and output. <ja>ダイアログ・進捗・出力。</ja></summary>
    IUi Ui { get; }

    /// <summary>Application settings. <ja>アプリの設定。</ja></summary>
    ISettings Settings { get; }

    /// <summary>The running script. <ja>実行中のスクリプト。</ja></summary>
    IScript Script { get; }

    /// <summary>Files (permission needed). <ja>ファイル (許可が必要)。</ja></summary>
    IFileSystem Fs { get; }

    /// <summary>Network (permission needed). <ja>ネットワーク (許可が必要)。</ja></summary>
    INet Net { get; }

    /// <summary>External programs (permission needed). <ja>外部プログラム (許可が必要)。</ja></summary>
    IProcessRunner Process { get; }

    /// <summary>
    /// Subscribes to an event: <c>documentOpened</c>, <c>documentClosed</c>, <c>activeDocumentChanged</c>, <c>beforeSave</c>,
    /// <c>afterSave</c> or <c>selectionChanged</c> (at most every 100 ms). A script with subscriptions keeps running until
    /// <see cref="Stop"/> is called or the user stops it.
    /// <ja>イベントを購読する: <c>documentOpened</c>・<c>documentClosed</c>・<c>activeDocumentChanged</c>・<c>beforeSave</c>・<c>afterSave</c>・<c>selectionChanged</c> (100 ms ごとにまとめる)。購読したスクリプトは <see cref="Stop"/> を呼ぶか停止するまで常駐する。</ja>
    /// </summary>
    [TsType("EventSubscription")]
    IEventSubscription On(string eventName, [TsType("(event: ScriptEventArgs) => void")] Action<ScriptEventArgs> handler);

    /// <summary>Ends the running script. <ja>実行中のスクリプト自身を終了する。</ja></summary>
    void Stop();

    /// <summary>
    /// Ends the script with an exit code for headless runs (0-255; use 64 or more for script-specific meanings).
    /// <ja>スクリプトを終え、ヘッドレス実行の終了コードを決める (0〜255。スクリプト独自の意味には 64 以上を使う)。</ja>
    /// </summary>
    void Exit(int code);
}

/// <summary>Runs application commands by ID (AUTO-06). <ja>アプリのコマンドを ID で実行する (AUTO-06)。</ja></summary>
public interface ICommands
{
    /// <summary>
    /// Runs the command <paramref name="id"/>. Commands that ask for confirmation take the answer in <c>args.confirm</c>; without it,
    /// interactive runs show the dialog and headless runs choose the safe answer (cancel).
    /// <ja>コマンド <paramref name="id"/> を実行する。確認を求めるコマンドは <c>args.confirm</c> で答えを渡せる。渡さない場合、対話的な実行ではダイアログを出し、ヘッドレス実行では安全側 (キャンセル) を選ぶ。</ja>
    /// </summary>
    /// <exception cref="ArgumentError">Unknown command ID. <ja>存在しないコマンド ID。</ja></exception>
    [TsType("Promise<unknown>")]
    Task<object?> ExecuteAsync(string id, [TsType("Record<string, unknown>")] IReadOnlyDictionary<string, object?>? args = null);

    /// <summary>All commands (IDs, display names, arguments). <ja>コマンドの一覧 (ID・表示名・引数)。</ja></summary>
    IReadOnlyList<CommandInfo> List();
}
