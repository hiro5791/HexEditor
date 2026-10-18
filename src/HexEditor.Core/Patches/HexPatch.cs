using System.IO.Compression;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Patches;

/// <summary>
/// 独自形式のパッチ (<c>.hexpatch</c>。EDIT-42 の仕様 2) の適用と書き出し。適用はピースの操作 (変わらない部分は元の木を共有し、データだけを
/// 追加バッファに入れる) で、変換前の長さと部分的な SHA-256 (全体の SHA-256 があればそれも) で確認する (EDIT-43 の仕様 3)。逆適用もできる (仕様 7)。
/// </summary>
public sealed class HexPatch : LoadedPatch
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly Stream? _outer;
    private readonly ZipArchive _zip;

    private HexPatch(Stream stream, bool ownsStream, Stream? outer, ZipArchive zip, HexPatchManifest manifest)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _outer = outer;
        _zip = zip;
        Manifest = manifest;
    }

    public HexPatchManifest Manifest { get; }

    public override PatchFormat Format => PatchFormat.HexPatch;

    public override int RecordCount => Manifest.Ops.Count;

    public override long ChangedBytes => Manifest.Ops.Sum(o => Math.Max(o.SourceLength, o.TargetLength));

    public override bool CanReverse => HexPatchManifest.HasOldData(_zip, Manifest);

    public override bool VerificationReadsWholeFile => Manifest.Source.FullSha256 is not null;

    public override long ResultLength(long inputLength, bool reverse = false) => reverse ? Manifest.Source.Length : Manifest.Target.Length;

    public override PatchVerification Verify(DocumentSnapshot input, bool reverse = false, Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        SourceIdentity expected = reverse ? Manifest.Target : Manifest.Source;
        if (input.Length != expected.Length)
        {
            return PatchVerification.Mismatch;
        }

        SourceIdentity actual = IdentityOf(input, expected.FullSha256 is not null, progress, cancellationToken);
        return actual.Matches(expected) ? PatchVerification.Match : PatchVerification.Mismatch;
    }

    protected override bool VerifyResult(DocumentSnapshot result, bool reverse, Action<long>? progress, CancellationToken cancellationToken)
    {
        SourceIdentity expected = reverse ? Manifest.Source : Manifest.Target;
        return result.Length == expected.Length && IdentityOf(result, expected.FullSha256 is not null, progress, cancellationToken).Matches(expected);
    }

    protected override void ApplyCore(Document document, bool reverse, string description, CancellationToken cancellationToken)
    {
        PieceTree input = document.Current.Tree;
        long inputLength = reverse ? Manifest.Target.Length : Manifest.Source.Length;
        if (input.Length != inputLength)
        {
            throw new PatchException(PatchError.OutOfRange, "変換前の長さが違います。");
        }

        ZipArchiveEntry? entry = _zip.GetEntry(reverse ? HexPatchManifest.OldDataEntry : HexPatchManifest.DataEntry);
        using Stream data = entry?.Open() ?? Stream.Null;
        PieceTree output = PieceTree.Empty;
        long pos = 0;
        byte[] buffer = new byte[1 << 20];
        int index = 0;
        foreach (HexPatchOp op in Manifest.Ops)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long at = reverse ? op.TargetOffset : op.SourceOffset;
            long remove = reverse ? op.TargetLength : op.SourceLength;
            long add = reverse ? op.SourceLength : op.TargetLength;
            if (at > pos)
            {
                output = output.Concat(input.Slice(pos, at - pos));
            }

            for (long done = 0; done < add;)
            {
                int n = (int)Math.Min(buffer.Length, add - done);
                if (data.ReadAtLeast(buffer.AsSpan(0, n), n, throwOnEndOfStream: false) != n)
                {
                    throw new PatchException(PatchError.Corrupt, $"レコード {index + 1} のデータが足りません。", index);
                }

                output = output.Concat(document.BufferBytes(buffer.AsSpan(0, n)));
                done += n;
            }

            pos = at + remove;
            index++;
        }

        if (pos < inputLength)
        {
            output = output.Concat(input.Slice(pos, inputLength - pos));
        }

        document.ReplaceAll(output, description);
    }

    public override void Dispose()
    {
        _zip.Dispose();
        if (_ownsStream)
        {
            _stream.Dispose();
        }

        _outer?.Dispose();
        base.Dispose();
    }

    internal static HexPatch Load(Stream stream, bool ownsStream, Stream? outer = null)
    {
        ZipArchive? zip = null;
        try
        {
            stream.Position = 0;
            zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            HexPatchManifest manifest = HexPatchManifest.Read(zip);
            return new HexPatch(stream, ownsStream, outer, zip, manifest);
        }
        catch (Exception ex) when (ex is HexPatchFormatException or InvalidDataException)
        {
            zip?.Dispose();
            if (ownsStream)
            {
                stream.Dispose();
            }

            outer?.Dispose();
            int record = ex is HexPatchFormatException h ? h.Record : -1;
            throw new PatchException(PatchError.Corrupt, ex.Message, record);
        }
    }

    // ---- 書き出し ----

    /// <summary>自己適用型パッチの適用プログラムの文字列を入れる項目。</summary>
    public const string ApplierStringsEntry = "applier-strings.json";

    /// <summary>推定サイズ (命令の数とパッチの大きさ。データは無圧縮として見積もる)。</summary>
    internal static (long Records, long Size) Estimate(PatchScript script, List<HexPatchOp> ops)
    {
        long data = ops.Sum(o => o.SourceLength + o.TargetLength);
        return (ops.Count, 1024 + ops.Count * 96L + data);
    }

    /// <summary>
    /// 独自形式で書く。<paramref name="fullHash"/> なら変換前・変換後の全体の SHA-256 も入れる (全体を読む)。<paramref name="defaultFileName"/>・
    /// <paramref name="message"/> は自己適用型パッチ (EDIT-44) の既定の対象ファイル名と表示するメッセージ。
    /// </summary>
    public static HexPatchManifest Write(Stream output, PatchScript script, bool fullHash = false, string? defaultFileName = null, string? message = null,
        SourceIdentity? sourceIdentity = null, List<HexPatchOp>? ops = null, Action<long>? progress = null, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? applierStrings = null)
    {
        ops ??= script.Operations();
        SourceIdentity source = sourceIdentity ?? SourceIdentity.Compute(Reader(script.ReadSource, script.SourceLength), script.SourceLength, fullHash, progress,
            cancellationToken);
        SourceIdentity target = SourceIdentity.Compute(Reader(script.ReadTarget, script.TargetLength), script.TargetLength, fullHash,
            p => progress?.Invoke(script.SourceLength + p), cancellationToken);
        var manifest = new HexPatchManifest
        {
            Source = source,
            Target = target,
            Ops = ops,
            DefaultFileName = defaultFileName,
            Message = message,
        };

        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        using (Stream json = zip.CreateEntry(HexPatchManifest.ManifestEntry, CompressionLevel.Optimal).Open())
        {
            manifest.WriteTo(json);
        }

        byte[] buffer = new byte[1 << 20];
        void CopyRanges(string name, bool fromTarget)
        {
            using Stream s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
            foreach (HexPatchOp op in ops)
            {
                long at = fromTarget ? op.TargetOffset : op.SourceOffset;
                long length = fromTarget ? op.TargetLength : op.SourceLength;
                for (long done = 0; done < length; done += buffer.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, length - done);
                    (fromTarget ? script.ReadTarget : script.ReadSource)(at + done, buffer.AsSpan(0, n));
                    s.Write(buffer, 0, n);
                }
            }
        }

        CopyRanges(HexPatchManifest.DataEntry, fromTarget: true);
        CopyRanges(HexPatchManifest.OldDataEntry, fromTarget: false);
        if (applierStrings is not null)
        {
            // 自己適用型パッチの適用プログラムが表示する文字列 (言語ごと。EDIT-44 の仕様 4)。
            using Stream s = zip.CreateEntry(ApplierStringsEntry, CompressionLevel.Optimal).Open();
            using var json = new System.Text.Json.Utf8JsonWriter(s);
            json.WriteStartObject();
            foreach ((string language, IReadOnlyDictionary<string, string> table) in applierStrings)
            {
                json.WriteStartObject(language);
                foreach ((string key, string value) in table)
                {
                    json.WriteString(key, value);
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        return manifest;
    }

    private static IdentityReader Reader(Clipboard.ByteReader read, long length) => (offset, destination) =>
    {
        int n = (int)Math.Min(destination.Length, Math.Max(0, length - offset));
        read(offset, destination[..n]);
        return n;
    };
}
