using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Forensics.WriteBlock;
using HexEditor.Platform;

namespace HexEditor.App.Services;

/// <summary>
/// アプリ全体の書き込み保護モード (12 の FOR-01)。状態と書き込みの関門は Core の <see cref="WriteBlockGate.Global"/> が持ち、保存・デバイス・
/// プロセス・ファイルのツール・スクリプトの書き込みはそこで拒否される。ここは UI 側の入口: 起動時の状態 (仕様 1: 再起動後も保持、設定
/// 「起動時に常に有効にする」、<c>--write-block</c>)、切り替えと監査ログ (<see cref="WriteBlockService"/>)、保護の対象のファイル (開いている
/// ドキュメントのデータソースと証拠) の一覧、補助プロセスへの通知 (<c>SetWriteBlock</c>)。
/// </summary>
internal static class WriteBlockMode
{
    /// <summary>設定「起動時に常に有効にする」(既定オフ)。</summary>
    public const string AlwaysOnStartupKey = "forensics.writeBlock.alwaysOnStartup";

    /// <summary>アプリの状態: 前回終了したときの状態 (仕様 1: 再起動後も保持する)。</summary>
    public const string StateKey = "forensics.writeBlock.enabled";

    /// <summary>アプリの状態: 初回の InfoBar (仕様 7) を出したか。</summary>
    public const string NoticeShownKey = "forensics.writeBlock.noticeShown";

    private static readonly object s_lock = new();
    private static readonly Dictionary<string, int> s_openSources = new(StringComparer.OrdinalIgnoreCase);
    private static bool s_started;

    public static WriteBlockPolicy Policy => WriteBlockGate.Global;

    public static WriteBlockService Service { get; } = new(WriteBlockGate.Global, () => ForensicsService.Audit);

    public static bool IsEnabled => Policy.IsEnabled;

    /// <summary>モード・例外が変わった (任意のスレッド)。</summary>
    public static event EventHandler? Changed
    {
        add => Policy.Changed += value;
        remove => Policy.Changed -= value;
    }

    /// <summary>無効表示にする機能のツールチップの理由 (仕様 2: 「書き込み保護モードが有効です」)。無効なら null。</summary>
    public static string? Reason => IsEnabled ? Loc.Get("WriteBlock_Enabled") : null;

    /// <summary>
    /// 起動時の状態を決めて有効にする (最初のドキュメントを開く前に呼ぶ。AUTO-37 の <c>--write-block</c>)。保護の対象の判定と、状態の保存・
    /// 補助プロセスへの通知をつなぐ。
    /// </summary>
    public static void Start(bool commandLine)
    {
        if (s_started)
        {
            return;
        }

        s_started = true;
        Policy.IsProtectedPath = IsProtectedPath;
        Changed += (_, _) =>
        {
            AppState.SetString(StateKey, IsEnabled ? "on" : "off");
            _ = App.Devices?.SetWriteBlockAsync(IsEnabled);
        };

        bool always = App.Settings?.GetBool(AlwaysOnStartupKey, false) == true;
        bool previous = AppState.GetString(StateKey, "off") == "on";
        string? source = commandLine ? "commandLine" : always ? "startup" : previous ? "restored" : null;
        if (source is not null)
        {
            Enable(source);
        }
    }

    /// <summary>有効にする。変わったら true。</summary>
    public static bool Enable(string source)
    {
        bool changed = Service.Enable(source);
        if (changed)
        {
            AppLog.Info($"Write protection mode enabled ({source})");
        }

        return changed;
    }

    /// <summary>解除する (確認ダイアログで「書き込み保護を解除する」を選んだ後)。</summary>
    public static bool Disable(IReadOnlyList<string> openEvidence)
    {
        bool changed = Service.Disable(openEvidence);
        if (changed)
        {
            AppLog.Info("Write protection mode disabled");
        }

        return changed;
    }

    /// <summary>初回の InfoBar を出すか (出すと記録する)。</summary>
    public static bool TakeFirstNotice()
    {
        if (AppState.GetString(NoticeShownKey, string.Empty) == "1")
        {
            return false;
        }

        AppState.SetString(NoticeShownKey, "1");
        return true;
    }

    // ---- 保護の対象 ----

    /// <summary>開いたドキュメントのデータソースのファイルを記録する (同じファイルを複数のタブで開けば数える)。</summary>
    public static void AddOpenSource(string path)
    {
        string key = WriteBlockPolicy.Normalize(path);
        lock (s_lock)
        {
            s_openSources[key] = s_openSources.TryGetValue(key, out int n) ? n + 1 : 1;
        }
    }

    public static void RemoveOpenSource(string path)
    {
        string key = WriteBlockPolicy.Normalize(path);
        lock (s_lock)
        {
            if (s_openSources.TryGetValue(key, out int n))
            {
                if (n <= 1)
                {
                    s_openSources.Remove(key);
                }
                else
                {
                    s_openSources[key] = n - 1;
                }
            }
        }
    }

    /// <summary>保護の対象か: 開いているドキュメントのデータソース、証拠に登録したファイル・イメージ (どのスレッドからも呼ばれる)。</summary>
    private static bool IsProtectedPath(string normalized)
    {
        lock (s_lock)
        {
            if (s_openSources.ContainsKey(normalized))
            {
                return true;
            }
        }

        return ForensicsService.Cases.IsRegistered(EvidenceKind.File, normalized) || ForensicsService.Cases.IsRegistered(EvidenceKind.ImageFile, normalized);
    }

    /// <summary>テスト用: 状態を変えずに保存の記録を消す (テストのプロファイルの間で持ち越さない)。</summary>
    internal static int OpenSourceCount
    {
        get
        {
            lock (s_lock)
            {
                return s_openSources.Count;
            }
        }
    }
}
