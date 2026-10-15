using System.Diagnostics;
using HexEditor.Core.Scripting;

namespace HexEditor.Core.Plugins;

/// <summary>プラグインの状態 (AUTO-32 の仕様 1: 有効 / 無効 / 読み込めません (理由) / 停止中)。</summary>
public enum PluginState
{
    /// <summary>まだ読み込んでいない、または読み込まなかった (セーフモード・管理者の確認で「読み込まない」など。理由を持つ)。</summary>
    NotLoaded,

    /// <summary>読み込んで有効。</summary>
    Enabled,

    /// <summary>利用者が無効にした (信頼の確認で「有効にしない」を含む)。</summary>
    Disabled,

    /// <summary>読み込めない (マニフェストの誤り、バージョンの不一致、署名が無効、読み込みの失敗。理由を持つ)。</summary>
    Failed,

    /// <summary>エラーが続いたため一時的に止めた (AUTO-30 の仕様 7)。</summary>
    Suspended,
}

/// <summary>見つけたプラグイン 1 つ (アプリ全体で 1 つの <see cref="PluginManager"/> が持つ)。</summary>
public sealed class PluginRecord
{
    private readonly List<RegisteredExtension> _extensions = [];
    private readonly Lock _lock = new();

    internal PluginRecord(string id, string folder, PluginManifest? manifest, bool development)
    {
        Id = id;
        Folder = folder;
        Manifest = manifest;
        IsDevelopment = development;
    }

    public string Id { get; }

    /// <summary>プラグインのフォルダ (<c>plugins/&lt;id&gt;/&lt;version&gt;/</c>、または開発用のフォルダ)。</summary>
    public string Folder { get; }

    /// <summary>マニフェスト (読めなかった場合は null)。</summary>
    public PluginManifest? Manifest { get; }

    /// <summary>開発用のフォルダから読み込んだもの (AUTO-33。プラグインマネージャに「開発中」と表示する)。</summary>
    public bool IsDevelopment { get; }

    public PluginState State { get; internal set; } = PluginState.NotLoaded;

    /// <summary>状態の理由 (読み込めない理由、停止した理由など)。</summary>
    public string? Reason { get; internal set; }

    public PluginSignatureInfo Signature { get; internal set; } = PluginSignatureInfo.Unchecked;

    /// <summary>全ファイルのハッシュ (信頼の記録の識別。AUTO-34 の仕様 1)。</summary>
    public string ContentHash { get; internal set; } = string.Empty;

    /// <summary>次の起動で状態が変わる (有効 / 無効の切り替え・削除・更新の後。AUTO-32 の仕様 3)。</summary>
    public bool PendingRestart { get; internal set; }

    /// <summary>アセンブリを読み込んだ (アンロードには対応しないため、削除は再起動後に行う)。</summary>
    public bool AssemblyLoaded { get; internal set; }

    /// <summary>読み込んだ入口のアセンブリの名前 (異常終了の呼び出し履歴との照合に使う)。</summary>
    public string? AssemblyName { get; internal set; }

    /// <summary>Mark of the Web が付いている (追加したパッケージから引き継ぐ。信頼の確認で警告する)。</summary>
    public bool FromInternet { get; internal set; }

    /// <summary>表示名 (表示言語)。マニフェストがなければ ID。</summary>
    public string NameFor(string? language) => Manifest?.NameFor(language) ?? Id;

    public string Version => Manifest?.Version.ToString() ?? Path.GetFileName(Folder);

    public string Publisher => Manifest?.Publisher ?? string.Empty;

    /// <summary>登録した拡張ポイント (重複などで無効にしたものを含む)。</summary>
    public IReadOnlyList<RegisteredExtension> Extensions
    {
        get
        {
            lock (_lock)
            {
                return [.. _extensions];
            }
        }
    }

    internal void AddExtension(RegisteredExtension extension)
    {
        lock (_lock)
        {
            _extensions.Add(extension);
        }
    }

    internal PluginGuard Guard { get; set; } = null!;

    public override string ToString() => $"{Id} {Version} ({State})";
}

/// <summary>プラグインのコードの呼び出しで起きた例外 (呼び出し元の処理だけを失敗にする。アプリは動き続ける)。</summary>
public sealed class PluginCallException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// プラグインのコードの呼び出しの見張り (AUTO-30 の仕様 7、AUTO-31 の仕様 5)。
/// <list type="bullet">
/// <item>例外はすべて捕まえて出力パネルの「プラグイン: 名前」に記録する。1 分間に 10 回以上起きたらプラグインを一時的に止める。</item>
/// <item>UI スレッドの呼び出しが 100 ms を超えたら警告し、続けて超えたらプラグインを止める。</item>
/// <item>止めたプラグインの呼び出しはせず、既定の値を返す。</item>
/// </list>
/// </summary>
public sealed class PluginGuard
{
    public const int MaxFaultsPerMinute = 10;
    public static readonly TimeSpan SlowCall = TimeSpan.FromMilliseconds(100);

    /// <summary>続けて遅かった回数がこれに達したら止める。</summary>
    public const int MaxConsecutiveSlowCalls = 2;

    private readonly PluginRecord _owner;
    private readonly IPluginGuardHost _host;
    private readonly Queue<DateTimeOffset> _faults = new();
    private readonly Lock _lock = new();
    private int _consecutiveSlow;

    internal PluginGuard(PluginRecord owner, IPluginGuardHost host)
    {
        _owner = owner;
        _host = host;
    }

    /// <summary>止めた (または有効でない) プラグインか。</summary>
    public bool IsStopped => _owner.State is PluginState.Suspended or PluginState.Failed or PluginState.Disabled;

    /// <summary>記録した例外の数 (テスト用)。</summary>
    public int FaultCount { get; private set; }

    /// <summary>呼ぶ。例外・止めたプラグインでは <paramref name="fallback"/> を返す。</summary>
    public T Invoke<T>(string what, Func<T> call, T fallback, bool uiThread = false)
    {
        if (IsStopped)
        {
            return fallback;
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fault(what, ex);
            return fallback;
        }
        finally
        {
            if (uiThread)
            {
                Timed(what, Stopwatch.GetElapsedTime(start));
            }
        }
    }

    /// <summary>呼ぶ。例外は記録してから <see cref="PluginCallException"/> にして投げる (計算・変換を失敗にする)。止めたプラグインでも投げる。</summary>
    public T InvokeOrThrow<T>(string what, Func<T> call)
    {
        if (IsStopped)
        {
            throw new PluginCallException(Stopped());
        }

        try
        {
            return call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fault(what, ex);
            throw new PluginCallException(Msg.T($"プラグイン「{_owner.NameFor(Msg.Default)}」でエラーが起きました: {ex.Message}",
                $"Plugin \"{_owner.NameFor(Msg.Default)}\" failed: {ex.Message}"), ex);
        }
    }

    public void InvokeOrThrow(string what, Action call) => InvokeOrThrow<object?>(what, () =>
    {
        call();
        return null;
    });

    /// <summary>止めた旨の表示。</summary>
    public string Stopped() => Msg.T($"プラグイン「{_owner.NameFor(Msg.Default)}」は停止中です。", $"Plugin \"{_owner.NameFor(Msg.Default)}\" is stopped.");

    public void Fault(string what, Exception ex)
    {
        bool suspend;
        lock (_lock)
        {
            FaultCount++;
            DateTimeOffset now = _host.Now;
            _faults.Enqueue(now);
            while (_faults.Count > 0 && now - _faults.Peek() > TimeSpan.FromMinutes(1))
            {
                _faults.Dequeue();
            }

            suspend = _faults.Count >= MaxFaultsPerMinute && _owner.State == PluginState.Enabled;
        }

        Exception shown = ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
        _host.Write(_owner, ScriptOutputKind.Error, $"{what}: {shown.GetType().Name}: {shown.Message}");
        if (suspend)
        {
            _host.Suspend(_owner, Msg.T("エラーが続いたため停止しました。", "Stopped because errors continued."));
        }
    }

    private void Timed(string what, TimeSpan elapsed)
    {
        if (elapsed <= SlowCall)
        {
            lock (_lock)
            {
                _consecutiveSlow = 0;
            }

            return;
        }

        bool suspend;
        lock (_lock)
        {
            _consecutiveSlow++;
            suspend = _consecutiveSlow >= MaxConsecutiveSlowCalls && _owner.State == PluginState.Enabled;
        }

        _host.Write(_owner, ScriptOutputKind.Warning, Msg.T($"{what}: UI スレッドの呼び出しに {elapsed.TotalMilliseconds:0} ms かかりました (上限 {SlowCall.TotalMilliseconds:0} ms)。",
            $"{what}: the UI thread call took {elapsed.TotalMilliseconds:0} ms (limit {SlowCall.TotalMilliseconds:0} ms)."));
        if (suspend)
        {
            _host.Suspend(_owner, Msg.T("UI スレッドの呼び出しが続けて遅かったため停止しました。", "Stopped because UI thread calls were repeatedly slow."));
        }
    }
}

/// <summary>見張りが使うもの (<see cref="PluginManager"/> が実装する)。</summary>
internal interface IPluginGuardHost
{
    DateTimeOffset Now { get; }

    void Write(PluginRecord plugin, ScriptOutputKind kind, string text);

    void Suspend(PluginRecord plugin, string reason);
}
