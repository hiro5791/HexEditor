using System.Globalization;
using System.Text.Json;
using HexEditor.Core.Files;

namespace HexEditor.Core.Forensics.Cases;

/// <summary>ケース情報 (12 の FOR-03 の仕様 2)。ケース番号とケース名は必須。</summary>
public sealed record CaseInfo
{
    public string Number { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Investigator { get; init; } = string.Empty;

    public string Organization { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public DateTime CreatedUtc { get; init; }

    /// <summary>タイムゾーン ID (FAT などタイムゾーンを持たない日時を解釈する既定値。FOR-05)。</summary>
    public string TimeZoneId { get; init; } = string.Empty;

    /// <summary>入力の誤り (必須の欄が空)。なければ空。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Number))
        {
            errors.Add(nameof(Number));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add(nameof(Name));
        }

        return errors;
    }
}

/// <summary>証拠のデータソースの種類 (開いているドキュメントの種類)。</summary>
public enum EvidenceKind
{
    PhysicalDisk,
    Volume,
    ImageFile,
    File,
    Memory,
}

/// <summary>ハッシュ 1 つ (アルゴリズム名と値)。</summary>
public sealed record EvidenceHash(string Algorithm, string Value);

/// <summary>ハッシュの計算の状態。キャンセルしたら <see cref="NotComputed"/> のまま (仕様の「巨大ファイル・長時間処理」)。</summary>
public enum EvidenceHashState
{
    NotComputed,
    Computed,
}

/// <summary>証拠 1 件 (FOR-03 の仕様 5)。除外しても <c>case.json</c> に残す (仕様 7)。</summary>
public sealed record EvidenceItem
{
    public string Id { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public EvidenceKind Kind { get; init; }

    public string Path { get; init; } = string.Empty;

    public string? Model { get; init; }

    public string? Serial { get; init; }

    public long? CapacityBytes { get; init; }

    public long? SectorCount { get; init; }

    public int? SectorSize { get; init; }

    public IReadOnlyList<EvidenceHash> Hashes { get; init; } = [];

    public EvidenceHashState HashState { get; init; }

    /// <summary>ハッシュを計算した日時 (UTC)。</summary>
    public DateTime? HashedUtc { get; init; }

    public DateTime AddedUtc { get; init; }

    public string AddedBy { get; init; } = string.Empty;

    /// <summary>一覧から外した (除外の印)。</summary>
    public bool Excluded { get; init; }

    public DateTime? ExcludedUtc { get; init; }

    public string? ExcludedBy { get; init; }

    /// <summary>検証結果 (FOR-22)。<c>passed</c> / <c>failed</c> / null (未検証)。</summary>
    public string? Verification { get; init; }

    public DateTime? VerifiedUtc { get; init; }

    /// <summary>イメージの元のデータソース (FOR-20 で作ったイメージの場合の、元の証拠番号)。</summary>
    public string? DerivedFrom { get; init; }

    /// <summary>ハッシュの値 (アルゴリズム名は大文字小文字を区別しない)。なければ null。</summary>
    public string? HashOf(string algorithm) => Hashes.FirstOrDefault(h => string.Equals(h.Algorithm, algorithm, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>一覧の「ハッシュ」の列の文字列 (<c>SHA-256: …</c> を改行で並べる)。</summary>
    public string HashSummary => HashState == EvidenceHashState.NotComputed && Hashes.Count == 0
        ? string.Empty
        : string.Join('\n', Hashes.Select(h => $"{h.Algorithm}: {h.Value}"));

    /// <summary>ディスク・ボリューム (物理デバイス) か。出力先の検査の対象になる。</summary>
    public bool IsDevice => Kind is EvidenceKind.PhysicalDisk or EvidenceKind.Volume;
}

/// <summary>「証拠を追加」で登録する内容。イメージ作成 (FOR-20) などは、計算済みのハッシュを入れて登録する。</summary>
public sealed record EvidenceRegistration
{
    public required EvidenceKind Kind { get; init; }

    public required string Path { get; init; }

    public string Description { get; init; } = string.Empty;

    /// <summary>証拠番号を指定する (通常は null で <c>E001</c> からの連番)。</summary>
    public string? Id { get; init; }

    public string? Model { get; init; }

    public string? Serial { get; init; }

    public long? CapacityBytes { get; init; }

    public long? SectorCount { get; init; }

    public int? SectorSize { get; init; }

    public IReadOnlyList<EvidenceHash> Hashes { get; init; } = [];

    public string? DerivedFrom { get; init; }
}

/// <summary>ケースの中身 (<c>case.json</c>)。</summary>
internal sealed record CaseFile
{
    public int Version { get; init; } = 1;

    public CaseInfo Case { get; init; } = new();

    public List<EvidenceItem> Evidence { get; init; } = [];
}

/// <summary>入力の誤り (ケース番号・ケース名が空など)。<see cref="Fields"/> は誤りのある欄の名前。</summary>
public sealed class CaseValidationException(IReadOnlyList<string> fields) : Exception("The case information is incomplete.")
{
    public IReadOnlyList<string> Fields { get; } = fields;
}

/// <summary>ケースのフォルダに書けない・既にケースがあるなど。ケースは作らない。</summary>
public sealed class CaseFolderException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary><c>case.json</c> が読めない (「ケースファイルを読み込めません」。行番号付き)。ファイルは変更しない。</summary>
public sealed class CaseFileException(string path, long line, string detail) : IOException($"{path}:{line}: {detail}")
{
    public string FilePath { get; } = path;

    /// <summary>誤りのある行 (1 から)。分からなければ 1。</summary>
    public long Line { get; } = line;

    public string Detail { get; } = detail;
}

/// <summary>既に登録されているデータソースを再登録しようとした (既存の証拠番号を示して止める)。</summary>
public sealed class DuplicateEvidenceException(string existingId) : InvalidOperationException($"This source is already registered as {existingId}.")
{
    public string ExistingId { get; } = existingId;
}

/// <summary>
/// 1 つのケース (FOR-03)。フォルダに <c>case.json</c>、<c>audit.log</c>、<c>exports/</c>、<c>reports/</c> を持つ。変更のたびに
/// <c>case.json</c> を一時ファイル経由で保存する (仕様 8)。操作は監査ログ (FOR-04) に記録する。
/// </summary>
public sealed class ForensicCase
{
    public const string CaseFileName = "case.json";
    public const string AuditFileName = "audit.log";
    public const string ExportsFolderName = "exports";
    public const string ReportsFolderName = "reports";
    public const string ImagesFolderName = "images";

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly string _user;
    private CaseFile _file;

    private ForensicCase(string folder, CaseFile file, TimeProvider time, string user)
    {
        Folder = folder;
        _file = file;
        _time = time;
        _user = user;
    }

    public string Folder { get; }

    public string CaseFilePath => System.IO.Path.Combine(Folder, CaseFileName);

    public string AuditPath => System.IO.Path.Combine(Folder, AuditFileName);

    public string ExportsFolder => System.IO.Path.Combine(Folder, ExportsFolderName);

    public string ReportsFolder => System.IO.Path.Combine(Folder, ReportsFolderName);

    /// <summary>イメージの保存先の既定 (任意の場所に保存してもよい)。</summary>
    public string ImagesFolder => System.IO.Path.Combine(Folder, ImagesFolderName);

    /// <summary>カービングの書き出し先の既定 (<c>exports/carved/</c>)。</summary>
    public string CarvedFolder => System.IO.Path.Combine(ExportsFolder, "carved");

    public CaseInfo Info
    {
        get
        {
            lock (_gate)
            {
                return _file.Case;
            }
        }
    }

    /// <summary>除外したものも含む全部の証拠 (登録の順)。</summary>
    public IReadOnlyList<EvidenceItem> AllEvidence
    {
        get
        {
            lock (_gate)
            {
                return [.. _file.Evidence];
            }
        }
    }

    /// <summary>除外していない証拠 (証拠パネルに出すもの)。</summary>
    public IReadOnlyList<EvidenceItem> Evidence
    {
        get
        {
            lock (_gate)
            {
                return [.. _file.Evidence.Where(e => !e.Excluded)];
            }
        }
    }

    /// <summary>ケースの変更 (証拠の追加・除外・ハッシュ・検証結果、ケース情報の変更)。</summary>
    public event EventHandler? Changed;

    /// <summary>ローカルの時刻の解釈に使うタイムゾーン (ケースのタイムゾーン。FOR-05 の仕様 3)。</summary>
    public TimeZoneInfo TimeZone => Time.ForensicTimeContext.ResolveZone(Info.TimeZoneId);

    // ---- 作る・開く ----

    /// <summary>新しいケースを作る。書き込めなければ <see cref="CaseFolderException"/> (ケースは作らない)。</summary>
    public static ForensicCase Create(string folder, CaseInfo info, string user, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        IReadOnlyList<string> invalid = info.Validate();
        if (invalid.Count > 0)
        {
            throw new CaseValidationException(invalid);
        }

        folder = System.IO.Path.GetFullPath(folder);
        string casePath = System.IO.Path.Combine(folder, CaseFileName);
        if (File.Exists(casePath))
        {
            throw new CaseFolderException("A case already exists in this folder.");
        }

        try
        {
            Directory.CreateDirectory(folder);
            Directory.CreateDirectory(System.IO.Path.Combine(folder, ExportsFolderName));
            Directory.CreateDirectory(System.IO.Path.Combine(folder, ReportsFolderName));
            var file = new CaseFile
            {
                Case = info with
                {
                    CreatedUtc = time.GetUtcNow().UtcDateTime,
                    TimeZoneId = string.IsNullOrWhiteSpace(info.TimeZoneId) ? TimeZoneInfo.Local.Id : info.TimeZoneId,
                },
            };
            var created = new ForensicCase(folder, file, time, user);
            created.Save(file);
            File.WriteAllBytes(created.AuditPath, []);
            return created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CaseFolderException(ex.Message, ex);
        }
    }

    /// <summary>ケースを開く。<c>case.json</c> が壊れていれば <see cref="CaseFileException"/> (ファイルは変更しない)。</summary>
    public static ForensicCase Open(string folder, string user, TimeProvider? time = null)
    {
        folder = System.IO.Path.GetFullPath(folder);
        string path = System.IO.Path.Combine(folder, CaseFileName);
        CaseFile file;
        try
        {
            string text = File.ReadAllText(path);
            file = JsonSerializer.Deserialize<CaseFile>(text, JsonFile.Options) ?? throw new CaseFileException(path, 1, "The file is empty.");
            if (file.Version < 1 || file.Case is null || file.Evidence is null)
            {
                throw new CaseFileException(path, 1, "The case data is missing.");
            }

            if (file.Case.Validate().Count > 0)
            {
                throw new CaseFileException(path, 1, "The case number or name is missing.");
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EvidenceItem e in file.Evidence)
            {
                if (string.IsNullOrWhiteSpace(e.Id) || !ids.Add(e.Id))
                {
                    throw new CaseFileException(path, 1, "An evidence number is missing or duplicated.");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new CaseFileException(path, (ex.LineNumber ?? 0) + 1, ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            throw new CaseFolderException("There is no case in this folder.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new CaseFolderException("There is no case in this folder.", ex);
        }

        return new ForensicCase(folder, file, time ?? TimeProvider.System, user);
    }

    // ---- 保存 ----

    private void Save(CaseFile file)
    {
        try
        {
            JsonFile.WriteText(CaseFilePath, JsonSerializer.Serialize(file, JsonFile.Options));
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    private void Commit(CaseFile updated)
    {
        // 保存に成功してから置き換える (書けなければメモリの状態も変えない)。
        Save(updated);
        _file = updated;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ---- 証拠 ----

    /// <summary>同じデータソースの登録を探す (除外したものは対象外)。</summary>
    public EvidenceItem? FindBySource(EvidenceKind kind, string path)
    {
        string key = SourceKey(kind, path);
        lock (_gate)
        {
            return _file.Evidence.FirstOrDefault(e => !e.Excluded && SourceKey(e.Kind, e.Path) == key);
        }
    }

    /// <summary>データソースの比較用の正規形 (ファイルは完全パス、デバイスは大文字、大文字小文字は区別しない)。</summary>
    public static string SourceKey(EvidenceKind kind, string path)
    {
        string p = path.Trim();
        if (kind is EvidenceKind.ImageFile or EvidenceKind.File)
        {
            // 別名のパス (\\?\、ジャンクション、短い名前) でも同じ証拠と分かるようにそろえる。
            p = Files.CanonicalPath.Normalize(p);
        }
        else
        {
            p = p.TrimEnd('\\');
        }

        return p.ToUpperInvariant();
    }

    private static string NextId(IEnumerable<EvidenceItem> items)
    {
        int max = 0;
        foreach (EvidenceItem item in items)
        {
            if (item.Id.Length > 1 && item.Id[0] == 'E' && int.TryParse(item.Id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                max = Math.Max(max, n);
            }
        }

        return "E" + (max + 1).ToString("D3", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 証拠を登録する。既に登録されているデータソースなら <see cref="DuplicateEvidenceException"/>。ハッシュを渡さなければ
    /// <see cref="EvidenceHashState.NotComputed"/> (計算は呼び出し側が行い、<see cref="SetHashes"/> で記録する)。
    /// </summary>
    public EvidenceItem AddEvidence(EvidenceRegistration registration)
    {
        EvidenceItem item;
        lock (_gate)
        {
            EvidenceItem? existing = FindBySource(registration.Kind, registration.Path);
            if (existing is not null)
            {
                throw new DuplicateEvidenceException(existing.Id);
            }

            string id = registration.Id ?? NextId(_file.Evidence);
            if (_file.Evidence.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DuplicateEvidenceException(id);
            }

            DateTime now = _time.GetUtcNow().UtcDateTime;
            item = new EvidenceItem
            {
                Id = id,
                Description = registration.Description,
                Kind = registration.Kind,
                Path = registration.Path,
                Model = registration.Model,
                Serial = registration.Serial,
                CapacityBytes = registration.CapacityBytes,
                SectorCount = registration.SectorCount,
                SectorSize = registration.SectorSize,
                Hashes = registration.Hashes,
                HashState = registration.Hashes.Count > 0 ? EvidenceHashState.Computed : EvidenceHashState.NotComputed,
                HashedUtc = registration.Hashes.Count > 0 ? now : null,
                AddedUtc = now,
                AddedBy = _user,
                DerivedFrom = registration.DerivedFrom,
            };
            Commit(_file with { Evidence = [.. _file.Evidence, item] });
        }

        RaiseChanged();
        return item;
    }

    /// <summary>証拠を取り出す (除外したものを含む)。</summary>
    public EvidenceItem? Find(string id)
    {
        lock (_gate)
        {
            return _file.Evidence.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>一覧から外す (データソースには何もしない)。<c>case.json</c> には「除外」の印を付けて残す (仕様 7)。外せたか。</summary>
    public bool Exclude(string id)
    {
        lock (_gate)
        {
            EvidenceItem? item = Find(id);
            if (item is null || item.Excluded)
            {
                return false;
            }

            Replace(item with { Excluded = true, ExcludedUtc = _time.GetUtcNow().UtcDateTime, ExcludedBy = _user });
        }

        RaiseChanged();
        return true;
    }

    /// <summary>ハッシュの計算結果を記録する。</summary>
    public EvidenceItem? SetHashes(string id, IReadOnlyList<EvidenceHash> hashes)
    {
        EvidenceItem? updated;
        lock (_gate)
        {
            EvidenceItem? item = Find(id);
            if (item is null)
            {
                return null;
            }

            updated = item with { Hashes = hashes, HashState = hashes.Count > 0 ? EvidenceHashState.Computed : EvidenceHashState.NotComputed, HashedUtc = _time.GetUtcNow().UtcDateTime };
            Replace(updated);
        }

        RaiseChanged();
        return updated;
    }

    /// <summary>検証の結果 (FOR-22)。<paramref name="passed"/> が null なら未検証に戻す。</summary>
    public EvidenceItem? SetVerification(string id, bool? passed)
    {
        EvidenceItem? updated;
        lock (_gate)
        {
            EvidenceItem? item = Find(id);
            if (item is null)
            {
                return null;
            }

            updated = item with
            {
                Verification = passed is null ? null : passed.Value ? "passed" : "failed",
                VerifiedUtc = passed is null ? null : _time.GetUtcNow().UtcDateTime,
            };
            Replace(updated);
        }

        RaiseChanged();
        return updated;
    }

    /// <summary>説明を変える。</summary>
    public EvidenceItem? SetDescription(string id, string description)
    {
        EvidenceItem? updated;
        lock (_gate)
        {
            EvidenceItem? item = Find(id);
            if (item is null)
            {
                return null;
            }

            updated = item with { Description = description };
            Replace(updated);
        }

        RaiseChanged();
        return updated;
    }

    /// <summary>ケース情報を変える (タイムゾーンなど)。作成日時は変えない。</summary>
    public void UpdateInfo(CaseInfo info)
    {
        IReadOnlyList<string> invalid = info.Validate();
        if (invalid.Count > 0)
        {
            throw new CaseValidationException(invalid);
        }

        lock (_gate)
        {
            Commit(_file with { Case = info with { CreatedUtc = _file.Case.CreatedUtc } });
        }

        RaiseChanged();
    }

    private void Replace(EvidenceItem updated)
    {
        List<EvidenceItem> list = [.. _file.Evidence];
        int index = list.FindIndex(e => string.Equals(e.Id, updated.Id, StringComparison.OrdinalIgnoreCase));
        list[index] = updated;
        Commit(_file with { Evidence = list });
    }
}
