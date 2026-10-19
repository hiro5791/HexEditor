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

    /// <summary>書き込み保護モードが有効か (FOR-01。<see cref="WriteBlockMode"/>)。</summary>
    public static Func<bool> IsWriteBlockEnabled { get; set; } = () => WriteBlockMode.IsEnabled;

    /// <summary>書き込み保護モードを有効にする (ケースを開いたときの提案と自動で有効にする設定。FOR-03 の仕様 4)。</summary>
    public static Action EnableWriteBlock { get; set; } = () => WriteBlockMode.Enable("case");

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

    /// <summary>
    /// ほかの領域の口をこのサービスにつなぐ (起動時に 1 回)。イメージ (FOR-20〜26) と消去・墨塗り (FOR-30〜32) の書き込み保護・証拠・監査ログ、
    /// ファイルシステム (FOR-08〜17) の監査ログ、カービング (FOR-18) の「未割り当て領域のみ」「スラックのみ」の範囲。
    /// </summary>
    public static void ConnectAreas()
    {
        AppImageForensicHooks.Install();
        Core.Wiping.WipeForensics.Hooks = AppWipeForensicHooks.Instance;
        Core.FileSystems.FileSystemAudit.Sink = RecordFileSystemEvent;
        AllocationMapProvider = doc => Core.FileSystems.FileSystemAllocationMap.For(doc.Document);

        // レポート (FOR-33) の節: パーティション (FOR-06)、ファイルシステムと論理ボリューム (FOR-07、FOR-09〜14)、取得記録 (FOR-20)。
        // デバイス情報 (FOR-27・28) の節は MainWindow.DeviceReports が登録する。
        Func<string, string?> localize = key => Loc.TryGet("Report_" + key);
        Core.Forensics.Reports.ReportRegistry.Shared.Register(new Core.Forensics.Reports.PartitionReportContributor(
            () => ReportDocuments().Select(d => (d.Name, ViewModels.FileSystemDocumentState.Find(d.Document)?.Partitions))
                .Where(x => x.Partitions is not null).Select(x => (x.Name, x.Partitions!)), localize));
        Core.Forensics.Reports.ReportRegistry.Shared.Register(new Core.Forensics.Reports.FileSystemReportContributor(
            () => ReportDocuments().Select(d => (d.Name, ViewModels.FileSystemDocumentState.Find(d.Document)?.Session))
                .Where(x => x.Session is not null).Select(x => (x.Name, x.Session!.VolumeOffset, x.Session.FileSystem)),
            () => ReportDocuments().Select(d => (d.Name, ViewModels.FileSystemDocumentState.Find(d.Document)?.LogicalVolumes))
                .Where(x => x.LogicalVolumes is not null).Select(x => (x.Name, x.LogicalVolumes!)),
            localize));
        Core.Forensics.Reports.ReportRegistry.Shared.Register(AcquisitionReports = new Core.Forensics.Reports.AcquisitionReportContributor(localize));
    }

    /// <summary>レポートの「取得記録」の節 (イメージの取得のたびに加える)。</summary>
    public static Core.Forensics.Reports.AcquisitionReportContributor AcquisitionReports { get; private set; } = new();

    private static volatile IReadOnlyList<(string Name, Core.Engine.Document Document)>? s_reportDocuments;

    /// <summary>
    /// レポートの節が読む、すべてのウィンドウの開いているドキュメント (表示名とドキュメント)。レポートはバックグラウンドで作るため、
    /// ドキュメントの一覧 (UI のスレッドが変更するコレクション) は <see cref="CaptureReportDocuments"/> が UI のスレッドで写したものを使う。
    /// </summary>
    private static IReadOnlyList<(string Name, Core.Engine.Document Document)> ReportDocuments() => s_reportDocuments ?? CaptureOpenDocuments();

    private static List<(string Name, Core.Engine.Document Document)> CaptureOpenDocuments() =>
        [.. WindowManager.Windows.ToArray().SelectMany(w => w.Vm.Documents.ToArray()).Distinct().Select(d => (d.DisplayName, d.Document))];

    /// <summary>レポートを作る前に UI のスレッドで呼ぶ: 開いているドキュメントの一覧を写す (作り終えたら <see cref="ReleaseReportDocuments"/>)。</summary>
    public static void CaptureReportDocuments() => s_reportDocuments = CaptureOpenDocuments();

    public static void ReleaseReportDocuments() => s_reportDocuments = null;

    /// <summary>ファイルシステムの機能の操作を監査ログに記録する (FOR-09 の受け入れ基準 4、FOR-16 の仕様 5・6、FOR-08 の仕様 4)。</summary>
    internal static void RecordFileSystemEvent(Core.FileSystems.FileSystemAuditEvent e)
    {
        string operation = e.Action == Core.FileSystems.FileSystemAuditAction.CreatePartitionEntries ? AuditOperations.PartitionEntriesCreate : AuditOperations.FileExport;
        var parameters = new List<KeyValuePair<string, string>>(e.Details) { new("action", e.Action.ToString()) };
        Audit.Record(new AuditRecord(operation)
        {
            EvidenceId = Cases.ActiveEvidence.FirstOrDefault(x => string.Equals(x.Path, e.Source, StringComparison.OrdinalIgnoreCase))?.Id,
            Path = e.Source,
            Parameters = parameters,
        });
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

    private static readonly HashSet<string> s_destructiveCommands = new(StringComparer.Ordinal);

    /// <summary>
    /// 書き込み保護モード (FOR-01) が有効な間は無効表示にする破壊的なコマンドとして登録する (仕様 2 の 5 つ目: 消去、クローン、セクタ範囲の復元、
    /// パーティションの書き込み、安全な削除、代替データストリームの作成・削除)。ツールチップは「書き込み保護モードが有効です」。
    /// 消去・クローン・復元・パーティションの書き込みの各部品は、自分のコマンドをここに登録する (実行時の拒否は Core の WriteBlockGate)。
    /// </summary>
    public static void RegisterDestructiveCommands(params string[] commandIds)
    {
        lock (s_destructiveCommands)
        {
            foreach (string id in commandIds)
            {
                s_destructiveCommands.Add(id);
            }
        }

        RegisterWriteCommands(commandIds);
    }

    /// <summary>書き込み保護モードで無効にする破壊的なコマンドか。</summary>
    public static bool IsDestructiveCommand(string commandId)
    {
        lock (s_destructiveCommands)
        {
            return s_destructiveCommands.Contains(commandId);
        }
    }

    /// <summary>
    /// コマンドが使えない理由 (使えるなら null)。<see cref="Commands.CommandHost.WriteGate"/> に設定する。書き込み保護モードの間の破壊的なコマンド
    /// (FOR-01 の仕様 2) と、監査ログに書けない間の書き込みを伴うコマンド (FOR-04 の「エラー」)。
    /// </summary>
    public static string? WriteGateReason(string commandId)
    {
        if (IsWriteBlockEnabled() && IsDestructiveCommand(commandId))
        {
            return Loc.Get("WriteBlock_Enabled");
        }

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
