using System.IO.Compression;
using System.Text.Json;

namespace HexEditor.Core.Patches;

// このファイルは自己適用型パッチの適用プログラム (src/HexEditor.PatchApplier) でも使う。HexEditor.Core の他の型に依存しないこと。

/// <summary>独自形式のパッチの命令の種類 (EDIT-42 の仕様 2)。</summary>
public enum HexPatchOpKind
{
    /// <summary>同じ長さで置き換える。</summary>
    Replace,

    /// <summary>挿入する (変換前の長さ 0)。</summary>
    Insert,

    /// <summary>削除する (変換後の長さ 0)。</summary>
    Delete,
}

/// <summary>
/// 独自形式のパッチの命令 1 つ。変換前の [SourceOffset, SourceOffset + SourceLength) を、変換後の [TargetOffset, TargetOffset + TargetLength) にする。
/// 命令は位置の昇順に並び、重ならない。変換後のデータは <c>data.bin</c>、変換前のデータ (逆適用用。EDIT-43 の仕様 7) は <c>old.bin</c> に、命令の順に続けて入れる。
/// </summary>
public sealed record HexPatchOp(HexPatchOpKind Kind, long SourceOffset, long SourceLength, long TargetOffset, long TargetLength);

/// <summary>独自形式のパッチの破損 (EDIT-43 の「エラー」の「レコード N が壊れています」)。</summary>
public sealed class HexPatchFormatException(string message, int record = -1) : IOException(message)
{
    /// <summary>壊れている命令の番号 (0 から。全体の問題なら −1)。</summary>
    public int Record { get; } = record;
}

/// <summary>
/// 独自形式のパッチ (<c>.hexpatch</c>。EDIT-42 の仕様 2)。zip 形式で、命令の一覧と識別情報 (<c>hexpatch.json</c>)、変換後のデータ (<c>data.bin</c>)、
/// 変換前のデータ (<c>old.bin</c>) を入れる。オフセットは 64 bit で上限はない。
/// </summary>
public sealed class HexPatchManifest
{
    /// <summary>zip の中の目印になる項目 (EDIT-43 の仕様 1 の「zip 内の独自の目印」)。</summary>
    public const string ManifestEntry = "hexpatch.json";

    public const string DataEntry = "data.bin";

    public const string OldDataEntry = "old.bin";

    /// <summary><c>format</c> の値。</summary>
    public const string FormatName = "hexpatch";

    public const int CurrentVersion = 1;

    public required SourceIdentity Source { get; init; }

    public required SourceIdentity Target { get; init; }

    public required IReadOnlyList<HexPatchOp> Ops { get; init; }

    /// <summary>自己適用型パッチの既定の対象ファイル名 (EDIT-44 の仕様 2)。</summary>
    public string? DefaultFileName { get; init; }

    /// <summary>自己適用型パッチで表示するメッセージ (EDIT-44 の「画面」)。</summary>
    public string? Message { get; init; }

    /// <summary>変換後のデータの合計の長さ。</summary>
    public long DataLength => Ops.Sum(o => o.TargetLength);

    /// <summary>変換前のデータの合計の長さ。</summary>
    public long OldDataLength => Ops.Sum(o => o.SourceLength);

    public void WriteTo(Stream output)
    {
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("format", FormatName);
        json.WriteNumber("version", CurrentVersion);
        json.WritePropertyName("source");
        Source.WriteTo(json);
        json.WritePropertyName("target");
        Target.WriteTo(json);
        if (DefaultFileName is not null)
        {
            json.WriteString("defaultFileName", DefaultFileName);
        }

        if (Message is not null)
        {
            json.WriteString("message", Message);
        }

        json.WriteStartArray("ops");
        foreach (HexPatchOp op in Ops)
        {
            json.WriteStartObject();
            json.WriteString("op", op.Kind switch { HexPatchOpKind.Insert => "insert", HexPatchOpKind.Delete => "delete", _ => "replace" });
            json.WriteNumber("src", op.SourceOffset);
            json.WriteNumber("srcLen", op.SourceLength);
            json.WriteNumber("dst", op.TargetOffset);
            json.WriteNumber("dstLen", op.TargetLength);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>JSON を読む。形式・命令の誤りは <see cref="HexPatchFormatException"/>。</summary>
    public static HexPatchManifest Read(Stream input)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(input);
        }
        catch (JsonException ex)
        {
            throw new HexPatchFormatException("hexpatch.json: " + ex.Message);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out JsonElement f) || f.GetString() != FormatName)
            {
                throw new HexPatchFormatException("hexpatch.json: format");
            }

            if (!root.TryGetProperty("version", out JsonElement v) || v.GetInt32() > CurrentVersion)
            {
                throw new HexPatchFormatException("hexpatch.json: version");
            }

            SourceIdentity source, target;
            try
            {
                source = SourceIdentity.ReadFrom(root.GetProperty("source"));
                target = SourceIdentity.ReadFrom(root.GetProperty("target"));
            }
            catch (Exception ex) when (ex is FormatException or KeyNotFoundException)
            {
                throw new HexPatchFormatException("hexpatch.json: source / target");
            }

            var ops = new List<HexPatchOp>();
            if (!root.TryGetProperty("ops", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            {
                throw new HexPatchFormatException("hexpatch.json: ops");
            }

            long lastSource = 0, lastTarget = 0;
            foreach (JsonElement e in list.EnumerateArray())
            {
                int index = ops.Count;
                try
                {
                    HexPatchOpKind kind = e.GetProperty("op").GetString() switch
                    {
                        "replace" => HexPatchOpKind.Replace,
                        "insert" => HexPatchOpKind.Insert,
                        "delete" => HexPatchOpKind.Delete,
                        _ => throw new FormatException(),
                    };
                    var op = new HexPatchOp(kind, e.GetProperty("src").GetInt64(), e.GetProperty("srcLen").GetInt64(),
                        e.GetProperty("dst").GetInt64(), e.GetProperty("dstLen").GetInt64());
                    bool shapeOk = kind switch
                    {
                        HexPatchOpKind.Replace => op.SourceLength == op.TargetLength && op.SourceLength > 0,
                        HexPatchOpKind.Insert => op.SourceLength == 0 && op.TargetLength > 0,
                        _ => op.TargetLength == 0 && op.SourceLength > 0,
                    };

                    // 命令は昇順で重ならず、変換前と変換後の変わらない部分の長さが等しい。
                    if (!shapeOk || op.SourceOffset < lastSource || op.TargetOffset < lastTarget
                        || op.SourceOffset - lastSource != op.TargetOffset - lastTarget
                        || op.SourceOffset + op.SourceLength > source.Length || op.TargetOffset + op.TargetLength > target.Length)
                    {
                        throw new FormatException();
                    }

                    lastSource = op.SourceOffset + op.SourceLength;
                    lastTarget = op.TargetOffset + op.TargetLength;
                    ops.Add(op);
                }
                catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
                {
                    throw new HexPatchFormatException($"op {index + 1}", index);
                }
            }

            if (source.Length - lastSource != target.Length - lastTarget)
            {
                throw new HexPatchFormatException("hexpatch.json: length");
            }

            return new HexPatchManifest
            {
                Source = source,
                Target = target,
                Ops = ops,
                DefaultFileName = root.TryGetProperty("defaultFileName", out JsonElement d) ? d.GetString() : null,
                Message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() : null,
            };
        }
    }

    /// <summary>zip が独自形式のパッチか (目印の項目があるか)。</summary>
    public static bool IsHexPatch(ZipArchive zip) => zip.GetEntry(ManifestEntry) is not null;

    /// <summary>zip から命令の一覧を読む。</summary>
    public static HexPatchManifest Read(ZipArchive zip)
    {
        ZipArchiveEntry entry = zip.GetEntry(ManifestEntry) ?? throw new HexPatchFormatException("hexpatch.json");
        using Stream s = entry.Open();
        HexPatchManifest manifest = Read(s);
        if (zip.GetEntry(DataEntry) is not { } data || data.Length != manifest.DataLength)
        {
            throw new HexPatchFormatException("data.bin");
        }

        if (zip.GetEntry(OldDataEntry) is { } old && old.Length != manifest.OldDataLength)
        {
            throw new HexPatchFormatException("old.bin");
        }

        return manifest;
    }

    /// <summary>逆適用 (EDIT-43 の仕様 7) に必要な変換前のデータがあるか。</summary>
    public static bool HasOldData(ZipArchive zip, HexPatchManifest manifest) =>
        manifest.OldDataLength == 0 || zip.GetEntry(OldDataEntry) is { } e && e.Length == manifest.OldDataLength;

    /// <summary>
    /// ストリームで適用する (自己適用型パッチの適用プログラムと同じ処理。EDIT-44 の「巨大ファイル」)。<paramref name="input"/> は変換前
    /// (<paramref name="reverse"/> なら変換後) のデータ、<paramref name="data"/> は data.bin (逆適用は old.bin) を先頭から読むストリーム。
    /// <paramref name="progress"/> には書いたバイト数を知らせる。
    /// </summary>
    public void ApplyStreaming(Stream input, Stream data, Stream output, bool reverse = false, Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        byte[] buffer = new byte[1 << 20];
        long inputPos = 0, written = 0;
        long inputLength = reverse ? Target.Length : Source.Length;

        void Copy(Stream from, long count)
        {
            while (count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (n <= 0)
                {
                    throw new HexPatchFormatException("data");
                }

                output.Write(buffer, 0, n);
                count -= n;
                written += n;
                progress?.Invoke(written);
            }
        }

        void Skip(long count)
        {
            if (input.CanSeek)
            {
                input.Seek(count, SeekOrigin.Current);
                return;
            }

            while (count > 0)
            {
                int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (n <= 0)
                {
                    throw new EndOfStreamException();
                }

                count -= n;
            }
        }

        foreach (HexPatchOp op in Ops)
        {
            long at = reverse ? op.TargetOffset : op.SourceOffset;
            long remove = reverse ? op.TargetLength : op.SourceLength;
            long add = reverse ? op.SourceLength : op.TargetLength;
            Copy(input, at - inputPos);
            Skip(remove);
            inputPos = at + remove;
            Copy(data, add);
        }

        Copy(input, inputLength - inputPos);
    }
}

/// <summary>
/// 自己適用型パッチ (EDIT-44) の実行ファイルの末尾。適用プログラムの後ろに独自形式のパッチ (zip) を付け、最後の 16 バイトに
/// 目印 <c>HXSELFPT</c> とパッチの長さ (リトルエンディアンの 64 bit) を置く。
/// </summary>
public static class SelfPatchContainer
{
    public const int TrailerSize = 16;

    private static ReadOnlySpan<byte> Magic => "HXSELFPT"u8;

    /// <summary>末尾に付ける 16 バイト。</summary>
    public static byte[] Trailer(long payloadLength)
    {
        byte[] trailer = new byte[TrailerSize];
        Magic.CopyTo(trailer);
        BitConverter.TryWriteBytes(trailer.AsSpan(8), payloadLength);
        if (!BitConverter.IsLittleEndian)
        {
            trailer.AsSpan(8).Reverse();
        }

        return trailer;
    }

    /// <summary>実行ファイルの中のパッチの位置と長さ。目印がなければ null。</summary>
    public static (long Offset, long Length)? Find(Stream exe)
    {
        if (exe.Length < TrailerSize)
        {
            return null;
        }

        byte[] trailer = new byte[TrailerSize];
        exe.Position = exe.Length - TrailerSize;
        exe.ReadExactly(trailer);
        if (!trailer.AsSpan(0, 8).SequenceEqual(Magic))
        {
            return null;
        }

        Span<byte> len = trailer.AsSpan(8);
        if (!BitConverter.IsLittleEndian)
        {
            len.Reverse();
        }

        long length = BitConverter.ToInt64(len);
        long offset = exe.Length - TrailerSize - length;
        return length <= 0 || offset < 0 ? null : (offset, length);
    }
}

/// <summary>ストリームの一部分を、先頭から始まるシークできるストリームとして見せる (実行ファイルの中の zip を読む)。</summary>
public sealed class SubStream(Stream inner, long offset, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset1, int count) => Read(buffer.AsSpan(offset1, count));

    public override int Read(Span<byte> buffer)
    {
        int n = (int)Math.Min(buffer.Length, length - _position);
        if (n <= 0)
        {
            return 0;
        }

        inner.Position = offset + _position;
        int read = inner.Read(buffer[..n]);
        _position += read;
        return read;
    }

    public override long Seek(long offset1, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset1,
            SeekOrigin.Current => _position + offset1,
            _ => length + offset1,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset1, int count) => throw new NotSupportedException();
}
