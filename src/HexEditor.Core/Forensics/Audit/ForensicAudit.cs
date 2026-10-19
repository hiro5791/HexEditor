namespace HexEditor.Core.Forensics.Audit;

/// <summary>監査ログが使えなくなった・戻った (FOR-04 の「エラー」)。</summary>
public sealed class AuditFaultEventArgs(bool faulted, string message) : EventArgs
{
    public bool Faulted { get; } = faulted;

    public string Message { get; } = message;
}

/// <summary>
/// 監査ログの入口 (FOR-04)。ケースを開いている間は、ケースフォルダの <c>audit.log</c> に記録する。ケースがないときは記録しない。ただし
/// <see cref="AuditRecord.AlwaysGlobal"/> の操作 (書き込み保護モードの切り替え。仕様 8) は、設定フォルダの <c>forensics-audit.log</c> に同じ形式で記録する。
/// 他の領域の機能 (イメージ作成、消去、クローン、ファイルの書き出しなど) は、この入口の <see cref="Record"/> / <see cref="Begin"/> で記録する。
/// 記録できなくなったら <see cref="WritesAllowed"/> を false にして <see cref="FaultChanged"/> を出す (書き込みを伴う機能はこれを見て無効にする)。
/// 失敗中の記録はメモリに溜め (上限あり)、<see cref="TryRecover"/> で書き戻す。
/// </summary>
public sealed class ForensicAudit
{
    /// <summary>失敗中に溜める記録の上限。</summary>
    public const int PendingLimit = 10_000;

    public const string GlobalFileName = "forensics-audit.log";

    private readonly object _gate = new();
    private readonly AuditIdentity _identity;
    private readonly TimeProvider _time;
    private readonly IAuditStorage? _storage;
    private readonly List<(AuditLog Log, AuditRecord Record, DateTime Time)> _pending = [];
    private AuditLog? _caseLog;
    private AuditLog? _globalLog;
    private string? _globalPath;
    private string _faultMessage = string.Empty;

    public ForensicAudit(AuditIdentity identity, TimeProvider? time = null, IAuditStorage? storage = null)
    {
        _identity = identity;
        _time = time ?? TimeProvider.System;
        _storage = storage;
    }

    /// <summary>ログに書き込める状態か。false の間は、書き込みを伴う操作 (消去・クローンなど) を無効にする。</summary>
    public bool WritesAllowed
    {
        get
        {
            lock (_gate)
            {
                return !IsFaulted;
            }
        }
    }

    public bool IsFaulted { get; private set; }

    public string FaultMessage
    {
        get
        {
            lock (_gate)
            {
                return _faultMessage;
            }
        }
    }

    /// <summary>設定「閲覧操作も記録する」(既定オフ)。ジャンプ・検索・ブックマークの追加を記録する。</summary>
    public bool LogBrowsing { get; set; }

    public AuditLog? CaseLog
    {
        get
        {
            lock (_gate)
            {
                return _caseLog;
            }
        }
    }

    public event EventHandler<AuditFaultEventArgs>? FaultChanged;

    /// <summary>1 行を記録した (監査ログのパネルが表示を更新する)。書き込みのスレッドから呼ばれる。</summary>
    public event EventHandler<AuditEntry>? Recorded;

    /// <summary>ケースの監査ログを使い始める。</summary>
    public void AttachCase(AuditLog log)
    {
        lock (_gate)
        {
            _caseLog = log;
        }
    }

    public void DetachCase()
    {
        lock (_gate)
        {
            _caseLog = null;
        }
    }

    /// <summary>ケースがなくても書き込み保護モードの切り替えを記録するログの場所 (設定フォルダの <c>forensics-audit.log</c>)。</summary>
    public void SetGlobalLogFolder(string settingsFolder)
    {
        lock (_gate)
        {
            _globalPath = Path.Combine(settingsFolder, GlobalFileName);
            _globalLog = null;
        }
    }

    private AuditLog? Target(AuditRecord record)
    {
        if (_caseLog is not null)
        {
            return _caseLog;
        }

        if (record.AlwaysGlobal && _globalPath is not null)
        {
            _globalLog ??= AuditLog.Open(_globalPath, _identity, _time, _storage);
            return _globalLog;
        }

        return null;
    }

    /// <summary>
    /// 記録する。記録先がない (ケースが開いていない) ときや、閲覧操作で設定がオフのときは何もせず null。書けなければ溜めて <see cref="FaultChanged"/> を出し、null。
    /// </summary>
    public AuditEntry? Record(AuditRecord record)
    {
        if (AuditOperations.IsBrowsing(record.Operation) && !LogBrowsing)
        {
            return null;
        }

        AuditFaultEventArgs? args = null;
        AuditEntry? entry = null;
        lock (_gate)
        {
            AuditLog? target;
            try
            {
                target = Target(record);
            }
            catch (IOException ex)
            {
                // 設定フォルダのログを開けない: 記録できなかったことを知らせる。
                args = Fault(ex.Message);
                goto raise;
            }

            if (target is null)
            {
                return null;
            }

            DateTime now = _time.GetUtcNow().UtcDateTime;
            if (IsFaulted)
            {
                Enqueue(target, record, now);
                return null;
            }

            try
            {
                entry = target.Append(record, now);
            }
            catch (AuditWriteException ex)
            {
                Enqueue(target, record, now);
                args = Fault(ex.Message);
            }
        }

    raise:
        if (args is not null)
        {
            FaultChanged?.Invoke(this, args);
        }

        if (entry is not null)
        {
            Recorded?.Invoke(this, entry);
        }

        return entry;
    }

    private AuditFaultEventArgs Fault(string message)
    {
        IsFaulted = true;
        _faultMessage = message;
        return new AuditFaultEventArgs(true, message);
    }

    private void Enqueue(AuditLog log, AuditRecord record, DateTime time)
    {
        if (_pending.Count < PendingLimit)
        {
            _pending.Add((log, record, time));
        }
    }

    /// <summary>操作を始める前の確認: 今のログに書き込めるか。書けなければ <see cref="AuditWriteException"/> (操作を実行しない。FOR-04 の「エラー」)。</summary>
    public void EnsureCanWrite()
    {
        AuditLog? log;
        lock (_gate)
        {
            if (IsFaulted)
            {
                throw new AuditWriteException(_faultMessage, new IOException(_faultMessage));
            }

            log = _caseLog;
        }

        log?.Probe();
    }

    /// <summary>ログに書けるか確かめて、書けるなら溜めた記録を書き戻して <see cref="WritesAllowed"/> に戻す。戻れたか。</summary>
    public bool TryRecover()
    {
        AuditFaultEventArgs? args = null;
        lock (_gate)
        {
            if (!IsFaulted)
            {
                return true;
            }

            try
            {
                foreach (AuditLog log in _pending.Select(p => p.Log).Concat(_caseLog is null ? [] : [_caseLog]).Distinct())
                {
                    log.Probe();
                }

                int count = _pending.Count;
                foreach ((AuditLog log, AuditRecord record, DateTime time) in _pending.ToList())
                {
                    log.Append(record, time);
                    _pending.RemoveAt(0);
                }

                IsFaulted = false;
                _faultMessage = string.Empty;
                (_caseLog ?? _globalLog)?.Append(new AuditRecord(AuditOperations.AuditRecovered)
                {
                    Message = "Audit logging resumed.",
                    Parameters = AuditRecord.Args(("delayed", count.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                }, _time.GetUtcNow().UtcDateTime);
                args = new AuditFaultEventArgs(false, string.Empty);
            }
            catch (AuditWriteException)
            {
                return false;
            }
        }

        FaultChanged?.Invoke(this, args);
        return true;
    }

    /// <summary>
    /// 長い操作を 2 行 (開始と終了) で記録する。開始の行は <c>phase=start</c>、<see cref="AuditScope.Complete"/> で <c>phase=end</c> の行を書く。
    /// </summary>
    public AuditScope Begin(AuditRecord start)
    {
        Record(start with { Parameters = [.. start.Parameters, new("phase", "start")] });
        return new AuditScope(this, start);
    }
}

/// <summary>2 行で記録する操作の終了側。<see cref="Dispose"/> までに <see cref="Complete"/> がなければ失敗として記録する。</summary>
public sealed class AuditScope : IDisposable
{
    private readonly ForensicAudit _audit;
    private readonly AuditRecord _start;
    private bool _done;

    internal AuditScope(ForensicAudit audit, AuditRecord start)
    {
        _audit = audit;
        _start = start;
    }

    /// <summary>終了の行を書く (<paramref name="extra"/> は結果のハッシュなどを加えるパラメーター)。</summary>
    public AuditEntry? Complete(AuditResult result, string message = "", IReadOnlyList<KeyValuePair<string, string>>? extra = null)
    {
        if (_done)
        {
            return null;
        }

        _done = true;
        return _audit.Record(_start with
        {
            Result = result,
            Message = message,
            Parameters = [.. _start.Parameters, .. extra ?? [], new("phase", "end")],
        });
    }

    public void Dispose() => Complete(AuditResult.Failure, "The operation did not complete.");
}
