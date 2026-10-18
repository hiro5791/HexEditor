using System.IO.Compression;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;

namespace HexEditor.Core.Patches;

/// <summary>パッチの形式 (EDIT-42 の仕様 2、EDIT-44)。</summary>
public enum PatchFormat
{
    Ips,
    Ips32,
    Ups,
    Bps,

    /// <summary>独自形式 (<c>.hexpatch</c>)。</summary>
    HexPatch,

    /// <summary>自己適用型 (EXE。EDIT-44)。中身は独自形式。</summary>
    SelfApplying,
}

/// <summary>変換前の確認の結果 (EDIT-43 の仕様 3)。</summary>
public enum PatchVerification
{
    /// <summary>一致。</summary>
    Match,

    /// <summary>不一致 (別のファイル用の可能性がある)。</summary>
    Mismatch,

    /// <summary>確認の手段がない (IPS・IPS32)。</summary>
    Unverifiable,
}

/// <summary>パッチの誤り (形式の判別の失敗、破損、適用後の CRC の不一致など。EDIT-43 の「エラー」)。</summary>
public sealed class PatchException(PatchError error, string message, int record = -1, Exception? inner = null) : IOException(message, inner)
{
    public PatchError Error { get; } = error;

    /// <summary>壊れているレコードの番号 (0 から。全体の問題なら −1)。</summary>
    public int Record { get; } = record;
}

public enum PatchError
{
    /// <summary>形式を判別できない。</summary>
    UnknownFormat,

    /// <summary>パッチが壊れている (<see cref="PatchException.Record"/> のレコード)。</summary>
    Corrupt,

    /// <summary>適用後の CRC-32 (独自形式は識別情報) が合わない。適用は取り消した。</summary>
    ResultMismatch,

    /// <summary>適用する対象の長さが範囲外 (長さ固定のドキュメントで末尾を超える)。</summary>
    OutOfRange,

    /// <summary>この形式は逆適用できない。</summary>
    ReverseNotSupported,
}

/// <summary>
/// 読み込んだパッチ (EDIT-43)。適用前のダイアログに出す情報と、確認・適用を持つ。適用はドキュメントへの 1 つの編集グループ。
/// </summary>
public abstract class LoadedPatch : IDisposable
{
    public abstract PatchFormat Format { get; }

    /// <summary>レコード (命令) の数。</summary>
    public abstract int RecordCount { get; }

    /// <summary>変更するバイト数 (書き込むデータの量)。</summary>
    public abstract long ChangedBytes { get; }

    /// <summary>逆適用 (UPS と独自形式のみ。EDIT-43 の仕様 7) できるか。</summary>
    public virtual bool CanReverse => false;

    /// <summary>変換前の確認がファイル全体を読むか (CRC-32・全体の SHA-256。長時間処理にして「確認を省略して適用」を付ける)。</summary>
    public virtual bool VerificationReadsWholeFile => false;

    /// <summary>長さ <paramref name="inputLength"/> の対象に当てた後の長さ。</summary>
    public abstract long ResultLength(long inputLength, bool reverse = false);

    /// <summary>変換前の確認 (EDIT-43 の仕様 3)。<paramref name="progress"/> には読んだバイト数を知らせる。</summary>
    public abstract PatchVerification Verify(DocumentSnapshot input, bool reverse = false, Action<long>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ドキュメントに当てる (1 回の Undo で戻せる。EDIT-43 の仕様 4)。<paramref name="verifyResult"/> なら適用後の CRC-32 (独自形式は識別情報) を確かめ、
    /// 合わなければ適用を取り消して <see cref="PatchException"/> (<see cref="PatchError.ResultMismatch"/>。仕様 5)。長さ固定のドキュメントでは
    /// 長さが変わるパッチは当てない (<see cref="FixedLengthException"/>。仕様 6)。
    /// </summary>
    public void ApplyTo(Document document, bool reverse = false, bool verifyResult = true, string description = "パッチの適用",
        Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (reverse && !CanReverse)
        {
            throw new PatchException(PatchError.ReverseNotSupported, "この形式は逆適用できません。");
        }

        long result = ResultLength(document.Length, reverse);
        if (result != document.Length && !document.CanResize)
        {
            throw new FixedLengthException();
        }

        HistoryNode before = document.History.CurrentNode;
        using (document.BeginGroup(description))
        {
            ApplyCore(document, reverse, description, cancellationToken);
        }

        if (verifyResult && !VerifyResult(document.Current, reverse, progress, cancellationToken))
        {
            if (!ReferenceEquals(document.History.CurrentNode, before) && document.History.CanUndo)
            {
                document.Undo();
            }

            throw new PatchException(PatchError.ResultMismatch, "適用後の内容が、パッチに記録された内容と一致しません。");
        }
    }

    protected abstract void ApplyCore(Document document, bool reverse, string description, CancellationToken cancellationToken);

    /// <summary>
    /// 適用後の確認だけをする (UPS・BPS の CRC-32、独自形式の識別情報。仕様 5)。UI は適用を UI スレッドで、この確認をバックグラウンドで行い、
    /// 合わなければ元に戻す。確認の手段がない形式は true。
    /// </summary>
    public bool CheckResult(DocumentSnapshot result, bool reverse = false, Action<long>? progress = null, CancellationToken cancellationToken = default) =>
        VerifyResult(result, reverse, progress, cancellationToken);

    /// <summary>適用後の確認。確認の手段がない形式は true。</summary>
    protected virtual bool VerifyResult(DocumentSnapshot result, bool reverse, Action<long>? progress, CancellationToken cancellationToken) => true;

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>スナップショット全体の CRC-32 (UPS・BPS の確認。ファイルサイズに比例する長時間処理)。</summary>
    public static uint Crc32(DocumentSnapshot snapshot, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[1 << 20];
        uint state = PatchCrc32.Initial;
        for (long pos = 0; pos < snapshot.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, snapshot.Length - pos);
            PatchDiff.ReadPadded(snapshot, pos, buffer.AsSpan(0, n));
            state = PatchCrc32.Append(state, buffer.AsSpan(0, n));
            pos += n;
            progress?.Invoke(pos);
        }

        return PatchCrc32.Finish(state);
    }

    /// <summary>スナップショットの識別情報 (独自形式の確認)。</summary>
    public static SourceIdentity IdentityOf(DocumentSnapshot snapshot, bool full, Action<long>? progress = null, CancellationToken cancellationToken = default) =>
        SourceIdentity.Compute((offset, destination) =>
        {
            int n = (int)Math.Min(destination.Length, Math.Max(0, snapshot.Length - offset));
            PatchDiff.ReadPadded(snapshot, offset, destination[..n]);
            return n;
        }, snapshot.Length, full, progress, cancellationToken);
}

/// <summary>パッチファイルの判別と読み込み (EDIT-43 の仕様 1)。</summary>
public static class PatchFiles
{
    /// <summary>
    /// ヘッダで形式を判別する (<c>PATCH</c> → IPS、<c>IPS32</c> → IPS32、<c>UPS1</c> → UPS、<c>BPS1</c> → BPS、zip 内の <c>hexpatch.json</c> → 独自形式、
    /// 末尾に自己適用型の目印がある実行ファイル → 自己適用型)。判別できなければ null。
    /// </summary>
    public static PatchFormat? Detect(Stream stream)
    {
        Span<byte> head = stackalloc byte[5];
        stream.Position = 0;
        int n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        ReadOnlySpan<byte> h = head[..n];
        PatchFormat? format = h.StartsWith("PATCH"u8) ? PatchFormat.Ips
            : h.StartsWith("IPS32"u8) ? PatchFormat.Ips32
            : h.StartsWith("UPS1"u8) ? PatchFormat.Ups
            : h.StartsWith("BPS1"u8) ? PatchFormat.Bps
            : null;
        if (format is null && h.StartsWith("PK"u8))
        {
            try
            {
                stream.Position = 0;
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                format = HexPatchManifest.IsHexPatch(zip) ? PatchFormat.HexPatch : null;
            }
            catch (InvalidDataException)
            {
                format = null;
            }
        }

        if (format is null && h.StartsWith("MZ"u8) && SelfPatchContainer.Find(stream) is not null)
        {
            format = PatchFormat.SelfApplying;
        }

        stream.Position = 0;
        return format;
    }

    /// <summary>パッチファイルを開いて読む。判別できない・壊れている場合は <see cref="PatchException"/>。</summary>
    public static LoadedPatch Load(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16);
        try
        {
            return Load(stream, ownsStream: true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>ストリーム (シークできるもの) からパッチを読む。<paramref name="ownsStream"/> なら、パッチを閉じるときにストリームも閉じる。</summary>
    public static LoadedPatch Load(Stream stream, bool ownsStream = false)
    {
        PatchFormat format = Detect(stream) ?? throw new PatchException(PatchError.UnknownFormat, "パッチの形式を判別できません。");
        switch (format)
        {
            case PatchFormat.Ips or PatchFormat.Ips32:
                try
                {
                    IpsPatchData data = IpsPatch.Read(stream);
                    if (ownsStream)
                    {
                        stream.Dispose();
                    }

                    return new IpsLoadedPatch(data);
                }
                catch (IpsFormatException ex)
                {
                    throw new PatchException(PatchError.Corrupt, ex.Message, ex.Record, ex);
                }

            case PatchFormat.Ups:
                return UpsPatch.Load(stream, ownsStream);
            case PatchFormat.Bps:
                return BpsPatch.Load(stream, ownsStream);
            case PatchFormat.HexPatch:
                return HexPatch.Load(stream, ownsStream);
            default:
                (long offset, long length) = SelfPatchContainer.Find(stream)!.Value;
                return HexPatch.Load(new SubStream(stream, offset, length), ownsStream: true, outer: ownsStream ? stream : null);
        }
    }
}

/// <summary>IPS・IPS32 (TOOL-12 の読み込みの規則)。確認の手段がない。</summary>
internal sealed class IpsLoadedPatch(IpsPatchData data) : LoadedPatch
{
    public IpsPatchData Data { get; } = data;

    public override PatchFormat Format => Data.Ips32 ? PatchFormat.Ips32 : PatchFormat.Ips;

    public override int RecordCount => Data.Records.Count;

    public override long ChangedBytes => Data.Records.Sum(r => (long)r.Length);

    public override long ResultLength(long inputLength, bool reverse = false) => IpsPatch.ResultLength(Data, inputLength);

    public override PatchVerification Verify(DocumentSnapshot input, bool reverse = false, Action<long>? progress = null,
        CancellationToken cancellationToken = default) => PatchVerification.Unverifiable;

    protected override void ApplyCore(Document document, bool reverse, string description, CancellationToken cancellationToken) =>
        IpsPatch.ApplyTo(document, Data, description);
}
