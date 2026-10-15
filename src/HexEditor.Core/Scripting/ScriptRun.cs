using HexEditor.Sdk;

namespace HexEditor.Core.Scripting;

/// <summary>スクリプトの実行がどこから来たか (AUTO-34 の仕様 1 の「確認を出さないもの」の判定に使う)。</summary>
public enum ScriptOrigin
{
    /// <summary>ファイル (スクリプト一覧・ファイル選択・コマンドライン)。</summary>
    File,

    /// <summary>アプリ内のスクリプトエディタの内容 (未保存を含む。利用者が書いたもの)。</summary>
    Editor,

    /// <summary>コンソールの入力 (AUTO-23)。</summary>
    Console,

    /// <summary>アプリに同梱のサンプル (AUTO-46)。</summary>
    BundledSample,

    /// <summary>保存していない「直前のマクロ」(AUTO-28 の仕様 7。メモリ上にだけある)。</summary>
    UnsavedMacro,

    /// <summary>フック (AUTO-25)。</summary>
    Hook,
}

/// <summary>実行の状態と結果 (AUTO-01 の仕様 10)。</summary>
public enum ScriptRunStatus
{
    Waiting,
    Running,
    Succeeded,

    /// <summary>構文エラー・実行時の例外・上限超過。</summary>
    Failed,

    /// <summary>停止ボタン・<c>script.signal</c> のキャンセルで終わった (エラーとしては扱わない。AUTO-05 の「エラー」)。</summary>
    Stopped,

    /// <summary>実行しなかった (信頼の確認で拒否、パラメータの入力をキャンセル、組織の設定で無効、API のバージョンが合わない)。</summary>
    NotRun,
}

/// <summary>終わった理由の詳細 (ヘッドレス実行の終了コード。AUTO-36 の仕様 7)。</summary>
public enum ScriptFailure
{
    None,

    /// <summary>構文エラー・コンパイルエラー・実行時の例外・API のバージョン (終了コード 4)。</summary>
    ScriptError,

    /// <summary>信頼・許可がない (終了コード 5)。</summary>
    Permission,

    /// <summary>キャンセル (終了コード 6)。</summary>
    Canceled,

    /// <summary>実行時間・メモリ・再帰の上限 (終了コード 7)。</summary>
    Limit,

    /// <summary>入出力のエラー (スクリプトを読めない、C# スクリプト コンポーネントがない。終了コード 3)。</summary>
    Io,

    /// <summary>引数の誤り (拡張子が不明。終了コード 2)。</summary>
    Argument,
}

/// <summary>スクリプトのエラーの位置 (出力パネルのリンク・コンパイルエラーの一覧。AUTO-01 の「エラー」)。</summary>
public sealed record ScriptDiagnostic(string Message, string? Path, int Line, int Column, string? Code = null, bool IsError = true);

/// <summary>実行の結果 (AUTO-01 の仕様 10)。</summary>
public sealed record ScriptRunResult(ScriptRunStatus Status, ScriptFailure Failure, string? Message, IReadOnlyList<ScriptDiagnostic> Diagnostics,
    string? StackTrace, TimeSpan Elapsed, IReadOnlyList<string> ModifiedDocuments, int? ExitCode)
{
    /// <summary>ヘッドレス実行の終了コード (AUTO-36 の仕様 7。<c>app.exit</c> の値が優先)。</summary>
    public int ProcessExitCode => ExitCode ?? Failure switch
    {
        ScriptFailure.None => 0,
        ScriptFailure.Argument => 2,
        ScriptFailure.Io => 3,
        ScriptFailure.ScriptError => 4,
        ScriptFailure.Permission => 5,
        ScriptFailure.Canceled => 6,
        ScriptFailure.Limit => 7,
        _ => 10,
    };
}

/// <summary>
/// 実行の要求 (AUTO-01)。<see cref="Code"/> を渡すとファイルの代わりにその内容を実行する (未保存のエディタ、コンソール、直前のマクロ)。
/// </summary>
public sealed record ScriptRunRequest
{
    /// <summary>スクリプトのファイル。<see cref="Code"/> がなければここから読む。</summary>
    public string? Path { get; init; }

    public string? Code { get; init; }

    /// <summary>言語。null なら拡張子で決める (AUTO-01 の仕様 1)。</summary>
    public ScriptLanguage? Language { get; init; }

    public ScriptOrigin Origin { get; init; } = ScriptOrigin.File;

    /// <summary>表示名 (メタデータの <c>@name</c> がなければこれ、なければファイル名)。</summary>
    public string? DisplayName { get; init; }

    /// <summary>ヘッドレス実行 (AUTO-41)。確認ダイアログを出さない。</summary>
    public bool Headless { get; init; }

    /// <summary>ヘッドレス実行の <c>--arg 名前=値</c> (AUTO-16 の仕様 4、AUTO-41 の仕様 3)。</summary>
    public IReadOnlyDictionary<string, string> CommandLineArgs { get; init; } = new Dictionary<string, string>();

    /// <summary>コマンドラインの <c>--trust</c> (AUTO-34 の仕様 6)。</summary>
    public bool CommandLineTrust { get; init; }

    /// <summary>コマンドラインの <c>--allow 許可,...</c>。</summary>
    public IReadOnlyCollection<string> CommandLineAllow { get; init; } = [];

    /// <summary>あらかじめ決まった引数 (フックの <c>event</c> など。AUTO-25 の仕様 3)。</summary>
    public IReadOnlyDictionary<string, object?> PresetArgs { get; init; } = new Dictionary<string, object?>();

    /// <summary>パラメータの入力ダイアログを出さず、既定値 (前回の値) と <see cref="PresetArgs"/> を使う。</summary>
    public bool SkipParameterDialog { get; init; }

    /// <summary>実行時間の上限の上書き (フックの上限時間。AUTO-25 の仕様 1)。null なら設定とメタデータで決める。</summary>
    public TimeSpan? TimeLimitOverride { get; init; }

    /// <summary>C# スクリプトをデバッグ情報付きでコンパイルする (AUTO-22)。</summary>
    public bool Debug { get; init; }

    /// <summary>
    /// 実行時間の上限に達したら、「続ける」を尋ねずに停止する (フックなど、利用者が見ていない実行。AUTO-25 の仕様 5)。
    /// </summary>
    public bool StopOnTimeLimit { get; init; }

    /// <summary>
    /// 実行する本文の変換。信頼の確認とパラメータの入力は元の本文で行い、実行の直前にこの変換を適用する
    /// (マクロの繰り返し再生。AUTO-28)。行番号を変えない変換にする。
    /// </summary>
    public Func<string, string>? CodeWrapper { get; init; }

    /// <summary>JavaScript のデバッガ (AUTO-21)。null なら通常の実行。</summary>
    public Debugging.JsDebugSession? Debugger { get; init; }

    /// <summary>
    /// 入力をまたいで変数を保持する JavaScript のセッション (コンソール。AUTO-23 の仕様 3)。null なら実行ごとに新しいエンジン。
    /// </summary>
    public Repl.JsReplSession? Repl { get; init; }

    /// <summary>実行が終わった後に残す結果の値 (コンソールの結果の表示。AUTO-23 の仕様 2)。</summary>
    public Action<Repl.ReplValue>? OnReplResult { get; init; }

    /// <summary>
    /// 実行の結果の行 (「成功 (0.12 s)」「失敗: ...」とエラーの位置) を出力パネルに書かない (コンソール。結果とエラーは
    /// コンソール自身が見せる。AUTO-23)。<c>console.log</c> などの出力は書く。
    /// </summary>
    public bool Quiet { get; init; }
}

/// <summary>
/// 実行の部品ができてから作る <c>script.args</c> の値 (フックの <c>event</c> のようにドキュメントの包みが要るもの)。
/// <see cref="ScriptRunRequest.PresetArgs"/> に入れる。
/// </summary>
public sealed class ScriptLazyArg(Func<ScriptContext, object?> create)
{
    internal object? Create(ScriptContext context) => create(context);
}

/// <summary>
/// 実行中・実行済みのスクリプト 1 本 (AUTO-01)。処理センターの項目・InfoBar の停止ボタン・スクリプト一覧の「常駐」表示はこれを見る。
/// </summary>
public sealed class ScriptRun
{
    private readonly TaskCompletionSource<ScriptRunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _lock = new();
    private ScriptRunStatus _status = ScriptRunStatus.Waiting;
    private bool _resident;
    private bool _notResponding;

    internal ScriptRun(ScriptHost host, string name, string? path, ScriptLanguage language, TimeProvider time)
    {
        Host = host;
        Name = name;
        Path = path;
        Language = language;
        StartedAt = time.GetUtcNow();
        Time = time;
    }

    internal ScriptHost Host { get; }

    internal TimeProvider Time { get; }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; internal set; }

    public string? Path { get; }

    public ScriptLanguage Language { get; }

    /// <summary>実行がどこから来たか (フックの実行では出力パネルを自動で表示しない、など)。</summary>
    public ScriptOrigin Origin { get; internal set; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>出力パネルのチャンネル (<c>script:名前</c>。パネルは「スクリプト: 名前」と表示する。AUTO-24 の仕様 1)。</summary>
    public string Channel => ChannelPrefix + Name;

    public const string ChannelPrefix = "script:";

    public ScriptRunStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>イベントを購読して常駐している (AUTO-06 の仕様 4)。</summary>
    public bool IsResident
    {
        get => Volatile.Read(ref _resident);
        internal set
        {
            Volatile.Write(ref _resident, value);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>停止を求めてから強制中断の猶予を過ぎても終わらない C# スクリプト (AUTO-03 の仕様 7)。</summary>
    public bool IsNotResponding
    {
        get => Volatile.Read(ref _notResponding);
        internal set
        {
            Volatile.Write(ref _notResponding, value);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>停止を求めた。</summary>
    public bool StopRequested => Context?.StopRequested ?? false;

    /// <summary>
    /// スクリプトの実行が終わり、結果の行 (エラーの位置・「成功」など) を書く段階に入った。ヘッドレス実行はこれ以降の情報の行を
    /// 標準出力に出さない (AUTO-41 の仕様 6: 標準出力は <c>console.log</c> だけ)。
    /// </summary>
    public bool ExecutionFinished { get; internal set; }

    public Task<ScriptRunResult> Completion => _completion.Task;

    public ScriptRunResult? Result => _completion.Task.IsCompleted ? _completion.Task.Result : null;

    /// <summary>状態・常駐・応答なしが変わった (どのスレッドからでも発生する)。</summary>
    public event EventHandler? Changed;

    internal ScriptContext? Context { get; set; }

    /// <summary>
    /// 停止する (AUTO-01 の仕様 8)。まずキャンセルを求め (<c>script.signal</c>)、猶予 (既定 3 秒) を過ぎても終わらなければ
    /// JavaScript は強制的に中断する。C# スクリプトは強制中断できないため「応答しません」の状態にする。
    /// </summary>
    public void Stop() => Context?.RequestStop();

    internal void SetStatus(ScriptRunStatus status)
    {
        lock (_lock)
        {
            _status = status;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Complete(ScriptRunResult result)
    {
        SetStatus(result.Status);
        _completion.TrySetResult(result);
    }
}
