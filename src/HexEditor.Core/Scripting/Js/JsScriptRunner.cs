using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using HexEditor.Core.Scripting.Repl;
using HexEditor.Sdk;
using Jint;
using JintEngine = Jint.Engine;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace HexEditor.Core.Scripting.Js;

/// <summary>JavaScript のスクリプトを Jint で実行する (AUTO-02)。</summary>
internal static class JsScriptRunner
{
    /// <summary>スクリプトのスレッドのスタックの大きさ (再帰の深さ 10,000 まで)。</summary>
    private const int StackSize = 256 * 1024 * 1024;

    public static Task<ScriptExecution> RunAsync(ScriptContext context, string code, ScriptLimits limits)
    {
        if (context.Request.Repl is { } repl)
        {
            return repl.ExecuteAsync(context, code, limits);
        }

        var tcs = new TaskCompletionSource<ScriptExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var session = new JsSession(context, limits);
                tcs.TrySetResult(session.Run(code));
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ScriptExecution.Error(ScriptFailure.ScriptError, ex.Message, [new ScriptDiagnostic(ex.Message, context.Path, 0, 0)], ex.ToString()));
            }
        }, StackSize)
        {
            IsBackground = true,
            Name = "Script: " + context.Name,
        };
        thread.Start();
        return tcs.Task;
    }

    internal static string Prelude { get; } = LoadPrelude();

    private static string LoadPrelude()
    {
        using Stream stream = typeof(JsScriptRunner).Assembly.GetManifestResourceStream("HexEditor.Scripting.prelude.js")
            ?? throw new InvalidOperationException(Msg.T("prelude.js がありません。", "prelude.js is missing."));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// 1 回の実行の Jint のエンジンとイベントループ (AUTO-02 の仕様 9: 実行ごとに新しいエンジン)。スクリプトはこのスレッドだけで動き、
/// 一括処理 API の完了などは <see cref="Post"/> でこのスレッドに渡す。トップレベルの Promise と購読がすべて終わったら終了する
/// (AUTO-05 の仕様 8、AUTO-06 の仕様 4)。
/// </summary>
internal sealed class JsSession : IScriptThread, IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];

    /// <summary>
    /// テンプレート拡張の呼び出し (AUTO-13 の仕様 6)。スクリプトのスレッドがテンプレートの評価を同期で待っている間
    /// (<see cref="RunPumping{T}"/>) もここだけは処理する (評価が拡張関数を呼んで止まらないように)。
    /// </summary>
    private readonly BlockingCollection<Action> _extensionQueue = [];
    private readonly ScriptLimits _limits;
    private readonly ExecutionBudget _budget;
    private Thread? _thread;
    private JintEngine _engine = null!;
    private JsBinder _binder = null!;
    private int _pending;

    public JsSession(ScriptContext context, ScriptLimits limits)
    {
        Context = context;
        _limits = limits;
        _budget = new ExecutionBudget(limits.Time, limits.MemoryBytes);
    }

    public ScriptContext Context { get; private set; }

    public bool IsCurrent => Thread.CurrentThread == _thread;

    public void BeginPending() => Interlocked.Increment(ref _pending);

    /// <summary>メモリの上限を確かめる (API の呼び出しとイベントループの区切りで呼ぶ)。</summary>
    public void CheckMemory() => _budget.CheckMemory();

    public void EndPending()
    {
        Interlocked.Decrement(ref _pending);
    }

    /// <summary>スクリプトのスレッドで実行する (どのスレッドからでも呼べる)。</summary>
    public void Post(Action action)
    {
        try
        {
            _queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // 実行が終わった後に届いたもの (無視する)。
        }
    }

    public Task PostAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex) when (!ScriptErrorMap.IsControl(ex) && ex is not RecursionDepthOverflowException)
            {
                tcs.TrySetException(ex);
            }
            catch
            {
                tcs.TrySetCanceled();
                throw;
            }
        });
        return tcs.Task;
    }

    /// <summary>テンプレート拡張の関数をスクリプトのスレッドで実行して結果を待つ (評価のスレッドから呼ぶ)。</summary>
    public T InvokeExtension<T>(Func<T> func)
    {
        if (IsCurrent)
        {
            return func();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _extensionQueue.Add(() =>
            {
                try
                {
                    tcs.TrySetResult(func());
                }
                catch (Exception ex) when (!ScriptErrorMap.IsControl(ex) && ex is not RecursionDepthOverflowException)
                {
                    tcs.TrySetException(ex);
                }
                catch
                {
                    tcs.TrySetCanceled();
                    throw;
                }
            });
        }
        catch (InvalidOperationException)
        {
            throw new CanceledError(Msg.T("スクリプトは終了しました。", "The script has ended."));
        }

        try
        {
            tcs.Task.Wait(Context.Token);
        }
        catch (OperationCanceledException)
        {
            throw new CanceledError(Msg.T("スクリプトは停止されました。", "The script was stopped."));
        }

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// スクリプトのスレッドから、ほかのスレッドの処理 (テンプレートの評価) を同期で待つ。待っている間もテンプレート拡張の呼び出しを処理する。
    /// </summary>
    public T RunPumping<T>(Func<T> work)
    {
        if (!IsCurrent)
        {
            return work();
        }

        Task<T> task = Task.Run(work);
        while (!task.IsCompleted)
        {
            if (_extensionQueue.TryTake(out Action? call, 20))
            {
                call();
            }
        }

        return task.GetAwaiter().GetResult();
    }

    public T Invoke<T>(Func<T> func)
    {
        if (IsCurrent)
        {
            return func();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex) when (!ScriptErrorMap.IsControl(ex) && ex is not RecursionDepthOverflowException)
            {
                tcs.TrySetException(ex);
            }
            catch
            {
                tcs.TrySetCanceled();
                throw;
            }
        });
        try
        {
            tcs.Task.Wait(Context.Token);
        }
        catch (OperationCanceledException)
        {
            throw new CanceledError(Msg.T("スクリプトは停止されました。", "The script was stopped."));
        }

        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>イベントのハンドラなどの例外を出力パネルに出す (実行は続ける)。</summary>
    public void ReportError(JavaScriptException ex)
    {
        (string message, ScriptDiagnostic diag, _) = Describe(ex.Error, ex);
        Context.Output(ScriptOutputKind.Error, message, diag.Path, diag.Line, diag.Column);
    }

    public ScriptExecution Run(string code)
    {
        _thread = Thread.CurrentThread;
        string mainKey = Context.Path is not null ? Path.GetFullPath(Context.Path) : "script:" + Context.Name;
        var loader = new ScriptModuleLoader(mainKey, code, Context.ScriptFolder, Context.Host.Folders.Library);
        if (!Context.Headless && !Context.Request.StopOnTimeLimit)
        {
            _budget.OnTimeLimit = AskContinue;
        }

        Context.Clock = _budget;
        Context.Thread = this;
        Context.ForceStop = () =>
        {
            _budget.RequestForceStop();
            Post(() => { });
        };

        Debugging.JsDebugSession? debugger = Context.Request.Debugger;
        _engine = new JintEngine(o =>
        {
            o.Interop.Enabled = false;
            o.LimitRecursion(_limits.RecursionDepth);
            o.Constraints.Constraints.Add(_budget);
            o.EnableModules(loader);
            if (debugger is not null)
            {
                o.DebugMode();
                o.Debugger.InitialStepMode = debugger.InitialStepMode;
                o.Debugger.StatementHandling = Jint.Runtime.Debugger.DebuggerStatementHandling.Script;
            }
        });

        try
        {
            Setup();
            debugger?.Attach(Context, _engine, _budget, mainKey, code);
            _budget.Resume();
            var op = _engine.Modules.StartImport(mainKey);
            _engine.Advanced.ProcessTasks();
            while (true)
            {
                if (_budget.ForceStopRequested)
                {
                    throw new ScriptTerminatedException(_budget.NormalStop);
                }

                if (op.IsCompleted && Volatile.Read(ref _pending) <= 0 && Context.Subscriptions == 0)
                {
                    break;
                }

                if (op.IsFaulted)
                {
                    break;
                }

                _budget.Suspend();
                BlockingCollection<Action>.TakeFromAny([_queue, _extensionQueue], out Action? next);
                _budget.Resume();
                next!();
                _engine.Advanced.ProcessTasks();
                _budget.CheckMemory();
            }

            _budget.Suspend();
            _budget.CheckMemory();
            if (op.IsFaulted)
            {
                return FromError(op.Error ?? JsValue.Undefined);
            }

            return Context.StopRequested && !Context.StopCalled ? ScriptExecution.Stopped() : ScriptExecution.Success;
        }
        catch (Exception ex)
        {
            return Translate(ex) ?? throw new InvalidOperationException(ex.Message, ex);
        }
        finally
        {
            debugger?.Detach();
        }
    }

    /// <summary>実行中に起きた例外を実行の結果にする。扱わない例外は null。</summary>
    private ScriptExecution? Translate(Exception exception)
    {
        switch (exception)
        {
            case ScriptTerminatedException ex:
                return ex.Normal || Context.StopCalled ? ScriptExecution.Success : ScriptExecution.Stopped(Msg.T("強制的に中断しました。", "Forcibly interrupted."));
            case ScriptLimitExceededException ex:
                return ScriptExecution.Error(ScriptFailure.Limit, ex.Message, [Location(ex.Message)]);
            case RecursionDepthOverflowException:
            {
                string message = Msg.T($"再帰の深さの上限 {_limits.RecursionDepth} を超えました。", $"The recursion depth limit of {_limits.RecursionDepth} was exceeded.");
                return ScriptExecution.Error(ScriptFailure.Limit, message, [Location(message)]);
            }

            case Acornima.ParseErrorException ex:
                return ScriptExecution.Error(ScriptFailure.ScriptError, Msg.T("構文エラー: ", "Syntax error: ") + ex.Description,
                    [new ScriptDiagnostic(ex.Description, ex.SourceFile ?? Context.Path, ex.LineNumber, ex.Column, "SyntaxError")]);
            case JavaScriptException ex:
                return FromError(ex.Error, ex);
            case OutOfMemoryException:
            {
                string message = Msg.T($"メモリの上限 {ExecutionBudget.FormatBytes(_limits.MemoryBytes)} を超えました。", $"The memory limit of {ExecutionBudget.FormatBytes(_limits.MemoryBytes)} was exceeded.");
                return ScriptExecution.Error(ScriptFailure.Limit, message, [Location(message)]);
            }

            default:
                return null;
        }
    }

    private ScriptDiagnostic Location(string message)
    {
        // 中断した位置 (Jint の呼び出し履歴の先頭)。
        string? stack = null;
        try
        {
            stack = _engine.Advanced.StackTrace;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
        }

        (string? path, int line, int column) = ParseLocation(stack);
        return new ScriptDiagnostic(message, path ?? Context.Path, line, column);
    }

    private bool AskContinue()
    {
        TimeSpan limit = _budget.Limit ?? TimeSpan.Zero;
        try
        {
            return Context.Dispatcher.InvokeAsync(() => Context.Environment.Ui.ConfirmContinueAfterTimeoutAsync(Context.Run, limit)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            return false;
        }
    }

    private void Setup()
    {
        var host = new JsObject(_engine);
        host.FastSetDataProperty("output", new ClrFunction(_engine, "output", (_, args) =>
        {
            string kind = args.Length > 0 ? args[0].ToString() : "info";
            string text = args.Length > 1 ? args[1].ToString() : "";
            _budget.CheckMemory();
            Context.Output(kind switch { "warning" => ScriptOutputKind.Warning, "error" => ScriptOutputKind.Error, _ => ScriptOutputKind.Info }, text);
            return JsValue.Undefined;
        }));
        host.FastSetDataProperty("createSignal", new ClrFunction(_engine, "createSignal", (_, _) => _binder.CreateOwnedSignal()));
        host.FastSetDataProperty("abortSignal", new ClrFunction(_engine, "abortSignal", (_, args) =>
        {
            _binder.Abort(args.Length > 0 ? args[0] : JsValue.Undefined, args.Length > 1 ? args[1].ToString() : "AbortError");
            return JsValue.Undefined;
        }));

        JsValue preludeFn = _engine.Evaluate(JsScriptRunner.Prelude, "hexeditor:prelude");
        ObjectInstance helpers = _engine.Call(preludeFn, JsValue.Undefined, [host]).AsObject();
        _binder = new JsBinder(_engine, this, helpers);

        Api.ScriptApp app = Context.App;
        JsValue appJs = DefineGlobals();

        // 組み込みモジュール (AUTO-02 の仕様 3)。
        _engine.Modules.Add(ScriptModuleLoader.BuiltinPrefix + "hash", b =>
        {
            foreach (HashAlgorithmInfo info in app.Hash.Algorithms)
            {
                string id = info.Id;
                string exportName = Regex.Replace(id, "[^A-Za-z0-9_]", "_");
                b.ExportFunction(exportName, args =>
                {
                    byte[] data = (byte[])_binder.ToClr(args.Length > 0 ? args[0] : JsValue.Undefined, typeof(byte[]), "data")!;
                    HashResult r = app.Hash.ComputeBytes(data, id);
                    return info.Bits <= 32 ? new JsNumber(Convert.ToUInt32(r.Hex, 16)) : new JsString(r.Hex);
                });
            }

            b.ExportValue("algorithms", _binder.Wrap(app.Hash.Algorithms, typeof(IReadOnlyList<HashAlgorithmInfo>)));
        });
        _engine.Modules.Add(ScriptModuleLoader.BuiltinPrefix + "app", b =>
        {
            b.ExportValue("app", appJs);
            b.ExportValue("default", appJs);
        });
        _engine.Modules.Add(ScriptModuleLoader.BuiltinPrefix + "text", b => b.ExportValue("default", _binder.Wrap(app.Text, typeof(IText))));
    }

    /// <summary>グローバル (AUTO-03 の仕様 2 と同じ名前。AUTO-04 の仕様 1)。コンソールでは入力ごとに今の実行のものに定義し直す。</summary>
    private JsValue DefineGlobals()
    {
        Api.ScriptApp app = Context.App;
        JsValue appJs = _binder.Wrap(app, typeof(IApp));
        Define("app", appJs);
        Define("doc", _binder.Wrap(app.ActiveDocument, typeof(IDocument)));
        Define("ui", _binder.Wrap(app.Ui, typeof(IUi)));
        Define("hash", _binder.Wrap(app.Hash, typeof(IHash)));
        Define("clipboard", _binder.Wrap(app.Clipboard, typeof(IClipboard)));
        Define("settings", _binder.Wrap(app.Settings, typeof(ISettings)));
        Define("script", _binder.Wrap(app.Script, typeof(IScript)));
        Define("inspector", _binder.Wrap(app.Inspector, typeof(IInspector)));
        Define("text", _binder.Wrap(app.Text, typeof(IText)));
        Define("templates", _binder.Wrap(app.Templates, typeof(ITemplates)));
        Define("fs", _binder.Wrap(app.Fs, typeof(IFileSystem)));
        Define("net", _binder.Wrap(app.Net, typeof(INet)));
        Define("process", _binder.Wrap(app.Process, typeof(IProcessRunner)));
        return appJs;
    }

    private bool _redefinableGlobals;

    private void Define(string name, JsValue value) =>
        _engine.Global.FastSetProperty(name, new Jint.Runtime.Descriptors.PropertyDescriptor(value, writable: false, enumerable: false, configurable: _redefinableGlobals));

    // ---- コンソールのセッション (AUTO-23) ----

    /// <summary>コンソールのセッションを始める (セッションのスレッドで呼ぶ)。変数は入力をまたいで保たれる。</summary>
    public void InitializeRepl()
    {
        _thread = Thread.CurrentThread;
        _redefinableGlobals = true;
        var loader = new ScriptModuleLoader("script:console", "", null, Context.Host.Folders.Library);
        _engine = new JintEngine(o =>
        {
            o.Interop.Enabled = false;
            o.LimitRecursion(_limits.RecursionDepth);
            o.Constraints.Constraints.Add(_budget);
            o.EnableModules(loader);
        });
        Setup();
    }

    /// <summary>
    /// 入力を 1 つ実行する。入力ごとの実行 (<paramref name="context"/>) に付け替え、実行時間・メモリの上限も入力ごとに数える
    /// (AUTO-23 の仕様 8)。結果が Promise なら、終わるまで待ってその値を返す。
    /// </summary>
    public ScriptExecution ExecuteRepl(ScriptContext context, string code, ScriptLimits limits, out ReplValue? result)
    {
        Context = context;
        result = null;
        _budget.Restart(limits.Time, limits.MemoryBytes);
        _budget.OnTimeLimit = !context.Headless && !context.Request.StopOnTimeLimit ? AskContinue : null;
        context.Clock = _budget;
        context.Thread = this;
        context.ForceStop = () =>
        {
            _budget.RequestForceStop();
            Post(() => { });
        };
        try
        {
            DefineGlobals();
            _budget.Resume();
            JsValue value = EvaluateInput(code);
            if (value.IsPromise())
            {
                // Promise (一括処理など) は、終わるまでイベントループを回して待ち、その値を結果にする。
                bool settled = false;
                bool rejected = false;
                JsValue settledValue = JsValue.Undefined;
                ObjectInstance promise = value.AsObject();
                _engine.Call(promise.Get("then"), promise,
                [
                    new ClrFunction(_engine, "resolved", (_, a) =>
                    {
                        settled = true;
                        settledValue = a.Length > 0 ? a[0] : JsValue.Undefined;
                        return JsValue.Undefined;
                    }),
                    new ClrFunction(_engine, "rejected", (_, a) =>
                    {
                        settled = true;
                        rejected = true;
                        settledValue = a.Length > 0 ? a[0] : JsValue.Undefined;
                        return JsValue.Undefined;
                    }),
                ]);
                PumpUntil(() => settled);
                if (rejected)
                {
                    throw new JavaScriptException(settledValue);
                }

                value = settledValue;
            }

            _budget.Suspend();
            _budget.CheckMemory();
            result = JsReplFormatter.Format(value);
            return context.StopRequested && !context.StopCalled ? ScriptExecution.Stopped() : ScriptExecution.Success;
        }
        catch (Exception ex)
        {
            return Translate(ex) ?? throw new InvalidOperationException(ex.Message, ex);
        }
        finally
        {
            _budget.Suspend();
        }
    }

    /// <summary>
    /// 入力を評価する。<c>await</c> を含む入力が構文エラーになる場合は、<c>async</c> の関数に包んで評価し直す
    /// (式ならその値を返す。文なら値は返さず、そこで宣言した変数は残らない)。
    /// </summary>
    private JsValue EvaluateInput(string code)
    {
        try
        {
            return _engine.Evaluate(code, "console");
        }
        catch (Exception ex) when (IsSyntaxError(ex) && code.Contains("await", StringComparison.Ordinal))
        {
            try
            {
                return _engine.Evaluate("(async () => { return (" + code + "\n); })()", "console");
            }
            catch (Exception inner) when (IsSyntaxError(inner))
            {
                return _engine.Evaluate("(async () => { " + code + "\n })()", "console");
            }
        }
    }

    private static bool IsSyntaxError(Exception ex) =>
        ex is Acornima.ParseErrorException || (ex is JavaScriptException js && js.Error is ObjectInstance o && o.Get("name").ToString() == "SyntaxError");

    /// <summary>条件が満たされるまで、イベントループ (一括処理の完了など) を回す。</summary>
    private void PumpUntil(Func<bool> done)
    {
        _engine.Advanced.ProcessTasks();
        while (!done())
        {
            if (_budget.ForceStopRequested)
            {
                throw new ScriptTerminatedException(_budget.NormalStop);
            }

            _budget.Suspend();
            BlockingCollection<Action>.TakeFromAny([_queue, _extensionQueue], out Action? next);
            _budget.Resume();
            next!();
            _engine.Advanced.ProcessTasks();
        }
    }

    private ScriptExecution FromError(JsValue error, JavaScriptException? ex = null)
    {
        string name = error is ObjectInstance o ? o.Get("name").ToString() : "Error";
        (string message, ScriptDiagnostic diag, string? stack) = Describe(error, ex);
        if (name == "CanceledError")
        {
            // 捕まえられずに終わったキャンセルは「停止しました」(AUTO-05 の「エラー」)。
            return ScriptExecution.Stopped();
        }

        ScriptFailure failure = name == "LimitError" ? ScriptFailure.Limit
            : name == "PermissionError" ? ScriptFailure.Permission
            : ScriptFailure.ScriptError;
        return ScriptExecution.Error(failure, message, [diag], stack);
    }

    private (string Message, ScriptDiagnostic Diagnostic, string? Stack) Describe(JsValue error, JavaScriptException? ex)
    {
        string name = "Error";
        string message = error.ToString();
        string? stack = null;
        if (error is ObjectInstance o)
        {
            name = o.Get("name").ToString();
            message = o.Get("message").ToString();
            JsValue s = o.Get("stack");
            stack = s.IsString() ? s.AsString() : null;
        }

        string? path = null;
        int line = 0;
        int column = 0;
        if (ex is not null && ex.Location.Start.Line > 0)
        {
            path = ex.Location.SourceFile;
            line = ex.Location.Start.Line;
            column = ex.Location.Start.Column + 1;
        }

        if (line == 0)
        {
            (path, line, column) = ParseLocation(stack);
        }

        if (line == 0)
        {
            // 「(パス:行:列)」をメッセージの中から探す (モジュールの読み込みの構文エラー)。
            Match loc = Regex.Match(message, @"\((?<file>[^()]+):(?<line>\d+):(?<col>\d+)\)");
            if (loc.Success)
            {
                path = loc.Groups["file"].Value;
                line = int.Parse(loc.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                column = int.Parse(loc.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        if (line == 0 && name == "SyntaxError")
        {
            // Acornima のメッセージ「Line N: ...」から位置を取る。
            Match m = Regex.Match(message, @"Line (\d+)(?::(\d+))?");
            if (m.Success)
            {
                line = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                column = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            }
        }

        path = NormalizePath(path) ?? Context.Path;
        string text = $"{name}: {message}";
        return (text, new ScriptDiagnostic(text, path, line, column, name), stack);
    }

    private string? NormalizePath(string? path) =>
        path is null || path.StartsWith("script:", StringComparison.Ordinal) || path.StartsWith("hexeditor:", StringComparison.Ordinal)
            ? (path is not null && path.StartsWith("script:", StringComparison.Ordinal) ? Context.Path : path)
            : Uri.TryCreate(path, UriKind.Absolute, out Uri? uri) && uri.IsFile ? uri.LocalPath : path;

    private static readonly Regex StackFrame = new(@"at (?:.*? \()?(?<file>[^()]+?):(?<line>\d+):(?<col>\d+)\)?", RegexOptions.CultureInvariant);

    private static (string? Path, int Line, int Column) ParseLocation(string? stack)
    {
        if (string.IsNullOrEmpty(stack))
        {
            return (null, 0, 0);
        }

        foreach (Match m in StackFrame.Matches(stack))
        {
            string file = m.Groups["file"].Value.Trim();
            if (file.StartsWith("hexeditor:prelude", StringComparison.Ordinal))
            {
                continue;
            }

            return (file, int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(m.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture) + 1);
        }

        return (null, 0, 0);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _extensionQueue.CompleteAdding();
        _engine?.Dispose();
        _queue.Dispose();
        _extensionQueue.Dispose();
    }
}
