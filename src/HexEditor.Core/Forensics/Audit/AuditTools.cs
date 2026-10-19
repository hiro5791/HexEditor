using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HexEditor.Core.Forensics.Time;

namespace HexEditor.Core.Forensics.Audit;

/// <summary>検証で見つかった連鎖の切れの種類。</summary>
public enum AuditBreakKind
{
    /// <summary>行が JSON Lines の形式でない (空行の挿入、途中で切れた行など)。</summary>
    Format,

    /// <summary>行の内容が記録されたハッシュと合わない (行の編集)。</summary>
    HashMismatch,

    /// <summary>前の行のハッシュと <c>prev</c> が合わない (行の削除・入れ替え)。</summary>
    ChainBroken,

    /// <summary>連番が続かない。</summary>
    SequenceGap,
}

/// <summary>連鎖の切れた行。<see cref="Line"/> は 1 から数えた行番号。</summary>
public sealed record AuditBreak(long Line, AuditBreakKind Kind, string Detail);

/// <summary>検証の結果。</summary>
public sealed record AuditVerifyResult(long LinesChecked, IReadOnlyList<AuditBreak> Breaks, bool Truncated)
{
    public bool IsIntact => Breaks.Count == 0;

    /// <summary>切れた最初の行 (なければ null)。</summary>
    public long? FirstBrokenLine => Breaks.Count == 0 ? null : Breaks[0].Line;
}

/// <summary>監査ログの検証 (FOR-04 の仕様 6)。全行のハッシュの連鎖を確かめる。1 行ずつ読み、メモリは行数に比例しない。</summary>
public static class AuditVerifier
{
    /// <summary>報告する切れの数の上限 (大量に壊れたログでメモリを使わないため)。</summary>
    public const int MaxBreaks = 1000;

    public static AuditVerifyResult Verify(string path, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        var breaks = new List<AuditBreak>();
        long line = 0;
        string? previousHash = AuditLog.GenesisHash;
        long expectedSeq = 1;
        bool truncated = false;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, 64 * 1024);
        while (reader.ReadLine() is { } text)
        {
            line++;
            if ((line & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(line);
            }

            AuditBreak? broken = Check(text, line, ref previousHash, ref expectedSeq);
            if (broken is not null)
            {
                if (breaks.Count >= MaxBreaks)
                {
                    truncated = true;
                }
                else
                {
                    breaks.Add(broken);
                }
            }
        }

        progress?.Report(line);
        return new AuditVerifyResult(line, breaks, truncated);
    }

    private static AuditBreak? Check(string text, long line, ref string? previousHash, ref long expectedSeq)
    {
        (string Computed, string Recorded)? hashes = AuditLog.HashOf(text);
        if (hashes is null || !AuditLog.TryParse(text, out AuditEntry? entry) || entry is null)
        {
            // 形式が違う行: この行のハッシュは分からないので、次の行の prev は調べない (1 か所の改ざんで連鎖の報告があふれないように)。
            previousHash = null;
            return new AuditBreak(line, AuditBreakKind.Format, "The line is not a valid audit entry.");
        }

        AuditBreak? result = null;
        if (hashes.Value.Computed != hashes.Value.Recorded || entry.Hash != hashes.Value.Recorded)
        {
            result = new AuditBreak(line, AuditBreakKind.HashMismatch, "The content does not match its hash.");
        }
        else if (previousHash is not null && entry.PrevHash != previousHash)
        {
            result = new AuditBreak(line, AuditBreakKind.ChainBroken, "The previous-line hash does not match.");
        }
        else if (entry.Seq != expectedSeq)
        {
            result = new AuditBreak(line, AuditBreakKind.SequenceGap, $"Expected sequence {expectedSeq.ToString(CultureInfo.InvariantCulture)}.");
        }

        previousHash = hashes.Value.Recorded;
        expectedSeq = entry.Seq + 1;
        return result;
    }
}

/// <summary>
/// 監査ログの読み出し (パネルの一覧)。行の位置の索引だけをメモリに持ち (1 行 8 バイト)、行は必要になったときに読むので、
/// ログが 100 万行を超えても一覧を仮想化して表示できる (FOR-04 の「巨大ファイル・長時間処理」)。ログに追記されたら <see cref="Refresh"/> で続きを索引する。
/// </summary>
public sealed class AuditLogReader : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly List<long> _starts = [];
    private long _indexedUntil;

    public AuditLogReader(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        Refresh();
    }

    public string Path { get; }

    /// <summary>行数。</summary>
    public long Count
    {
        get
        {
            lock (_gate)
            {
                return _starts.Count;
            }
        }
    }

    /// <summary>追記された行を索引に加える。加えた行数を返す。</summary>
    public int Refresh(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            int before = _starts.Count;
            long length = _stream.Length;
            byte[] buffer = new byte[256 * 1024];
            long position = _indexedUntil;
            bool atLineStart = true;
            if (position > 0)
            {
                // 最後の行が改行で終わっていなかった場合は、その行の続きとして扱い直す (索引の最後の行を取り直す)。
                _stream.Seek(position - 1, SeekOrigin.Begin);
                atLineStart = _stream.ReadByte() == '\n';
                if (!atLineStart && _starts.Count > 0)
                {
                    position = _starts[^1];
                    _starts.RemoveAt(_starts.Count - 1);
                    before--;
                    atLineStart = true;
                }
            }

            _stream.Seek(position, SeekOrigin.Begin);
            while (position < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = _stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - position));
                if (read <= 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    if (atLineStart)
                    {
                        _starts.Add(position + i);
                        atLineStart = false;
                    }

                    if (buffer[i] == '\n')
                    {
                        atLineStart = true;
                    }
                }

                position += read;
            }

            // 最後の「行の始まり」だけで中身のない位置 (ファイルの末尾が改行) は行にしない。
            _indexedUntil = position;
            return _starts.Count - before;
        }
    }

    /// <summary>行の文字列 (改行を除く)。</summary>
    public string ReadLine(long index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _starts.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            long start = _starts[(int)index];
            long end = index + 1 < _starts.Count ? _starts[(int)index + 1] : _indexedUntil;
            int length = (int)Math.Min(end - start, 16 * 1024 * 1024);
            byte[] bytes = new byte[length];
            _stream.Seek(start, SeekOrigin.Begin);
            _stream.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
        }
    }

    /// <summary>行の内容。形式が違う行 (改ざんなど) は null。</summary>
    public AuditEntry? Read(long index) => AuditLog.TryParse(ReadLine(index), out AuditEntry? entry) ? entry : null;

    /// <summary>全行を順に読む (期間の指定があれば絞る)。形式が違う行は飛ばす。</summary>
    public IEnumerable<AuditEntry> Enumerate(DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        long count = Count;
        for (long i = 0; i < count; i++)
        {
            if (Read(i) is not { } entry)
            {
                continue;
            }

            DateTime? t = entry.TimeUtc;
            if ((fromUtc is not null && t < fromUtc) || (toUtc is not null && t > toUtc))
            {
                continue;
            }

            yield return entry;
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>監査ログの CSV / JSON への書き出し (FOR-04 の仕様 7)。日時の列は UTC とローカル (設定が「UTC」ならローカルなし)。</summary>
public static class AuditExporter
{
    private static readonly JsonWriterOptions JsonOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string CsvEscape(string value)
    {
        // CSV インジェクション対策: 式として解釈される先頭の文字の前に ' を付ける。
        if (value.Length > 0 && value[0] is '=' or '+' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    public static string ResultName(AuditResult result) => result switch
    {
        AuditResult.Failure => "failure",
        AuditResult.Cancelled => "cancelled",
        _ => "success",
    };

    public static void WriteCsv(TextWriter writer, IEnumerable<AuditEntry> entries, ForensicTimeContext context, string invalidText = "invalid")
    {
        var header = new List<string> { "seq" };
        bool utc = context.Columns.Contains(TimeColumn.Utc), local = context.Columns.Contains(TimeColumn.Local);
        if (utc)
        {
            header.Add("time_utc");
        }

        if (local)
        {
            header.Add("time_local");
        }

        header.AddRange(["user", "computer", "app", "operation", "evidence", "path", "parameters", "result", "message", "prev_hash", "hash"]);
        writer.Write(string.Join(',', header));
        writer.Write("\r\n");
        foreach (AuditEntry e in entries)
        {
            var cells = new List<string> { e.Seq.ToString(CultureInfo.InvariantCulture) };
            ForensicTimestamp ts = e.TimeUtc is { } t ? ForensicTimestamp.FromUtc(t, 1) : ForensicTimestamp.Invalid(e.Time);
            if (utc)
            {
                cells.Add(e.Time);
            }

            if (local)
            {
                cells.Add(context.Cell(ts, TimeColumn.Local, invalidText));
            }

            cells.AddRange([e.User, e.Computer, e.AppVersion, e.Operation, e.EvidenceId ?? string.Empty, e.Path ?? string.Empty, e.ParametersText,
                ExportResult(e), e.Message, e.PrevHash, e.Hash]);
            writer.Write(string.Join(',', cells.Select(CsvEscape)));
            writer.Write("\r\n");
        }
    }

    private static string ExportResult(AuditEntry e) => ResultName(e.Result);

    public static void WriteJson(Stream output, IEnumerable<AuditEntry> entries, ForensicTimeContext context)
    {
        using var w = new Utf8JsonWriter(output, JsonOptions);
        w.WriteStartArray();
        foreach (AuditEntry e in entries)
        {
            w.WriteStartObject();
            w.WriteNumber("seq", e.Seq);
            w.WriteString("time", e.Time);
            if (context.Columns.Contains(TimeColumn.Local))
            {
                ForensicTimestamp ts = e.TimeUtc is { } t ? ForensicTimestamp.FromUtc(t, 1) : ForensicTimestamp.Invalid(e.Time);
                w.WriteString("timeLocal", context.Cell(ts, TimeColumn.Local, "invalid"));
            }

            w.WriteString("user", e.User);
            w.WriteString("computer", e.Computer);
            w.WriteString("app", e.AppVersion);
            w.WriteString("operation", e.Operation);
            if (e.EvidenceId is not null)
            {
                w.WriteString("evidence", e.EvidenceId);
            }

            if (e.Path is not null)
            {
                w.WriteString("path", e.Path);
            }

            w.WriteStartObject("parameters");
            foreach (KeyValuePair<string, string> p in e.Parameters)
            {
                w.WriteString(p.Key, p.Value);
            }

            w.WriteEndObject();
            w.WriteString("result", ResultName(e.Result));
            w.WriteString("message", e.Message);
            w.WriteString("prevHash", e.PrevHash);
            w.WriteString("hash", e.Hash);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}
