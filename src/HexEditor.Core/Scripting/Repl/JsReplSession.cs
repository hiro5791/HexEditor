using System.Collections.Concurrent;
using HexEditor.Core.Scripting.Js;

namespace HexEditor.Core.Scripting.Repl;

/// <summary>
/// JavaScript のコンソールのセッション (AUTO-23 の仕様 3): 1 つのエンジンを入力をまたいで使い、変数を保つ。
/// 入力は <see cref="ScriptRunRequest.Repl"/> に渡して <see cref="ScriptHost"/> で実行する (入力 1 回が 1 つの実行 =
/// Undo の単位・実行時間の上限・許可の対象になる。仕様 7・8)。「リセット」は新しいセッションを作る。
/// </summary>
public sealed class JsReplSession : IDisposable
{
    private readonly BlockingCollection<Action> _inbox = [];
    private readonly Lock _lock = new();
    private Thread? _thread;
    private JsSession? _session;
    private bool _disposed;

    /// <summary>入力を実行する (セッションのスレッドで。前の入力が終わるまで待つ)。</summary>
    internal Task<ScriptExecution> ExecuteAsync(ScriptContext context, string code, ScriptLimits limits)
    {
        var tcs = new TaskCompletionSource<ScriptExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureStarted(context, limits);
        _inbox.Add(() =>
        {
            try
            {
                ScriptExecution execution = _session!.ExecuteRepl(context, code, limits, out ReplValue? value);
                if (value is not null)
                {
                    context.Request.OnReplResult?.Invoke(value);
                }

                tcs.TrySetResult(execution);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(ScriptExecution.Error(ScriptFailure.ScriptError, ex.Message, [new ScriptDiagnostic(ex.Message, null, 0, 0)], ex.ToString()));
            }
        });
        return tcs.Task;
    }

    private void EnsureStarted(ScriptContext first, ScriptLimits limits)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null)
            {
                return;
            }

            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                try
                {
                    _session = new JsSession(first, limits);
                    _session.InitializeRepl();
                }
                finally
                {
                    ready.Set();
                }

                foreach (Action work in _inbox.GetConsumingEnumerable())
                {
                    work();
                }

                _session.Dispose();
            }, 256 * 1024 * 1024)
            {
                IsBackground = true,
                Name = "Script console",
            };
            _thread.Start();
            ready.Wait();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inbox.CompleteAdding();
        }
    }
}
