using HexEditor.Core.Templates.Extensions;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Runtime;

namespace HexEditor.Core.Templates;

/// <summary>適用の種類 (TPL-26)。</summary>
public enum TemplateApplyKind
{
    Fixed,
    Follow,
}

/// <summary>適用の設定 (TPL-20 の仕様 2)。</summary>
public sealed record TemplateApplyOptions
{
    /// <summary>開始オフセット (<c>$</c> の初期値、<c>std::core::base_offset()</c>。TPL-09 の仕様 5)。</summary>
    public long StartOffset { get; init; }

    /// <summary>既定のエンディアン (適用時の指定。TPL-10 の優先順位の 6)。null はリトルエンディアン。</summary>
    public bool? BigEndian { get; init; }

    /// <summary>文字の既定の文字コード (ドキュメントの文字コード)。</summary>
    public string DefaultEncoding { get; init; } = "latin1";

    /// <summary>設定「テンプレートの上限の最大値」(TPL-17 の仕様 4)。</summary>
    public TemplateLimits MaximumLimits { get; init; } = TemplateLimits.DefaultMaximum;

    /// <summary>上限をさらに上書きする (「上限を上げて再実行」、追従の 200 ms・10 万パターン)。</summary>
    public TemplateLimits? LimitOverride { get; init; }

    /// <summary>自動適用での実行 (<c>limit.time</c> を 5 秒に固定する。TPL-17 の仕様 4)。</summary>
    public bool IsAutoApply { get; init; }

    /// <summary>テンプレート全体ではなく 1 つの型を評価する (追従。TPL-26 の仕様 3)。</summary>
    public string? TypeName { get; init; }

    /// <summary>拡張関数 (TPL-19)。null なら拡張関数の呼び出しはすべて「登録されていません」になる。</summary>
    public ITemplateExtensionHost? Extensions { get; init; }

    /// <summary>セクションの一時ファイルを置くフォルダ (アプリの一時フォルダ)。</summary>
    public string TempDirectory { get; init; } = Path.Combine(Path.GetTempPath(), "HexEditor", "templates");

    /// <summary>テスト用の時計 (時間の上限の測定)。</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>評価の状態 (TPL-21 の「画面」の状態表示)。</summary>
public enum TemplateRunState
{
    Running,
    Completed,

    /// <summary>エラーで止まった (部分結果あり)。</summary>
    Failed,
    Cancelled,

    /// <summary>上限に達して止まった (部分結果あり。「上限を上げて再実行」。TPL-28 の仕様 3)。</summary>
    LimitReached,

    /// <summary>構文エラー (評価を始めない。TPL-27 の仕様 1)。</summary>
    SyntaxError,
}

/// <summary>達した上限の種類。</summary>
public enum TemplateLimitKind
{
    None,
    Steps,
    Patterns,
    Memory,
    Time,
}

/// <summary>テンプレートの出力パネルの 1 行 (TPL-27 の仕様 4)。</summary>
public sealed record TemplateOutputEntry(DateTime TimeUtc, TemplateSeverity Severity, string Text, TemplateDiagnostic? Diagnostic);

/// <summary>
/// 1 つの適用の評価結果 (TPL-20)。評価はバックグラウンドで行い、評価中も評価済みの部分を <see cref="Root"/> から読める
/// (TPL-20 の仕様 4)。ノードの子・値は必要になったときに評価する (TPL-28)。
/// </summary>
public sealed class TemplateResult : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TemplateResult(TemplateProgram program, TemplateDataSource data, TemplateApplyOptions options)
    {
        Program = program;
        Data = data;
        Options = options;
        Root = new TemplateNode(this, TemplateNodeKind.Application, program.Metadata.GetDisplayName(System.Globalization.CultureInfo.CurrentUICulture,
            program.Path), options.TypeName ?? Path.GetFileName(program.Path), options.StartOffset, 0);
        Evaluator = new Evaluator(this);
    }

    public TemplateProgram Program { get; }

    public TemplateDataSource Data { get; }

    public TemplateApplyOptions Options { get; }

    /// <summary>適用のノード (結果ツリーの最上位。TPL-21 の仕様 9)。</summary>
    public TemplateNode Root { get; }

    public TemplateRunState State { get; internal set; } = TemplateRunState.Running;

    public TemplateLimitKind LimitReached { get; internal set; }

    /// <summary>実際に使った上限 (プラグマを最大値で切り詰めたもの)。</summary>
    public TemplateLimits Limits => Evaluator.Limits;

    /// <summary>警告・エラーと <c>print</c> の出力 (時刻順。TPL-27 の仕様 4)。</summary>
    public IReadOnlyList<TemplateOutputEntry> Output
    {
        get
        {
            lock (OutputLock)
            {
                return [.. OutputList];
            }
        }
    }

    /// <summary>すべての警告・エラー (読み込み時のものを含む)。</summary>
    public IReadOnlyList<TemplateDiagnostic> Diagnostics
    {
        get
        {
            lock (OutputLock)
            {
                return [.. DiagnosticsList];
            }
        }
    }

    public int ErrorCount => Diagnostics.Count(d => d.Severity == TemplateSeverity.Error);

    public int WarningCount => Diagnostics.Count(d => d.Severity == TemplateSeverity.Warning);

    /// <summary>評価済み (実体化した) のパターンの数 (TPL-21 の「評価済み: 12,345 パターン」)。</summary>
    public long PatternCount => Interlocked.Read(ref Evaluator.PatternCounter);

    /// <summary>実行した文の数。</summary>
    public long StepCount => Interlocked.Read(ref Evaluator.StepCounter);

    /// <summary>評価に使っているメモリの見積もり (結果ツリーとローカル変数。TC-TPL-28-02)。</summary>
    public long MemoryBytes => Interlocked.Read(ref Evaluator.MemoryCounter);

    /// <summary>評価中の位置 (進捗。TPL-28 の仕様 5)。</summary>
    public long CurrentOffset => Interlocked.Read(ref Evaluator.ProgressOffset);

    public long DataLength => Data.Length;

    public IReadOnlyList<TemplateSection> Sections
    {
        get
        {
            lock (SyncRoot)
            {
                return [.. Evaluator.Sections];
            }
        }
    }

    /// <summary>評価が終わる (完了・エラー・キャンセル・上限) と完了するタスク。</summary>
    public Task Completion => _completion.Task;

    public TimeSpan Elapsed { get; internal set; }

    /// <summary>テスト用の計数: 構造体の型ごとの評価の回数 (TC-TPL-08-01、TC-TPL-33-02)。</summary>
    public IReadOnlyDictionary<string, long> EvaluationCounts
    {
        get
        {
            lock (SyncRoot)
            {
                return new Dictionary<string, long>(Evaluator.TypeEvaluations);
            }
        }
    }

    /// <summary>評価を止める (要求から 100 ms 以内に止まる。TPL-28 の仕様 4)。部分結果は残る。</summary>
    public void Cancel() => _cts.Cancel();

    /// <summary>パスでノードを探す (<c>chunks[3].header.width</c>。TPL-21 の仕様 6)。途中の未評価の部分は評価する。</summary>
    public TemplateNode? FindByPath(string path) => Run(() => Evaluator.FindByPath(path));

    /// <summary>
    /// 部分木をすべて評価する (エクスポート、「すべて評価して検索」。TPL-21 の仕様 5、TPL-25 の仕様 5)。長時間処理。
    /// </summary>
    public void EvaluateAll(TemplateNode? node = null, CancellationToken cancellationToken = default) =>
        Run(() =>
        {
            Evaluator.EvaluateAll(node ?? Root, cancellationToken);
            return 0;
        });

    /// <summary>評価器のスレッドで <paramref name="body"/> を実行する (UI スレッドから子・値を読むときに使う)。</summary>
    public Task<T> InvokeAsync<T>(Func<T> body) => TemplateThreads.RunAsync(() => Run(body));

    public T Invoke<T>(Func<T> body) => Run(body);

    public void Dispose()
    {
        Cancel();
        foreach (TemplateSection s in Sections)
        {
            s.Dispose();
        }
    }

    // ───────────── 内部 ─────────────

    internal object SyncRoot { get; } = new();

    internal object OutputLock { get; } = new();

    internal List<TemplateOutputEntry> OutputList { get; } = [];

    internal List<TemplateDiagnostic> DiagnosticsList { get; } = [];

    internal Evaluator Evaluator { get; }

    internal CancellationToken CancellationToken => _cts.Token;

    /// <summary>評価器の状態を読み書きする処理を、評価器のスレッドで、評価を止めた安全な位置で実行する。</summary>
    internal T Run<T>(Func<T> body)
    {
        if (TemplateThreads.IsCurrent && Monitor.IsEntered(SyncRoot))
        {
            return body();
        }

        return TemplateThreads.Run(() =>
        {
            lock (SyncRoot)
            {
                Evaluator.Waiters++;
                try
                {
                    while (Evaluator.MainHoldsLock)
                    {
                        Monitor.Wait(SyncRoot);
                    }

                    return Evaluator.Guard(body);
                }
                finally
                {
                    Evaluator.Waiters--;
                    Monitor.PulseAll(SyncRoot);
                }
            }
        });
    }

    internal void Complete() => _completion.TrySetResult();

    internal void AddOutput(TemplateOutputEntry entry)
    {
        lock (OutputLock)
        {
            OutputList.Add(entry);
            if (entry.Diagnostic is not null)
            {
                DiagnosticsList.Add(entry.Diagnostic);
            }
        }
    }
}
