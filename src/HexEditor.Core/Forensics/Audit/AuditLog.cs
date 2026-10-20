using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Forensics.Audit;

/// <summary>操作の結果 (12 の FOR-04 の仕様 2)。</summary>
public enum AuditResult
{
    Success,
    Failure,
    Cancelled,
}

/// <summary>
/// 監査ログに記録する操作の種類 (FOR-04 の仕様 3)。他の領域 (イメージ作成、消去、クローンなど) もここの名前を使って
/// <see cref="ForensicAudit.Record"/> を呼ぶ。長い処理は <c>phase</c> パラメーターに <c>start</c> / <c>end</c> を入れた 2 行で記録する (<see cref="ForensicAudit.Begin"/>)。
/// </summary>
public static class AuditOperations
{
    public const string CaseCreate = "case.create";
    public const string CaseOpen = "case.open";
    public const string CaseClose = "case.close";
    public const string EvidenceAdd = "evidence.add";
    public const string EvidenceExclude = "evidence.exclude";
    public const string EvidenceVerify = "evidence.verify";
    public const string DocumentOpen = "document.open";
    public const string DocumentClose = "document.close";
    public const string WriteBlockEnable = "writeblock.enable";
    public const string WriteBlockDisable = "writeblock.disable";
    public const string WriteBlockExceptionSet = "writeblock.exception.set";
    public const string WriteBlockExceptionClear = "writeblock.exception.clear";
    public const string UsbProtectEnable = "usbprotect.enable";
    public const string UsbProtectDisable = "usbprotect.disable";
    public const string HashCompute = "hash.compute";
    public const string ImageCreate = "image.create";
    public const string ImageVerify = "image.verify";
    public const string FileExport = "file.export";
    public const string Carve = "carve.run";
    public const string Save = "write.save";
    public const string Wipe = "write.wipe";
    public const string Clone = "write.clone";
    public const string Restore = "write.restore";
    public const string Redact = "write.redact";

    /// <summary>パーティション表のエントリの作成 (ドキュメントの編集。FOR-08 の仕様 4)。</summary>
    public const string PartitionEntriesCreate = "edit.partitionEntries";
    public const string ReportCreate = "report.create";
    public const string SettingChange = "settings.change";
    public const string AuditVerify = "audit.verify";
    public const string AuditRecovered = "audit.recovered";
    public const string Jump = "browse.jump";
    public const string Search = "browse.search";
    public const string BookmarkAdd = "browse.bookmark";

    /// <summary>閲覧操作 (既定では記録しない。設定「閲覧操作も記録する」。仕様 4)。</summary>
    public static bool IsBrowsing(string operation) => operation.StartsWith("browse.", StringComparison.Ordinal);
}

/// <summary>操作者・コンピューター名・アプリの版 (各行に入れる)。</summary>
public sealed record AuditIdentity(string User, string Computer, string AppVersion)
{
    public static AuditIdentity Current(string appVersion) => new(Environment.UserName, Environment.MachineName, appVersion);
}

/// <summary>記録する 1 件 (連番・ハッシュは書くときに付く)。</summary>
public sealed record AuditRecord(string Operation)
{
    public string? EvidenceId { get; init; }

    public string? Path { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Parameters { get; init; } = [];

    public AuditResult Result { get; init; } = AuditResult.Success;

    public string Message { get; init; } = string.Empty;

    /// <summary>ケースを開いていなくても設定フォルダの <c>forensics-audit.log</c> に記録する (書き込み保護モードの切り替え。仕様 8)。</summary>
    public bool AlwaysGlobal { get; init; }

    public static IReadOnlyList<KeyValuePair<string, string>> Args(params (string Key, string Value)[] pairs) =>
        [.. pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value))];
}

/// <summary>監査ログの 1 行 (読み出した結果)。</summary>
public sealed record AuditEntry(
    long Seq,
    string Time,
    string User,
    string Computer,
    string AppVersion,
    string Operation,
    string? EvidenceId,
    string? Path,
    IReadOnlyList<KeyValuePair<string, string>> Parameters,
    AuditResult Result,
    string Message,
    string PrevHash,
    string Hash)
{
    /// <summary>日時 (UTC)。読めなければ null。</summary>
    public DateTime? TimeUtc => DateTime.TryParseExact(Time, AuditLog.TimeFormat, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime t) ? t : null;

    public string ParametersText => string.Join("; ", Parameters.Select(p => $"{p.Key}={p.Value}"));
}

/// <summary>ログに書けない (ディスク容量不足など)。</summary>
public sealed class AuditWriteException(string message, Exception inner) : IOException(message, inner);

/// <summary>追記の保存先。テストで書き込みエラー・空き容量不足を再現できるよう差し替えられる。</summary>
public interface IAuditStorage
{
    /// <summary>1 行 (末尾の改行を含む UTF-8) をファイルの末尾に追記して、ディスクまで書く。</summary>
    void Append(string path, ReadOnlySpan<byte> line);

    /// <summary>最後の空でない行。ファイルがなければ null。</summary>
    string? ReadLastLine(string path, out bool endsWithNewline);

    /// <summary>書き込めるか (操作を始める前の確認。仕様のエラー欄)。書けなければ例外。</summary>
    void Probe(string path);

    /// <summary>
    /// 最後に書いた行の連番とハッシュ (末尾の印) を残す。末尾の行を消した (切り詰めた) ログを検証で見つけるため。残せなくても追記は成功とする
    /// (印が古いだけなら検証は誤って報告しない)。既定は何もしない (テスト用の保存先)。
    /// </summary>
    void WriteHead(string path, long seq, string hash)
    {
    }
}

/// <summary>ファイルへの保存。</summary>
public sealed class FileAuditStorage : IAuditStorage
{
    /// <summary>空き容量がこれより少なければ書けないとみなす。</summary>
    public const long MinimumFreeBytes = 64 * 1024;

    public static FileAuditStorage Instance { get; } = new();

    public void Append(string path, ReadOnlySpan<byte> line)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(line);
        stream.Flush(flushToDisk: true);
    }

    public string? ReadLastLine(string path, out bool endsWithNewline)
    {
        endsWithNewline = true;
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        if (length == 0)
        {
            return null;
        }

        stream.Seek(-1, SeekOrigin.End);
        endsWithNewline = stream.ReadByte() == '\n';

        // 末尾から 64 KiB ずつ戻って、最後の空でない行の始まりを探す。
        var tail = new List<byte>();
        long position = length;
        while (position > 0)
        {
            int size = (int)Math.Min(64 * 1024, position);
            position -= size;
            stream.Seek(position, SeekOrigin.Begin);
            byte[] block = new byte[size];
            stream.ReadExactly(block);
            tail.InsertRange(0, block);
            string text = Encoding.UTF8.GetString([.. tail]).TrimEnd('\r', '\n');
            int nl = text.LastIndexOf('\n');
            if (nl >= 0)
            {
                return text[(nl + 1)..];
            }

            if (position == 0)
            {
                return text.Length == 0 ? null : text;
            }
        }

        return null;
    }

    /// <summary>末尾の印のファイル (<c>audit.log.head</c>)。</summary>
    public static string HeadPath(string path) => path + ".head";

    public void WriteHead(string path, long seq, string hash)
    {
        try
        {
            File.WriteAllText(HeadPath(path), seq.ToString(CultureInfo.InvariantCulture) + " " + hash + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 印を残せなくても追記は成功している (印が古いだけでは検証は誤って報告しない)。
        }
    }

    /// <summary>末尾の印を読む。なければ・読めなければ null。</summary>
    public static (long Seq, string Hash)? ReadHead(string path)
    {
        try
        {
            string head = HeadPath(path);
            if (!File.Exists(head) || new FileInfo(head).Length > 256)
            {
                return null;
            }

            string[] parts = File.ReadAllText(head).Trim().Split(' ');
            return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long seq) && seq > 0 && parts[1].Length == 64
                ? (seq, parts[1])
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Probe(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null && Path.GetPathRoot(folder) is { Length: > 0 } root)
        {
            try
            {
                if (new DriveInfo(root).AvailableFreeSpace < MinimumFreeBytes)
                {
                    throw new IOException("There is not enough space on the disk.");
                }
            }
            catch (ArgumentException)
            {
                // UNC など DriveInfo で引けないパスは、開けるかどうかだけ確かめる。
            }
        }

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }
}

/// <summary>パラメーターやメッセージに秘密の値 (パスワード・鍵・回復キー) を入れない (FOR-04 の「秘密を記録しない」)。</summary>
public static class AuditRedactor
{
    public const string Mask = "***";

    private static readonly string[] SecretWords =
        ["password", "passwd", "passphrase", "secret", "token", "apikey", "api-key", "privatekey", "private-key", "recoverykey", "recovery-key", "credential", "pin"];

    // BitLocker の回復パスワード (6 桁 × 8 組) と、長い 16 進の鍵らしい値。
    private static readonly Regex RecoveryKey = new(@"\b\d{6}(?:-\d{6}){7}\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSecretKey(string key)
    {
        string k = key.ToLowerInvariant();
        return SecretWords.Any(w => w == "pin" ? k == "pin" || k.EndsWith("_pin", StringComparison.Ordinal) || k.EndsWith(".pin", StringComparison.Ordinal) : k.Contains(w, StringComparison.Ordinal));
    }

    public static string RedactText(string text) => RecoveryKey.Replace(text, Mask);

    public static IReadOnlyList<KeyValuePair<string, string>> Redact(IReadOnlyList<KeyValuePair<string, string>> parameters) =>
        [.. parameters.Select(p => new KeyValuePair<string, string>(p.Key, IsSecretKey(p.Key) ? Mask : RedactText(p.Value)))];
}

/// <summary>
/// 追記専用で改ざんを検出できる監査ログ (FOR-04)。1 行 1 件の JSON Lines (UTF-8)。各行に前の行のハッシュ (<c>prev</c>) と
/// この行自身のハッシュ (<c>hash</c> 以外の内容の SHA-256) を含める。行を編集・削除する手段は持たない。
/// </summary>
public sealed class AuditLog
{
    /// <summary>日時の書式 (UTC、100 ナノ秒まで、末尾は <c>Z</c>)。</summary>
    public const string TimeFormat = "yyyy-MM-dd'T'HH':'mm':'ss'.'fffffff'Z'";

    /// <summary>最初の行の <c>prev</c>。</summary>
    public static readonly string GenesisHash = new('0', 64);

    private const string HashSuffixStart = ",\"hash\":\"";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    private readonly object _gate = new();
    private readonly IAuditStorage _storage;
    private readonly TimeProvider _time;
    private long _lastSeq;
    private string _lastHash = GenesisHash;

    private AuditLog(string path, AuditIdentity identity, TimeProvider time, IAuditStorage storage)
    {
        Path = path;
        Identity = identity;
        _time = time;
        _storage = storage;
    }

    public string Path { get; }

    public AuditIdentity Identity { get; }

    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _lastSeq;
            }
        }
    }

    public string LastHash
    {
        get
        {
            lock (_gate)
            {
                return _lastHash;
            }
        }
    }

    /// <summary>ログを開く (なければ作る)。既存のログなら最後の行から連鎖を続ける。</summary>
    public static AuditLog Open(string path, AuditIdentity identity, TimeProvider? time = null, IAuditStorage? storage = null)
    {
        var log = new AuditLog(path, identity, time ?? TimeProvider.System, storage ?? FileAuditStorage.Instance);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        string? last = log._storage.ReadLastLine(path, out bool endsWithNewline);
        if (last is not null)
        {
            log._needsLeadingNewline = !endsWithNewline;
            if (TryParse(last, out AuditEntry? entry) && entry is not null)
            {
                log._lastSeq = entry.Seq;
                log._lastHash = entry.Hash;
            }
            else
            {
                // 壊れた最後の行: 行数を数えて連番を続け、行全体のハッシュで連鎖をつなぐ (検証で壊れた行として報告される)。
                log._lastSeq = CountLines(path);
                log._lastHash = Sha256Hex(Encoding.UTF8.GetBytes(last));
            }
        }
        else
        {
            // 新しいログ (または空のファイル): ファイルを作っておく。
            log._storage.Append(path, []);
        }

        return log;
    }

    private bool _needsLeadingNewline;

    /// <summary>1 件を追記して、書いた行を返す。書けなければ <see cref="AuditWriteException"/>。</summary>
    public AuditEntry Append(AuditRecord record, DateTime? timeUtc = null)
    {
        lock (_gate)
        {
            DateTime now = (timeUtc ?? _time.GetUtcNow().UtcDateTime).ToUniversalTime();
            long seq = _lastSeq + 1;
            IReadOnlyList<KeyValuePair<string, string>> parameters = AuditRedactor.Redact(record.Parameters);
            string message = AuditRedactor.RedactText(record.Message);
            string time = now.ToString(TimeFormat, CultureInfo.InvariantCulture);
            string body = Body(seq, time, Identity, record, parameters, message, _lastHash);
            string hash = Sha256Hex(Encoding.UTF8.GetBytes(body));
            string line = body + HashSuffixStart + hash + "\"}";
            byte[] bytes = Encoding.UTF8.GetBytes((_needsLeadingNewline ? "\n" : string.Empty) + line + "\n");
            try
            {
                _storage.Append(Path, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AuditWriteException(ex.Message, ex);
            }

            _needsLeadingNewline = false;
            _storage.WriteHead(Path, seq, hash);
            var entry = new AuditEntry(seq, time, Identity.User, Identity.Computer, Identity.AppVersion, record.Operation, record.EvidenceId, record.Path,
                parameters, record.Result, message, _lastHash, hash);
            _lastSeq = seq;
            _lastHash = hash;
            return entry;
        }
    }

    /// <summary>書き込めるかを確かめる (操作を始める前)。書けなければ <see cref="AuditWriteException"/>。</summary>
    public void Probe()
    {
        try
        {
            _storage.Probe(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuditWriteException(ex.Message, ex);
        }
    }

    // ---- 行の形式 ----

    private static string ResultText(AuditResult result) => result switch
    {
        AuditResult.Failure => "failure",
        AuditResult.Cancelled => "cancelled",
        _ => "success",
    };

    private static string Body(long seq, string time, AuditIdentity id, AuditRecord record, IReadOnlyList<KeyValuePair<string, string>> parameters,
        string message, string prev)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", seq);
            w.WriteString("time", time);
            w.WriteString("user", id.User);
            w.WriteString("computer", id.Computer);
            w.WriteString("app", id.AppVersion);
            w.WriteString("op", record.Operation);
            if (record.EvidenceId is not null)
            {
                w.WriteString("evidence", record.EvidenceId);
            }

            if (record.Path is not null)
            {
                w.WriteString("path", record.Path);
            }

            w.WriteStartObject("params");
            foreach (KeyValuePair<string, string> p in parameters)
            {
                w.WriteString(p.Key, p.Value);
            }

            w.WriteEndObject();
            w.WriteString("result", ResultText(record.Result));
            w.WriteString("message", message);
            w.WriteString("prev", prev);
            w.WriteEndObject();
        }

        // 最後の '}' を除いたもの (続けて ,"hash":"…"} を付ける)。
        byte[] json = stream.ToArray();
        return Encoding.UTF8.GetString(json, 0, json.Length - 1);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static long CountLines(string path)
    {
        long count = 0;
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length > 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>1 行を読む。形式が違えば false。ハッシュが合っているかは調べない。</summary>
    public static bool TryParse(string line, out AuditEntry? entry)
    {
        entry = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string Str(string name) => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty;
            string? Opt(string name) => root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (!root.TryGetProperty("seq", out JsonElement seqElement) || !seqElement.TryGetInt64(out long seq))
            {
                return false;
            }

            var parameters = new List<KeyValuePair<string, string>>();
            if (root.TryGetProperty("params", out JsonElement p) && p.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in p.EnumerateObject())
                {
                    parameters.Add(new(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.ToString()));
                }
            }

            AuditResult result = Str("result") switch
            {
                "failure" => AuditResult.Failure,
                "cancelled" => AuditResult.Cancelled,
                _ => AuditResult.Success,
            };
            entry = new AuditEntry(seq, Str("time"), Str("user"), Str("computer"), Str("app"), Str("op"), Opt("evidence"), Opt("path"), parameters, result,
                Str("message"), Str("prev"), Str("hash"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>行の <c>hash</c> を除いた内容のハッシュを求める。行が <c>,"hash":"…"}</c> で終わっていなければ null。</summary>
    internal static (string Computed, string Recorded)? HashOf(string line)
    {
        const int suffixLength = 9 + 64 + 2;
        if (line.Length <= suffixLength || !line.EndsWith("\"}", StringComparison.Ordinal))
        {
            return null;
        }

        int start = line.Length - suffixLength;
        if (string.CompareOrdinal(line, start, HashSuffixStart, 0, HashSuffixStart.Length) != 0)
        {
            return null;
        }

        string recorded = line.Substring(start + HashSuffixStart.Length, 64);
        return (Sha256Hex(Encoding.UTF8.GetBytes(line[..start])), recorded);
    }
}
