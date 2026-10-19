using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.Time;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>固定した時刻を返す時計 (「時刻の固定」。テスト方針 7.2)。</summary>
internal sealed class FixedTime(DateTime utc) : TimeProvider
{
    private DateTime _utc = utc;

    public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(_utc, DateTimeKind.Utc));

    public void Advance(TimeSpan by) => _utc += by;
}

/// <summary>書き込みを失敗させられる保存先。</summary>
internal sealed class FlakyStorage : IAuditStorage
{
    private readonly FileAuditStorage _inner = FileAuditStorage.Instance;

    public bool Failing { get; set; }

    public void Append(string path, ReadOnlySpan<byte> line)
    {
        if (Failing)
        {
            throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        }

        _inner.Append(path, line);
    }

    public string? ReadLastLine(string path, out bool endsWithNewline) => _inner.ReadLastLine(path, out endsWithNewline);

    public void Probe(string path)
    {
        if (Failing)
        {
            throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        }

        _inner.Probe(path);
    }
}

/// <summary>FOR-04 監査ログ。</summary>
public sealed class AuditLogTests : IDisposable
{
    private static readonly AuditIdentity Identity = new("tester", "TESTPC", "1.2.3");

    private static readonly DateTime Fixed1 = new(2026, 10, 7, 3, 15, 42, DateTimeKind.Utc);

    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-audit").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string LogPath => Path.Combine(_dir, "audit.log");

    private static AuditRecord Rec(string op, string message = "") => new(op) { Message = message };

    [Fact]
    [Trait(TC, "TC-FOR-04-01")]
    public void ImageCreationIsRecordedAsStartAndEndWithTheComputedHashes()
    {
        string file = TestDataCatalog.Generate("TD-SEQ-1M", _dir);
        byte[] data = File.ReadAllBytes(file);
        string md5 = Convert.ToHexStringLower(MD5.HashData(data)), sha256 = Convert.ToHexStringLower(SHA256.HashData(data));
        var audit = new ForensicAudit(Identity, new FixedTime(Fixed1));
        audit.AttachCase(AuditLog.Open(LogPath, Identity, new FixedTime(Fixed1)));

        using (AuditScope scope = audit.Begin(new AuditRecord(AuditOperations.ImageCreate) { Path = Path.Combine(_dir, "images", "seq.dd") }))
        {
            scope.Complete(AuditResult.Success, string.Empty, AuditRecord.Args(("MD5", md5), ("SHA-256", sha256)));
        }

        string[] lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        AuditLog.TryParse(lines[0], out AuditEntry? start);
        AuditLog.TryParse(lines[1], out AuditEntry? end);
        Assert.Equal(AuditOperations.ImageCreate, start!.Operation);
        Assert.Equal(AuditOperations.ImageCreate, end!.Operation);
        Assert.Contains(start.Parameters, p => p is { Key: "phase", Value: "start" });
        Assert.Contains(end.Parameters, p => p is { Key: "phase", Value: "end" });
        Assert.Equal(md5, end.Parameters.Single(p => p.Key == "MD5").Value);
        Assert.Equal(sha256, end.Parameters.Single(p => p.Key == "SHA-256").Value);
    }

    private void WriteTwentyEntries(AuditLog log)
    {
        for (int i = 0; i < 10; i++)
        {
            log.Append(new AuditRecord(AuditOperations.DocumentOpen) { Path = "TD-SEQ-1M.bin", Message = $"Opened file number {i}" });
            log.Append(new AuditRecord(AuditOperations.DocumentClose) { Path = "TD-SEQ-1M.bin", Message = $"Closed file number {i}" });
        }
    }

    [Fact]
    [Trait(TC, "TC-FOR-04-02")]
    public void ChangingOneCharacterOfALineIsReportedWithItsLineNumber()
    {
        var time = new FixedTime(Fixed1);
        AuditLog log = AuditLog.Open(LogPath, Identity, time);
        WriteTwentyEntries(log);
        AuditVerifyResult intact = AuditVerifier.Verify(LogPath);
        Assert.True(intact.IsIntact);
        Assert.Equal(20, intact.LinesChecked);

        // 7 行目のメッセージの 1 文字を別の文字に置き換える (行の長さは変えない)。
        string[] lines = File.ReadAllLines(LogPath);
        int at = lines[6].IndexOf("\"message\":\"", StringComparison.Ordinal) + "\"message\":\"".Length;
        char[] chars = lines[6].ToCharArray();
        chars[at] = chars[at] == 'X' ? 'Y' : 'X';
        lines[6] = new string(chars);
        File.WriteAllText(LogPath, string.Join('\n', lines) + "\n");

        AuditVerifyResult tampered = AuditVerifier.Verify(LogPath);
        Assert.False(tampered.IsIntact);
        Assert.Equal(7, tampered.FirstBrokenLine);
        Assert.Single(tampered.Breaks);
        Assert.Equal(AuditBreakKind.HashMismatch, tampered.Breaks[0].Kind);

        // 検証で切れが見つかったことを新しい行として記録する (その行自体は正しい連鎖に続く)。
        var audit = new ForensicAudit(Identity, time);
        AuditLog reopened = AuditLog.Open(LogPath, Identity, time);
        audit.AttachCase(reopened);
        audit.Record(new AuditRecord(AuditOperations.AuditVerify) { Result = AuditResult.Failure, Message = "Chain broken at line 7." });
        string last = File.ReadAllLines(LogPath)[^1];
        AuditLog.TryParse(last, out AuditEntry? entry);
        Assert.Equal(AuditOperations.AuditVerify, entry!.Operation);
        Assert.Equal(AuditResult.Failure, entry.Result);
        Assert.Equal(7, AuditVerifier.Verify(LogPath).FirstBrokenLine);
    }

    [Fact]
    [Trait(TC, "TC-FOR-04-02")]
    public void DeletedLinesAndInsertedLinesAndRewrittenHashesAreAllDetected()
    {
        AuditLog log = AuditLog.Open(LogPath, Identity, new FixedTime(Fixed1));
        WriteTwentyEntries(log);
        string[] original = File.ReadAllLines(LogPath);

        // 行の削除: 削除した次の行 (新しい 7 行目) で連鎖が切れる。
        File.WriteAllText(LogPath, string.Join('\n', original.Where((_, i) => i != 6)) + "\n");
        AuditVerifyResult deleted = AuditVerifier.Verify(LogPath);
        Assert.Equal(7, deleted.FirstBrokenLine);
        Assert.Contains(deleted.Breaks, b => b.Kind == AuditBreakKind.ChainBroken);

        // 空行の挿入。
        File.WriteAllText(LogPath, string.Join('\n', original.Take(5).Append(string.Empty).Concat(original.Skip(5))) + "\n");
        Assert.Equal(6, AuditVerifier.Verify(LogPath).FirstBrokenLine);

        // 行のハッシュを書き換えて辻褄を合わせても、次の行の prev が合わない。
        string[] forged = (string[])original.Clone();
        string body = forged[3][..forged[3].LastIndexOf(",\"hash\":\"", StringComparison.Ordinal)].Replace("Closed", "Closed!", StringComparison.Ordinal);
        forged[3] = body + ",\"hash\":\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))) + "\"}";
        File.WriteAllText(LogPath, string.Join('\n', forged) + "\n");
        Assert.Equal(5, AuditVerifier.Verify(LogPath).FirstBrokenLine);

        // 元に戻せば検証を通る。
        File.WriteAllText(LogPath, string.Join('\n', original) + "\n");
        Assert.True(AuditVerifier.Verify(LogPath).IsIntact);
    }

    [Theory]
    [Trait(TC, "TC-FOR-04-03")]
    [InlineData("2026-10-07T03:15:42.1234567Z", "Tokyo Standard Time")]
    [InlineData("2026-03-08T06:59:59.9999999Z", "Eastern Standard Time")]
    public void EveryTimestampIsUtcWithZAndDoesNotDependOnTheWindowsTimeZone(string fixedTime, string zoneId)
    {
        // Windows のタイムゾーンを変えて実行する代わりに、ローカル時刻の表示用のゾーンを変えても記録が変わらないことを確かめる。
        DateTime now = DateTime.Parse(fixedTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var time = new FixedTime(now);
        var audit = new ForensicAudit(Identity, time);
        audit.AttachCase(AuditLog.Open(LogPath, Identity, time));
        audit.Record(new AuditRecord(AuditOperations.CaseCreate));
        audit.Record(new AuditRecord(AuditOperations.DocumentOpen) { Path = "TD-SEQ-1M.bin" });
        audit.Record(new AuditRecord(AuditOperations.DocumentClose) { Path = "TD-SEQ-1M.bin" });
        audit.Record(new AuditRecord(AuditOperations.WriteBlockEnable) { AlwaysGlobal = true });
        Assert.NotEqual(fixedTime, Local(now, zone));

        var pattern = new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$");
        string[] lines = File.ReadAllLines(LogPath);
        Assert.Equal(4, lines.Length);
        foreach (string line in lines)
        {
            AuditLog.TryParse(line, out AuditEntry? entry);
            Assert.Matches(pattern, entry!.Time);
            Assert.Equal(fixedTime, entry.Time);
        }
    }

    private static string Local(DateTime utc, TimeZoneInfo zone) => ForensicTimeFormat.Local(ForensicTimestamp.FromUtc(utc, 1), zone);

    [Fact]
    [Trait(TC, "TC-FOR-04-04")]
    public void WhenTheLogCannotBeWrittenWritesAreDisabledAndRecordsAreKeptUntilRecovery()
    {
        var storage = new FlakyStorage();
        var time = new FixedTime(Fixed1);
        var audit = new ForensicAudit(Identity, time, storage);
        audit.AttachCase(AuditLog.Open(LogPath, Identity, time, storage));
        var faults = new List<AuditFaultEventArgs>();
        audit.FaultChanged += (_, e) => faults.Add(e);

        audit.Record(new AuditRecord(AuditOperations.CaseOpen));
        Assert.True(audit.WritesAllowed);

        // 「空き容量不足」で次の書き込みが失敗する。
        storage.Failing = true;
        Assert.Throws<AuditWriteException>(audit.EnsureCanWrite);
        Assert.Null(audit.Record(new AuditRecord(AuditOperations.DocumentOpen) { Path = "a.bin" }));
        Assert.False(audit.WritesAllowed);
        Assert.Single(faults);
        Assert.True(faults[0].Faulted);
        Assert.Contains("space", audit.FaultMessage, StringComparison.OrdinalIgnoreCase);

        // 失敗している間の記録は溜める。
        audit.Record(new AuditRecord(AuditOperations.DocumentClose) { Path = "a.bin" });
        Assert.False(audit.TryRecover());
        Assert.Single(File.ReadAllLines(LogPath));

        // 容量が戻ったら書き戻し、連鎖は途切れない。
        storage.Failing = false;
        Assert.True(audit.TryRecover());
        Assert.True(audit.WritesAllowed);
        Assert.False(faults[^1].Faulted);
        string[] lines = File.ReadAllLines(LogPath);
        Assert.Equal(4, lines.Length);
        Assert.True(AuditVerifier.Verify(LogPath).IsIntact);
        AuditLog.TryParse(lines[1], out AuditEntry? first);
        Assert.Equal(AuditOperations.DocumentOpen, first!.Operation);
        AuditLog.TryParse(lines[3], out AuditEntry? recovered);
        Assert.Equal(AuditOperations.AuditRecovered, recovered!.Operation);
    }

    [Fact]
    public void WriteBlockSwitchesAreRecordedInTheGlobalLogWhenNoCaseIsOpen()
    {
        var time = new FixedTime(Fixed1);
        var audit = new ForensicAudit(Identity, time);
        audit.SetGlobalLogFolder(_dir);

        // ケースがないときの通常の操作は記録しない。
        Assert.Null(audit.Record(new AuditRecord(AuditOperations.DocumentOpen)));
        Assert.False(File.Exists(Path.Combine(_dir, ForensicAudit.GlobalFileName)));

        audit.Record(new AuditRecord(AuditOperations.WriteBlockEnable) { AlwaysGlobal = true });
        audit.Record(new AuditRecord(AuditOperations.WriteBlockDisable) { AlwaysGlobal = true, Message = "Disabled by the user." });
        string global = Path.Combine(_dir, ForensicAudit.GlobalFileName);
        Assert.Equal(2, File.ReadAllLines(global).Length);
        Assert.True(AuditVerifier.Verify(global).IsIntact);

        // 閲覧操作は既定では記録しない。設定でオンにすると記録する。
        audit.AttachCase(AuditLog.Open(LogPath, Identity, time));
        Assert.Null(audit.Record(new AuditRecord(AuditOperations.Jump)));
        audit.LogBrowsing = true;
        Assert.NotNull(audit.Record(new AuditRecord(AuditOperations.Jump)));
    }

    [Fact]
    public void SecretsAreNeverLogged()
    {
        AuditLog log = AuditLog.Open(LogPath, Identity, new FixedTime(Fixed1));
        AuditEntry entry = log.Append(new AuditRecord(AuditOperations.DocumentOpen)
        {
            Path = "volume.bin",
            Message = "Recovery key 123456-234567-345678-456789-567890-678901-789012-890123 was entered.",
            Parameters = AuditRecord.Args(("password", "hunter2"), ("Passphrase", "correct horse"), ("keyfile", "k.bin"), ("recovery-key", "abc"), ("mode", "read-only")),
        });
        string text = File.ReadAllText(LogPath);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", text, StringComparison.Ordinal);
        Assert.DoesNotContain("123456-234567", text, StringComparison.Ordinal);
        Assert.Equal(AuditRedactor.Mask, entry.Parameters.Single(p => p.Key == "password").Value);
        Assert.Equal("read-only", entry.Parameters.Single(p => p.Key == "mode").Value);
        Assert.True(AuditVerifier.Verify(LogPath).IsIntact);
    }

    [Fact]
    public void TheChainContinuesAfterReopeningAndAfterATruncatedLastLine()
    {
        var time = new FixedTime(Fixed1);
        AuditLog first = AuditLog.Open(LogPath, Identity, time);
        first.Append(Rec("a"));
        first.Append(Rec("b"));
        AuditLog second = AuditLog.Open(LogPath, Identity, time);
        Assert.Equal(2, second.LastSeq);
        AuditEntry third = second.Append(Rec("c"));
        Assert.Equal(3, third.Seq);
        Assert.True(AuditVerifier.Verify(LogPath).IsIntact);

        // 途中で切れた最後の行 (改行なし): 次の記録は新しい行から始まり、検証は壊れた行を報告する。
        File.AppendAllText(LogPath, "{\"seq\":4,\"time\":\"2026");
        AuditLog third2 = AuditLog.Open(LogPath, Identity, time);
        third2.Append(Rec("d"));
        AuditVerifyResult result = AuditVerifier.Verify(LogPath);
        Assert.Equal(4, result.FirstBrokenLine);
        Assert.Equal(AuditBreakKind.Format, result.Breaks[0].Kind);
    }

    [Fact]
    public void TheReaderIndexesLargeLogsAndFollowsAppends()
    {
        AuditLog log = AuditLog.Open(LogPath, Identity, new FixedTime(Fixed1));
        for (int i = 0; i < 5000; i++)
        {
            log.Append(Rec("bulk", $"entry {i}"));
        }

        using var reader = new AuditLogReader(LogPath);
        Assert.Equal(5000, reader.Count);
        Assert.Equal(4321, reader.Read(4320)!.Seq);
        Assert.Equal("entry 0", reader.Read(0)!.Message);
        log.Append(Rec("bulk", "late"));
        Assert.Equal(1, reader.Refresh());
        Assert.Equal("late", reader.Read(5000)!.Message);
        Assert.Equal(5001, reader.Enumerate().Count());
        Assert.Equal(1, reader.Enumerate(Fixed1.AddDays(1)).Count() + 1);
    }

    [Fact]
    public void ExportToCsvAndJsonKeepsEveryColumnAndEscapesFormulas()
    {
        var time = new FixedTime(Fixed1);
        AuditLog log = AuditLog.Open(LogPath, Identity, time);
        log.Append(new AuditRecord("op") { Message = "=HYPERLINK(\"x\"),with comma" });
        using var reader = new AuditLogReader(LogPath);
        var context = new ForensicTimeContext(TimeDisplayMode.Both, TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time"));
        var sw = new StringWriter();
        AuditExporter.WriteCsv(sw, reader.Enumerate(), context);
        string[] rows = sw.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, rows.Length);
        Assert.StartsWith("seq,time_utc,time_local,user,", rows[0], StringComparison.Ordinal);
        Assert.Contains("2026-10-07T03:15:42.0000000Z", rows[1], StringComparison.Ordinal);
        Assert.Contains("2026-10-07T12:15:42.0000000+09:00", rows[1], StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\"),with comma\"", rows[1], StringComparison.Ordinal);

        // UTC だけにすると、ローカルの列が消える。
        var utcOnly = new StringWriter();
        AuditExporter.WriteCsv(utcOnly, reader.Enumerate(), context with { Mode = TimeDisplayMode.Utc });
        Assert.DoesNotContain("time_local", utcOnly.ToString(), StringComparison.Ordinal);

        using var json = new MemoryStream();
        AuditExporter.WriteJson(json, reader.Enumerate(), context);
        using var doc = System.Text.Json.JsonDocument.Parse(json.ToArray());
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal("op", doc.RootElement[0].GetProperty("operation").GetString());
    }
}
