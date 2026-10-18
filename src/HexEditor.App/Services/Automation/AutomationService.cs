using HexEditor.App.Services.Scripting;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Rpc;
using HexEditor.Platform.Automation;
using HexEditor.Platform.Security;

namespace HexEditor.App.Services.Automation;

/// <summary>
/// 外部からの自動操作 (08 の AUTO-45): 設定 <c>automation.rpc.enabled</c> (既定オフ) がオンの間だけ名前付きパイプで待ち受け、接続ごとに
/// 信頼の確認 (AUTO-34 と同じ形式) をしてから JSON-RPC のセッションを動かす。アプリ全体で 1 つ。
/// <para>
/// 安全のための決まり: パイプは現在の利用者だけに ACL で許可し、ネットワークからの接続は拒否する (仕様 2)。接続してきた実行ファイルを調べ、
/// 初めてのものは確認を出す。拒否したら接続を切る (仕様 4)。組織の設定 <c>scripts.policy</c> でスクリプトが無効なら待ち受けない。
/// 編集はスクリプトと同じく Undo でき、操作中のドキュメントには「外部から操作されています」の InfoBar を出す (仕様 7)。
/// </para>
/// <para>テスト用のビルドだけ、設定 <c>test.automation.pipeName</c> でパイプ名を変えられる (テストどうし・利用者のアプリと重ならないように)。</para>
/// </summary>
internal static class AutomationService
{
    public const string EnabledKey = "automation.rpc.enabled";
    public const string TestPipeNameKey = "test.automation.pipeName";

    private static readonly Lock Gate = new();
    private static NamedPipeRpcListener? _listener;
    private static int _connections;
    private static bool _initialized;

    /// <summary>待ち受けの状態が変わった (ステータスバーのアイコン)。どのスレッドからも呼ばれる。</summary>
    public static event EventHandler? StateChanged;

    /// <summary>待ち受けている (ステータスバーに「外部からの自動操作: 有効」を出す。仕様 3)。</summary>
    public static bool IsRunning
    {
        get
        {
            lock (Gate)
            {
                return _listener is not null;
            }
        }
    }

    /// <summary>待ち受けを始められなかった理由 (なければ null)。</summary>
    public static string? StartError { get; private set; }

    /// <summary>接続中の数。</summary>
    public static int Connections => Volatile.Read(ref _connections);

    /// <summary>パイプ名。</summary>
    public static string PipeName =>
        PackagingTestHooks.TestBuild && App.Settings.GetString(TestPipeNameKey, string.Empty) is { Length: > 0 } test ? test : NamedPipeRpcListener.CurrentUserPipeName;

    /// <summary>起動時に 1 度呼ぶ (スクリプトの用意の後)。設定の変更に追従する。</summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        App.Settings.Changed += keys =>
        {
            if (keys.Contains(EnabledKey) || keys.Contains(ScriptSettingKeys.Policy) || keys.Contains(TestPipeNameKey))
            {
                App.DispatcherQueue?.TryEnqueue(Apply);
            }
        };
        Apply();
    }

    /// <summary>設定に合わせて待ち受けを始める・止める。</summary>
    public static void Apply()
    {
        bool want = App.Settings.GetBool(EnabledKey, false) && !ScriptingService.PolicyDisabled;
        NamedPipeRpcListener? stop = null;
        bool changed = false;
        lock (Gate)
        {
            if (want && _listener is null)
            {
                var listener = new NamedPipeRpcListener(PipeName, HandleAsync);
                try
                {
                    listener.Start();
                    _listener = listener;
                    StartError = null;
                    AppLog.Info("Automation: listening on the named pipe.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    StartError = ex.Message;
                    AppLog.Warning($"Automation: cannot create the pipe: {ex.GetType().Name}");
                }

                changed = true;
            }
            else if (!want && _listener is not null)
            {
                stop = _listener;
                _listener = null;
                StartError = null;
                changed = true;
            }
        }

        if (stop is not null)
        {
            // パイプを閉じる (接続中のものも切る)。設定をオフにしたらパイプはなくなる (受け入れ基準 1)。接続の後片付けは UI スレッドを使うため、
            // UI スレッドでは待たない。
            _ = Task.Run(async () =>
            {
                await stop.DisposeAsync().ConfigureAwait(false);
                AppLog.Info("Automation: stopped.");
            });
        }

        if (changed)
        {
            StateChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>1 つの接続 (バックグラウンドのスレッド)。</summary>
    private static async Task HandleAsync(PipeClientConnection connection)
    {
        Interlocked.Increment(ref _connections);
        StateChanged?.Invoke(null, EventArgs.Empty);
        try
        {
            string? signer = connection.ExecutablePath is { } exe ? AuthenticodeVerifier.SignerOf(exe) : null;
            var client = new JsonRpcClient(connection.ExecutablePath, connection.ProcessId, signer);
            ScriptHost host = ScriptingService.Host;
            AppScriptEnvironment env = ScriptingService.Environment;
            JsonRpcAdmission admission = await JsonRpcTrust.CheckAsync(host, env, client).ConfigureAwait(false);
            AppLog.Info($"Automation: connection from {Path.GetFileName(connection.ExecutablePath ?? "?")} ({(admission.Allowed ? "allowed" : "refused")}).");
            if (!admission.Allowed)
            {
                // 拒否したら接続を切る (仕様 4)。理由を 1 行送ってから閉じる。
                // 相手が読まない場合に止まらないよう、5 秒で打ち切る。
                try
                {
                    await Task.Run(() =>
                    {
                        JsonRpcSession.SendRefusal(connection.Stream, admission.Reason);
                        connection.Stream.WaitForPipeDrain();
                    }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }

                return;
            }

            string name = Path.GetFileName(connection.ExecutablePath!);
            ScriptContext context = host.OpenExternalContext(name, env, admission.Permissions);
            try
            {
                var session = new JsonRpcSession(context, connection.Stream);
                await session.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                await Task.Run(() => host.CloseExternalContext(context)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            AppLog.Warning($"Automation: connection ended: {ex.GetType().Name}");
        }
        finally
        {
            Interlocked.Decrement(ref _connections);
            StateChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
