using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.View;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting;

/// <summary>
/// UI スレッドへの受け渡し (AUTO-05 の仕様 7)。ドキュメントの変更・ビューの状態・ダイアログはすべて UI スレッドで行う。
/// ヘッドレス実行とテストでは、1 本のスレッドに順に流す実装 (<see cref="SerialDispatcher"/>) を使う。
/// </summary>
public interface IScriptDispatcher
{
    /// <summary>今のスレッドが UI スレッドか。</summary>
    bool CheckAccess();

    /// <summary>UI スレッドで実行して結果を返す。</summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}

/// <summary>受け渡しの補助。</summary>
public static class ScriptDispatcherExtensions
{
    public static Task<T> InvokeAsync<T>(this IScriptDispatcher d, Func<T> work) =>
        d.CheckAccess() ? Task.FromResult(work()) : d.InvokeAsync(() => Task.FromResult(work()));

    public static Task InvokeAsync(this IScriptDispatcher d, Action work) =>
        d.InvokeAsync(() =>
        {
            work();
            return true;
        });

    /// <summary>
    /// スクリプトのスレッドから UI スレッドの処理を同期で呼ぶ (同期 API 用)。UI スレッドから呼んだ場合はその場で実行する。
    /// UI スレッドはスクリプトを同期で待たないので、待ち合わせで止まらない。
    /// </summary>
    public static T Invoke<T>(this IScriptDispatcher d, Func<T> work) =>
        d.CheckAccess() ? work() : d.InvokeAsync(() => Task.FromResult(work())).GetAwaiter().GetResult();

    public static void Invoke(this IScriptDispatcher d, Action work) =>
        d.Invoke(() =>
        {
            work();
            return true;
        });
}

/// <summary>
/// スクリプトから見える 1 つのドキュメント (タブ)。App ではドキュメントのタブ (DocumentViewModel)、ヘッドレス実行では Core の
/// ドキュメントとビューの状態を包む。<see cref="Document"/> と <see cref="Editor"/> は UI スレッドで扱う。
/// </summary>
public interface IScriptDocumentHost
{
    Document Document { get; }

    /// <summary>主のビュー (カーソル・選択範囲・表示位置)。</summary>
    EditorState Editor { get; }

    /// <summary>パス。新規ドキュメントでは null。</summary>
    string? Path { get; }

    string Name { get; }

    /// <summary><c>file</c> / <c>disk</c> / <c>volume</c> / <c>process</c> / <c>memory</c>。</summary>
    string Kind { get; }

    /// <summary>閉じられたか (AUTO-07 の仕様 2)。</summary>
    bool IsClosed { get; }

    /// <summary>通常の保存と同じ安全な方式で保存する (01)。失敗は例外。</summary>
    Task SaveAsync(CancellationToken cancellationToken);

    Task SaveAsAsync(string path, CancellationToken cancellationToken);

    /// <summary>閉じる。<paramref name="discard"/> が false で未保存の変更があれば利用者に確認する。閉じたら true。</summary>
    Task<bool> CloseAsync(bool discard);

    /// <summary>範囲を別のタブで開く (読み取り専用のビュー)。</summary>
    Task<IScriptDocumentHost> OpenRangeAsync(long start, long length);

    /// <summary>
    /// スクリプトが書き込み中であることを示す (AUTO-01 の仕様 6): エディタ上部の InfoBar「スクリプト「名前」を実行中です」と停止ボタン。
    /// <paramref name="run"/> が null なら消す。
    /// </summary>
    void ShowScriptWriting(ScriptRun? run);
}

/// <summary>ダイアログの結果 (信頼の確認。AUTO-34 の仕様 2)。</summary>
public enum TrustAnswer
{
    RunOnce,
    AlwaysAllow,
    Deny,
}

/// <summary>信頼の確認ダイアログに出す内容 (AUTO-34 の仕様 2・8)。</summary>
/// <param name="FullTrust">C# スクリプト・プラグイン (完全信頼のコード) なら true。</param>
/// <param name="FromInternet">Mark of the Web が付いている。</param>
/// <param name="Elevated">アプリを管理者として実行中 (仕様 8 の警告を加える)。</param>
/// <param name="ElevationOnly">信頼済みで、管理者の確認だけを出す (ボタンは「実行する」「実行しない」)。</param>
public sealed record TrustPrompt(string Name, string? Path, string? Author, ScriptLanguage Language, IReadOnlyList<string> Permissions,
    bool FullTrust, bool FromInternet, bool Elevated, bool ElevationOnly);

/// <summary>パラメータの入力ダイアログの項目 (AUTO-26 の仕様 3)。</summary>
public sealed record ScriptFormField(string Name, string Label, string Type, object? Default, IReadOnlyList<string>? Choices);

/// <summary>
/// スクリプトの UI (AUTO-16、AUTO-34 の確認、AUTO-02 の時間の上限の確認、AUTO-03 のコンポーネントの案内)。App はアプリの共通部品で
/// 表示する。すべて UI スレッドで呼ばれる。<paramref name="scriptName"/> はダイアログのタイトルに付ける (AUTO-16 の仕様 3)。
/// </summary>
public interface IScriptUiService
{
    Task MessageAsync(string scriptName, string text, string kind, bool modal);

    /// <summary>選んだボタンの添字。Esc は -1。</summary>
    Task<int> ConfirmAsync(string scriptName, string text, IReadOnlyList<string> buttons);

    /// <summary>入力ダイアログ。値は項目名ごとの入力の文字列 (checkbox は "true" / "false")。キャンセルは null。</summary>
    /// <param name="validate">入力の検査 (誤りのある項目名と説明。なければ null)。誤りがある間は確定できない。</param>
    Task<IReadOnlyDictionary<string, string>?> FormAsync(string scriptName, string? title, IReadOnlyList<ScriptFormField> fields,
        Func<IReadOnlyDictionary<string, string>, (string Field, string Message)?> validate);

    /// <summary>ファイル・フォルダの選択。<paramref name="kind"/>: <c>open</c> / <c>openMany</c> / <c>save</c> / <c>folder</c>。キャンセルは null。</summary>
    Task<IReadOnlyList<string>?> PickAsync(string scriptName, string kind, PickOptions options);

    void Status(string text);

    void ShowResults(string scriptName, string title, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<string> columns,
        IScriptDocumentHost? document);

    /// <summary>信頼の確認 (AUTO-34)。</summary>
    Task<TrustAnswer> ConfirmTrustAsync(TrustPrompt prompt);

    /// <summary>宣言していない許可が必要になったときの確認 (AUTO-34 の仕様 4)。許可したら true。</summary>
    Task<bool> ConfirmPermissionAsync(string scriptName, string permission, string detail);

    /// <summary>実行時間の上限に達したとき (AUTO-02 の仕様 5)。「続ける」なら true。</summary>
    Task<bool> ConfirmContinueAfterTimeoutAsync(ScriptRun run, TimeSpan limit);

    /// <summary>C# スクリプト コンポーネントがないときの案内 (AUTO-03 の仕様 8)。「追加する」なら true。</summary>
    Task<bool> OfferCSharpComponentAsync();
}

/// <summary>コマンドの実行 (AUTO-06)。</summary>
public interface IScriptCommandService
{
    IReadOnlyList<CommandInfo> List();

    bool Exists(string id);

    /// <summary>コマンドを実行する。<paramref name="headless"/> では確認を求めるコマンドは安全側の答えを選ぶ (仕様 3)。</summary>
    Task<object?> ExecuteAsync(string id, IReadOnlyDictionary<string, object?> args, IScriptDocumentHost? target, bool headless);
}

/// <summary>クリップボード (AUTO-15)。App はシステムのクリップボード、テストはアプリ内の代わりのもの。</summary>
public interface IScriptClipboard
{
    void SetText(string text);

    /// <summary>バイナリ形式と Hex 文字列の両方を置く (03)。</summary>
    void SetBytes(byte[] data, string hexText);

    string? GetText();

    byte[]? GetBytes();
}

/// <summary>アプリの設定 (AUTO-17)。キーは 09 の設定のキー。</summary>
public interface IScriptSettings
{
    bool Exists(string key);

    JsonNode? Get(string key);

    /// <summary>設定画面と同じく、その場で反映する。値の型・範囲の誤りは <see cref="Sdk.ArgumentError"/>。</summary>
    void Set(string key, JsonNode? value);
}

/// <summary>インスペクタパネル (AUTO-12 の仕様 4)。</summary>
public interface IScriptInspectorPanel
{
    /// <summary>パネルの既定のエンディアン (省略時の解釈)。</summary>
    bool BigEndian { get; }

    Inspector.InspectorOptions Options { get; }

    void SetVisible(string typeId, bool visible);
}

/// <summary>HTTP (AUTO-18 の <c>net.fetch</c>)。テストは偽物を使い、実際には通信しない。</summary>
public interface IScriptHttp
{
    Task<(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)> SendAsync(string method, Uri url,
        IReadOnlyDictionary<string, string> headers, byte[]? body, long maxResponseBytes, CancellationToken cancellationToken);
}

/// <summary>外部プログラムの実行 (AUTO-18 の <c>process.run</c>)。シェルを経由せず、引数は配列のまま渡す。</summary>
public interface IScriptProcessLauncher
{
    Task<ProcessResult> RunAsync(string path, IReadOnlyList<string> args, string? workingDirectory, string? input, TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// アプリ側の環境 (App が実装する。ヘッドレス実行・テストは <see cref="CoreScriptEnvironment"/>)。
/// スクリプトの API (Core.Scripting.Api) はこれを通してアプリのドキュメント・UI・設定を使う。
/// </summary>
public interface IScriptEnvironment
{
    IScriptDispatcher Dispatcher { get; }

    string AppVersion { get; }

    /// <summary>UI の表示言語のタグ。</summary>
    string Language { get; }

    /// <summary>アプリ全体を管理者として実行中 (AUTO-34 の仕様 8)。</summary>
    bool IsElevated { get; }

    /// <summary>開いているドキュメント (すべてのウィンドウ)。UI スレッドで読む。</summary>
    IReadOnlyList<IScriptDocumentHost> Documents { get; }

    /// <summary>アクティブなドキュメント。UI スレッドで読み書きする。</summary>
    IScriptDocumentHost? ActiveDocument { get; set; }

    Task<IScriptDocumentHost> OpenFileAsync(string path, bool readOnly, bool background);

    Task<IScriptDocumentHost> OpenDiskAsync(string id, bool readOnly, bool background);

    Task<IScriptDocumentHost> OpenProcessAsync(int pid, bool readOnly, bool background);

    IScriptDocumentHost NewDocument(long length, byte[] fill);

    IScriptCommandService Commands { get; }

    IScriptUiService Ui { get; }

    IScriptClipboard Clipboard { get; }

    IScriptSettings Settings { get; }

    IScriptInspectorPanel Inspector { get; }

    /// <summary>テンプレートエンジン (07。テンプレートの領域が実装する。なければ null)。</summary>
    IScriptTemplateEngine? Templates { get; }

    IScriptHttp Http { get; }

    IScriptProcessLauncher Processes { get; }

    /// <summary>出力パネルへの出力 (AUTO-24)。</summary>
    ScriptOutputLog Output { get; }

    /// <summary>処理センター (ドキュメントのウィンドウのもの。ドキュメントがなければアクティブなウィンドウのもの)。</summary>
    OperationCenter OperationsFor(IScriptDocumentHost? document);

    /// <summary>Copy As の設定 (03)。</summary>
    Clipboard.CopyOptions CopyOptions { get; }

    /// <summary>イベント (AUTO-06 の仕様 4)。</summary>
    IScriptEventSource Events { get; }

    /// <summary>実行中のスクリプトの数が変わった (ステータスバーの表示。AUTO-01 の画面)。</summary>
    void OnRunningScriptsChanged(int count);

    /// <summary>
    /// 表示言語の文字列 (処理センターの項目名など、Core が作る UI の文字列)。キーは <see cref="ScriptTexts"/> の定数で、
    /// App はリソースの同名のキーで表示する。<paramref name="args"/> は <c>{0}</c> などに入れる。
    /// </summary>
    string Localize(string key, params object[] args);
}

/// <summary>Core が UI に出す文字列のキー (App ではリソースのキー。<see cref="IScriptEnvironment.Localize"/>)。</summary>
public static class ScriptTexts
{
    /// <summary>処理センターの項目「スクリプト: {0}」(AUTO-01 の仕様 6)。</summary>
    public const string Operation = "Script_Operation";

    /// <summary>一括処理の項目「スクリプト: {0} - {1}」(AUTO-09 の画面)。</summary>
    public const string BulkOperation = "Script_BulkOperation";

    /// <summary>書き込み中のドキュメントの処理センターの項目「スクリプト: {0} (書き込み中)」。</summary>
    public const string WriteOperation = "Script_WriteOperation";

    /// <summary>Undo の単位の名前「スクリプト: {0}」(AUTO-01 の仕様 7)。</summary>
    public const string UndoName = "Script_UndoName";

    /// <summary>既定の (英語・日本語の) 文字列。ヘッドレス実行とテストで使う。</summary>
    public static string Default(string key, string language, params object[] args)
    {
        bool ja = language.StartsWith("ja", StringComparison.OrdinalIgnoreCase);
        string format = key switch
        {
            Operation or UndoName => ja ? "スクリプト: {0}" : "Script: {0}",
            BulkOperation => ja ? "スクリプト: {0} - {1}" : "Script: {0} - {1}",
            WriteOperation => ja ? "スクリプト: {0} (書き込み中)" : "Script: {0} (writing)",
            _ => key,
        };
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args);
    }
}

/// <summary>保存の直前のイベントの引数 (AUTO-06 の <c>beforeSave</c>)。ハンドラは <see cref="Cancel"/> で保存を中止できる。</summary>
public sealed class BeforeSaveEventArgs(IScriptDocumentHost document) : EventArgs
{
    private readonly List<Task> _deferrals = [];

    public IScriptDocumentHost Document { get; } = document;

    /// <summary>中止の理由 (中止されていなければ null)。</summary>
    public string? CancelReason { get; private set; }

    public void Cancel(string reason) => CancelReason ??= reason;

    /// <summary>非同期のハンドラ (スクリプト) の完了を待たせる。保存する側は <see cref="WaitAsync"/> で待つ。</summary>
    public void Defer(Task task)
    {
        lock (_deferrals)
        {
            _deferrals.Add(task);
        }
    }

    public async Task WaitAsync(TimeSpan timeout)
    {
        Task[] tasks;
        lock (_deferrals)
        {
            tasks = [.. _deferrals];
        }

        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(timeout)).ConfigureAwait(false);
    }
}

/// <summary>アプリのイベント (AUTO-06 の仕様 4)。App はタブ・保存の処理から発生させる。UI スレッドで発生する。</summary>
public interface IScriptEventSource
{
    event EventHandler<IScriptDocumentHost>? DocumentOpened;

    event EventHandler<IScriptDocumentHost>? DocumentClosed;

    event EventHandler? ActiveDocumentChanged;

    event EventHandler<BeforeSaveEventArgs>? BeforeSave;

    event EventHandler<IScriptDocumentHost>? AfterSave;

    /// <summary>選択範囲・カーソルが変わった (間引きはスクリプト側で行う)。</summary>
    event EventHandler<IScriptDocumentHost>? SelectionChanged;
}

/// <summary>イベントをまとめて発生させる実装 (App とヘッドレス実行で使う)。</summary>
public sealed class ScriptEventSource : IScriptEventSource
{
    public event EventHandler<IScriptDocumentHost>? DocumentOpened;

    public event EventHandler<IScriptDocumentHost>? DocumentClosed;

    public event EventHandler? ActiveDocumentChanged;

    public event EventHandler<BeforeSaveEventArgs>? BeforeSave;

    public event EventHandler<IScriptDocumentHost>? AfterSave;

    public event EventHandler<IScriptDocumentHost>? SelectionChanged;

    public void RaiseDocumentOpened(IScriptDocumentHost doc) => DocumentOpened?.Invoke(this, doc);

    public void RaiseDocumentClosed(IScriptDocumentHost doc) => DocumentClosed?.Invoke(this, doc);

    public void RaiseActiveDocumentChanged() => ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>保存の直前に呼ぶ。スクリプトのハンドラの完了を待ち (最大 <paramref name="timeout"/>)、中止の理由を返す。</summary>
    public async Task<string?> RaiseBeforeSaveAsync(IScriptDocumentHost doc, TimeSpan timeout)
    {
        var args = new BeforeSaveEventArgs(doc);
        BeforeSave?.Invoke(this, args);
        await args.WaitAsync(timeout).ConfigureAwait(true);
        return args.CancelReason;
    }

    public void RaiseAfterSave(IScriptDocumentHost doc) => AfterSave?.Invoke(this, doc);

    public void RaiseSelectionChanged(IScriptDocumentHost doc) => SelectionChanged?.Invoke(this, doc);
}
