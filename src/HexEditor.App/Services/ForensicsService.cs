using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Forensics.Time;
using HexEditor.Platform;

namespace HexEditor.App.Services;

/// <summary>
/// フォレンジック機能のアプリ全体の状態 (12 の FOR-03〜05、FOR-18)。開いているケース (同時に 1 つ)、監査ログの入口、カービングの定義、
/// 日時の表示の設定をまとめて持つ。ウィンドウは <see cref="CaseChanged"/> などの変更の通知を受けて表示を更新する。
/// 他の領域 (書き込み保護モード、イメージ、消去、クローン、ファイルシステムなど) は <see cref="Audit"/> と <see cref="Cases"/> (<see cref="IEvidenceRegistry"/>) を使う。
/// </summary>
internal static class ForensicsService
{
    private static bool s_initialized;

    public static ForensicAudit Audit { get; private set; } = new(AuditIdentity.Current("0"));

    public static CaseManager Cases { get; private set; } = new(Audit, Environment.UserName);

    public static SignatureStore Signatures { get; private set; } = new(null);

    /// <summary>書き込み保護モードが有効か (FOR-01 を担当する部品が差し替える。既定はアプリの <see cref="WriteBlockMode"/>)。</summary>
    public static Func<bool> IsWriteBlockEnabled { get; set; } = () => WriteBlockMode.IsEnabled;

    /// <summary>書き込み保護モードを有効にする (ケースを開いたときの提案と自動で有効にする設定。FOR-01 を担当する部品が差し替える)。</summary>
    public static Action EnableWriteBlock { get; set; } = () => WriteBlockMode.Initialize(true);

    /// <summary>ケースを作った・開いた・閉じた。</summary>
    public static event EventHandler? CaseChanged
    {
        add => Cases.CaseChanged += value;
        remove => Cases.CaseChanged -= value;
    }

    /// <summary>証拠・ケース情報の変更。</summary>
    public static event EventHandler? EvidenceChanged
    {
        add => Cases.EvidenceChanged += value;
        remove => Cases.EvidenceChanged -= value;
    }

    /// <summary>日時の表示の設定が変わった。</summary>
    public static event EventHandler? TimeDisplayChanged;

    /// <summary>カービングの結果が変わった (パネルの更新)。</summary>
    public static event EventHandler? CarvingChanged;

    /// <summary>最後のカービングの結果 (パネルに出す)。</summary>
    public static CarvingResult? LastCarving { get; private set; }

    /// <summary>最後に作ったレポート (完了の通知とテストが読む)。</summary>
    public static Core.Forensics.Reports.ReportResult? LastReport { get; set; }

    /// <summary>実行中のカービングの結果の一覧 (走査しながら増える)。</summary>
    public static CarvedFileStore? LiveCarving { get; private set; }

    /// <summary>
    /// ドキュメントの割り当ての状況 (未割り当て領域・スラック。FOR-15、FOR-16)。ファイルシステムを解析する部品が設定する。null を返す
    /// ドキュメントでは「未割り当て領域のみ」「スラックのみ」を選べない。
    /// </summary>
    public static Func<ViewModels.DocumentViewModel, IAllocationMap?>? AllocationMapProvider { get; set; }

    /// <summary>カービングの対象のドキュメント (結果の行からジャンプする先)。</summary>
    public static object? CarvingTarget { get; set; }

    public static TimeProvider Clock => TestHooks.Time ?? TimeProvider.System;

    public static void Initialize(IAppEnvironment env)
    {
        if (s_initialized)
        {
            return;
        }

        s_initialized = true;
        string version = env.AppVersion;
        Audit = new ForensicAudit(AuditIdentity.Current(version), Clock);
        Audit.SetGlobalLogFolder(env.Locations.Settings);
        Cases = new CaseManager(Audit, Environment.UserName, Clock) { AppVersion = version };
        Signatures = new SignatureStore(env.Locations.Settings);
        try
        {
            Signatures.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Warning($"carving-signatures.json was not loaded: {ex.GetType().Name}");
        }

        ApplySettings();
        if (App.Settings is { } settings)
        {
            settings.Changed += _ => ApplySettings();
        }
    }

    /// <summary>設定の反映 (閲覧操作の記録、日時の表示)。</summary>
    public static void ApplySettings()
    {
        if (App.Settings is not { } s)
        {
            return;
        }

        Audit.LogBrowsing = s.GetBool("forensics.audit.logBrowsing", false);
        TimeDisplayChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>日時を表示する文脈 (設定の表示形式と、ケースのタイムゾーン。ケースがなければ Windows のタイムゾーン)。</summary>
    public static ForensicTimeContext TimeContext
    {
        get
        {
            TimeDisplayMode mode = ForensicTimeContext.ParseMode(App.Settings?.GetString(ForensicTimeContext.SettingKey, "both"));
            return new ForensicTimeContext(mode, ForensicTimeContext.ResolveZone(Cases.CurrentCase?.Info.TimeZoneId));
        }
    }

    private static readonly HashSet<string> s_writeCommands = new(StringComparer.Ordinal);

    /// <summary>
    /// 書き込みを伴うコマンドとして登録する。ケースを開いていて監査ログに書けない間は、これらのコマンドを無効にする (FOR-04 の「エラー」)。
    /// 消去・クローン・復元・墨塗りなど、他の領域の書き込みのコマンドも、ここに登録する。
    /// </summary>
    public static void RegisterWriteCommands(params string[] commandIds)
    {
        lock (s_writeCommands)
        {
            foreach (string id in commandIds)
            {
                s_writeCommands.Add(id);
            }
        }
    }

    /// <summary>コマンドが監査ログのために使えない理由 (使えるなら null)。<see cref="Commands.CommandHost.WriteGate"/> に設定する。</summary>
    public static string? WriteGateReason(string commandId)
    {
        bool isWrite;
        lock (s_writeCommands)
        {
            isWrite = s_writeCommands.Contains(commandId);
        }

        return isWrite && Cases.CurrentCase is not null && !Audit.WritesAllowed ? Loc.Get("Audit_WritesDisabled") : null;
    }

    /// <summary>ケースのタイムゾーンが変わった (日時の表示を更新する)。</summary>
    public static void NotifyTimeZoneChanged() => TimeDisplayChanged?.Invoke(null, EventArgs.Empty);

    public static void SetCarving(CarvingResult? result, CarvedFileStore? live)
    {
        LastCarving = result;
        LiveCarving = live;
        CarvingChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>カービングの結果が増えた (走査中)。</summary>
    public static void NotifyCarvingProgress() => CarvingChanged?.Invoke(null, EventArgs.Empty);
}
