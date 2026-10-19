using HexEditor.Core.Forensics.Audit;

namespace HexEditor.Core.Forensics.Cases;

/// <summary>
/// 証拠の登録口 (他の領域が使う)。ディスクイメージの作成 (FOR-20)、書き込み保護の例外 (FOR-01)、イメージの検証 (FOR-22)、カービング・レポートなどは、
/// ケースの実装を知らずにこの口を通して証拠を登録・照会する。実装は <see cref="CaseManager"/>。
/// </summary>
public interface IEvidenceRegistry
{
    /// <summary>今開いているケース。なければ null。</summary>
    ForensicCase? CurrentCase { get; }

    /// <summary>除外していない証拠。</summary>
    IReadOnlyList<EvidenceItem> ActiveEvidence { get; }

    /// <summary>このデータソースが証拠に登録されているか (書き込み保護の例外を設定できない判定。FOR-01 の仕様 5)。</summary>
    bool IsRegistered(EvidenceKind kind, string path);

    /// <summary>証拠を登録して監査ログに記録する。ケースが開いていなければ null。既に登録済みなら <see cref="DuplicateEvidenceException"/>。</summary>
    EvidenceItem? Register(EvidenceRegistration registration);

    /// <summary>ハッシュの計算結果を記録する。</summary>
    void RecordHashes(string evidenceId, IReadOnlyList<EvidenceHash> hashes);

    /// <summary>検証の結果 (FOR-22) を記録する。</summary>
    void RecordVerification(string evidenceId, bool passed);

    /// <summary>調査対象のデバイス (ディスク・ボリューム) のパス。出力先の検査に使う。</summary>
    IReadOnlyList<string> EvidenceDevicePaths { get; }
}

/// <summary>
/// アプリ全体でただ 1 つのケース (FOR-03 の仕様 3) と監査ログ (<see cref="ForensicAudit"/>) を持つ。作成・オープン・クローズ・証拠の追加と除外を
/// 監査ログに記録する。
/// </summary>
public sealed class CaseManager : IEvidenceRegistry
{
    private readonly object _gate = new();
    private readonly string _user;
    private readonly TimeProvider _time;
    private ForensicCase? _current;
    private AuditLog? _log;

    public CaseManager(ForensicAudit audit, string user, TimeProvider? time = null)
    {
        Audit = audit;
        _user = user;
        _time = time ?? TimeProvider.System;
    }

    public ForensicAudit Audit { get; }

    /// <summary>監査ログの保存先 (null ならファイル)。テストで書き込みエラー・空き容量不足を再現するために差し替える。</summary>
    public IAuditStorage? Storage { get; set; }

    public ForensicCase? CurrentCase
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>ケースを作った・開いた・閉じた。</summary>
    public event EventHandler? CaseChanged;

    /// <summary>証拠の追加・除外・ハッシュ・検証結果の変更 (ケース情報の変更も含む)。</summary>
    public event EventHandler? EvidenceChanged;

    public IReadOnlyList<EvidenceItem> ActiveEvidence => CurrentCase?.Evidence ?? [];

    public IReadOnlyList<string> EvidenceDevicePaths => [.. ActiveEvidence.Where(e => e.IsDevice).Select(e => e.Path)];

    public bool IsRegistered(EvidenceKind kind, string path) => CurrentCase?.FindBySource(kind, path) is not null;

    // ---- ケース ----

    /// <summary>新しいケースを作って開く。既にケースを開いていれば <see cref="InvalidOperationException"/> (先に閉じる)。</summary>
    public ForensicCase Create(string folder, CaseInfo info)
    {
        EnsureNoCase();
        ForensicCase created = ForensicCase.Create(folder, info, _user, _time);
        try
        {
            Attach(created);
        }
        catch (AuditWriteException ex)
        {
            throw new CaseFolderException(ex.Message, ex);
        }

        Audit.Record(new AuditRecord(AuditOperations.CaseCreate)
        {
            Path = created.Folder,
            Parameters = AuditRecord.Args(("number", info.Number), ("name", info.Name), ("investigator", info.Investigator), ("timezone", created.Info.TimeZoneId)),
        });
        CaseChanged?.Invoke(this, EventArgs.Empty);
        return created;
    }

    /// <summary>ケースを開く。<c>case.json</c> が壊れていれば <see cref="CaseFileException"/>。</summary>
    public ForensicCase Open(string folder)
    {
        EnsureNoCase();
        ForensicCase opened = ForensicCase.Open(folder, _user, _time);
        try
        {
            Attach(opened);
        }
        catch (AuditWriteException ex)
        {
            throw new CaseFolderException(ex.Message, ex);
        }

        Audit.Record(new AuditRecord(AuditOperations.CaseOpen)
        {
            Path = opened.Folder,
            Parameters = AuditRecord.Args(("number", opened.Info.Number), ("evidence", opened.Evidence.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        });
        CaseChanged?.Invoke(this, EventArgs.Empty);
        return opened;
    }

    private void EnsureNoCase()
    {
        if (CurrentCase is not null)
        {
            throw new InvalidOperationException("A case is already open. Close it first.");
        }
    }

    private void Attach(ForensicCase c)
    {
        AuditLog log = AuditLog.Open(c.AuditPath, AuditIdentity.Current(AppVersion), _time, Storage);
        lock (_gate)
        {
            _current = c;
            _log = log;
        }

        c.Changed += OnCaseChanged;
        Audit.AttachCase(log);
    }

    private void OnCaseChanged(object? sender, EventArgs e) => EvidenceChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>アプリの版 (監査ログの各行に入れる)。起動時に設定する。</summary>
    public string AppVersion { get; set; } = "0.0.0";

    /// <summary>ケースを閉じる (監査ログに記録してから外す)。</summary>
    public void Close()
    {
        ForensicCase? c;
        lock (_gate)
        {
            c = _current;
        }

        if (c is null)
        {
            return;
        }

        Audit.Record(new AuditRecord(AuditOperations.CaseClose) { Path = c.Folder });
        c.Changed -= OnCaseChanged;
        Audit.DetachCase();
        lock (_gate)
        {
            _current = null;
            _log = null;
        }

        CaseChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- 証拠 ----

    public EvidenceItem? Register(EvidenceRegistration registration)
    {
        if (CurrentCase is not { } c)
        {
            return null;
        }

        Audit.EnsureCanWrite();
        EvidenceItem item = c.AddEvidence(registration);
        Audit.Record(new AuditRecord(AuditOperations.EvidenceAdd)
        {
            EvidenceId = item.Id,
            Path = item.Path,
            Parameters = AuditRecord.Args(("kind", item.Kind.ToString()), ("description", item.Description), ("model", item.Model ?? string.Empty),
                ("serial", item.Serial ?? string.Empty), ("capacity", item.CapacityBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)),
        });
        if (item.Hashes.Count > 0)
        {
            RecordHashAudit(item, item.Hashes);
        }

        return item;
    }

    /// <summary>証拠を一覧から外す (データソースには何もしない)。外せたか。</summary>
    public bool Exclude(string id)
    {
        if (CurrentCase is not { } c || c.Find(id) is not { Excluded: false } item)
        {
            return false;
        }

        Audit.EnsureCanWrite();
        bool done = c.Exclude(id);
        if (done)
        {
            Audit.Record(new AuditRecord(AuditOperations.EvidenceExclude)
            {
                EvidenceId = id,
                Path = item.Path,
                Message = "Removed from the evidence list; the data source was not touched.",
            });
        }

        return done;
    }

    public void RecordHashes(string evidenceId, IReadOnlyList<EvidenceHash> hashes)
    {
        if (CurrentCase?.SetHashes(evidenceId, hashes) is { } item)
        {
            RecordHashAudit(item, hashes);
        }
    }

    private void RecordHashAudit(EvidenceItem item, IReadOnlyList<EvidenceHash> hashes) =>
        Audit.Record(new AuditRecord(AuditOperations.HashCompute)
        {
            EvidenceId = item.Id,
            Path = item.Path,
            Parameters = [.. hashes.Select(h => new KeyValuePair<string, string>(h.Algorithm, h.Value))],
        });

    public void RecordVerification(string evidenceId, bool passed)
    {
        if (CurrentCase?.SetVerification(evidenceId, passed) is { } item)
        {
            Audit.Record(new AuditRecord(AuditOperations.EvidenceVerify)
            {
                EvidenceId = item.Id,
                Path = item.Path,
                Result = passed ? AuditResult.Success : AuditResult.Failure,
            });
        }
    }

    /// <summary>ハッシュの計算を中止した: 証拠は登録したまま「ハッシュ未計算」にする。監査ログにキャンセルを記録する。</summary>
    public void RecordHashCancelled(string evidenceId)
    {
        if (CurrentCase?.Find(evidenceId) is { } item)
        {
            Audit.Record(new AuditRecord(AuditOperations.HashCompute) { EvidenceId = item.Id, Path = item.Path, Result = AuditResult.Cancelled });
        }
    }
}
