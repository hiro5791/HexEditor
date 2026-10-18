using System.Security.Cryptography;
using System.Text.Json;

namespace HexEditor.Core.Patches;

// このファイルは自己適用型パッチの適用プログラム (src/HexEditor.PatchApplier) でも使う。HexEditor.Core の他の型に依存しないこと。

/// <summary>位置 <c>offset</c> から読めるだけ読み、読んだバイト数を返す (末尾では短くなる)。</summary>
public delegate int IdentityReader(long offset, Span<byte> destination);

/// <summary>
/// ファイル (データ) の識別情報 (EDIT-21 の仕様 8、EDIT-42 の独自形式)。長さと、先頭・中央・末尾の各 1 MiB の SHA-256、任意で全体の SHA-256。
/// パスと更新日時は表示用で、一致の判定には使わない (ファイルを移動・コピーしても同じ内容なら一致とする)。
/// </summary>
public sealed record SourceIdentity(long Length, string HeadSha256, string MiddleSha256, string TailSha256)
{
    /// <summary>部分的な SHA-256 を取る大きさ (各 1 MiB)。</summary>
    public const int PartSize = 1024 * 1024;

    /// <summary>全体の SHA-256 (保存時に選んだ場合だけ。巨大ファイルでは時間がかかるため既定はなし)。</summary>
    public string? FullSha256 { get; init; }

    /// <summary>識別情報を取ったときのパス (表示用)。</summary>
    public string? Path { get; init; }

    /// <summary>識別情報を取ったときの更新日時 (表示用)。</summary>
    public DateTimeOffset? LastWriteTime { get; init; }

    /// <summary>中央の部分の開始位置。</summary>
    public static long MiddleOffset(long length) => Math.Max(0, length / 2 - PartSize / 2);

    /// <summary>
    /// 識別情報を求める。<paramref name="full"/> なら全体の SHA-256 も求める (<paramref name="progress"/> に読んだバイト数を知らせる)。
    /// </summary>
    public static SourceIdentity Compute(IdentityReader read, long length, bool full = false, Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int part = (int)Math.Min(PartSize, length);
        string head = HashRange(read, 0, part);
        string middle = HashRange(read, MiddleOffset(length), (int)Math.Min(part, length - MiddleOffset(length)));
        string tail = HashRange(read, length - part, part);
        string? whole = null;
        if (full)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[1 << 20];
            for (long pos = 0; pos < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = read(pos, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - pos)));
                if (n <= 0)
                {
                    throw new EndOfStreamException();
                }

                sha.AppendData(buffer, 0, n);
                pos += n;
                progress?.Invoke(pos);
            }

            whole = Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        return new SourceIdentity(length, head, middle, tail) { FullSha256 = whole };
    }

    /// <summary>ストリーム (シークできるもの) の識別情報。</summary>
    public static SourceIdentity Compute(Stream stream, bool full = false, Action<long>? progress = null, CancellationToken cancellationToken = default) =>
        Compute((offset, destination) =>
        {
            stream.Position = offset;
            return stream.ReadAtLeast(destination, destination.Length, throwOnEndOfStream: false);
        }, stream.Length, full, progress, cancellationToken);

    private static string HashRange(IdentityReader read, long offset, int length)
    {
        byte[] buffer = new byte[length];
        int done = 0;
        while (done < length)
        {
            int n = read(offset + done, buffer.AsSpan(done));
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }

            done += n;
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }

    /// <summary>
    /// 同じデータと見なせるか: 長さと部分的な SHA-256 が一致し、両方に全体の SHA-256 があればそれも一致する。
    /// </summary>
    public bool Matches(SourceIdentity other) =>
        Length == other.Length
        && string.Equals(HeadSha256, other.HeadSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(MiddleSha256, other.MiddleSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(TailSha256, other.TailSha256, StringComparison.OrdinalIgnoreCase)
        && (FullSha256 is null || other.FullSha256 is null || string.Equals(FullSha256, other.FullSha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>JSON のオブジェクトとして書く。</summary>
    public void WriteTo(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteNumber("length", Length);
        json.WriteString("headSha256", HeadSha256);
        json.WriteString("middleSha256", MiddleSha256);
        json.WriteString("tailSha256", TailSha256);
        if (FullSha256 is not null)
        {
            json.WriteString("sha256", FullSha256);
        }

        if (Path is not null)
        {
            json.WriteString("path", Path);
        }

        if (LastWriteTime is { } time)
        {
            json.WriteString("lastWriteTime", time.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        json.WriteEndObject();
    }

    /// <summary>JSON のオブジェクトから読む。形式が違えば <see cref="FormatException"/>。</summary>
    public static SourceIdentity ReadFrom(JsonElement e)
    {
        try
        {
            return new SourceIdentity(e.GetProperty("length").GetInt64(), e.GetProperty("headSha256").GetString()!,
                e.GetProperty("middleSha256").GetString()!, e.GetProperty("tailSha256").GetString()!)
            {
                FullSha256 = e.TryGetProperty("sha256", out JsonElement s) ? s.GetString() : null,
                Path = e.TryGetProperty("path", out JsonElement p) ? p.GetString() : null,
                LastWriteTime = e.TryGetProperty("lastWriteTime", out JsonElement t) && DateTimeOffset.TryParse(t.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset dt) ? dt : null,
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or NullReferenceException)
        {
            throw new FormatException("識別情報の形式が違います。", ex);
        }
    }
}
