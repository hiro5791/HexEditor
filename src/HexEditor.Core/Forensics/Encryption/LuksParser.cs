using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using HexEditor.Core.Forensics.Carving;

namespace HexEditor.Core.Forensics.Encryption;

/// <summary>
/// LUKS 1 / 2 のヘッダを読む (12 の FOR-29 の仕様 2: 暗号方式、UUID、キースロットの数)。値はビッグエンディアン。
/// <code>
/// 共通: 0 "LUKS" BA BE、6 版 (u16)
/// LUKS1: 8 暗号の名前 [32]、40 モード [32]、72 ハッシュ [32]、168 UUID [40]、208〜 キースロット 8 個 (48 バイトずつ。先頭 u32 が 0x00AC71F3 なら有効)
/// LUKS2: 8 ヘッダの大きさ (u64。JSON の領域を含む)、24 ラベル [48]、168 UUID [40]、4096〜 JSON (keyslots / segments)
/// </code>
/// JSON の領域は LUKS2 の決まりの上限 (4 MiB) までしか読まず、入れ子の深さも制限する。
/// </summary>
public static class LuksParser
{
    public static readonly byte[] Magic = [(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE];

    public const int BinaryHeaderSize = 4096;

    /// <summary>LUKS2 のヘッダ (JSON の領域を含む) の大きさの上限。</summary>
    public const long MaxHeaderSize = 4 * 1024 * 1024;

    public const uint KeySlotEnabled = 0x00AC71F3;

    private static string Text(ReadOnlySpan<byte> s)
    {
        int end = s.IndexOf((byte)0);
        ReadOnlySpan<byte> t = end < 0 ? s : s[..end];
        var sb = new StringBuilder(t.Length);
        foreach (byte b in t)
        {
            if (b is >= 0x20 and < 0x7F)
            {
                sb.Append((char)b);
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>先頭が LUKS の署名か。</summary>
    public static bool IsLuks(ReadOnlySpan<byte> head) => head.Length >= 8 && head[..6].SequenceEqual(Magic);

    /// <summary>LUKS のヘッダを読む。LUKS でなければ null。</summary>
    public static LuksInfo? Parse(ICarveData data, long offset)
    {
        byte[]? head = EncryptionRead.Bytes(data, offset, 1024);
        if (head is null || !IsLuks(head))
        {
            return null;
        }

        ushort version = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(6));
        if (version == 1)
        {
            int slots = 0;
            for (int i = 0; i < 8; i++)
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(208 + i * 48)) == KeySlotEnabled)
                {
                    slots++;
                }
            }

            string cipher = Text(head.AsSpan(8, 32));
            string mode = Text(head.AsSpan(40, 32));
            return new LuksInfo(1, mode.Length > 0 ? $"{cipher}-{mode}" : cipher, Text(head.AsSpan(168, 40)), slots, Hash: Text(head.AsSpan(72, 32)));
        }

        if (version != 2)
        {
            return new LuksInfo(version, string.Empty, Text(head.AsSpan(168, 40)), 0);
        }

        string uuid = Text(head.AsSpan(168, 40));
        string label = Text(head.AsSpan(24, 48));
        string checksum = Text(head.AsSpan(72, 32));
        ulong size = BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8));
        string cipher2 = string.Empty;
        int keySlots = 0;
        if (size > BinaryHeaderSize && size <= MaxHeaderSize && EncryptionRead.Bytes(data, offset + BinaryHeaderSize, (int)size - BinaryHeaderSize) is { } area)
        {
            (cipher2, keySlots) = ParseJson(area);
        }

        return new LuksInfo(2, cipher2, uuid, keySlots, label.Length > 0 ? label : null, checksum.Length > 0 ? checksum : null);
    }

    /// <summary>LUKS2 の JSON の領域 (末尾は 0 で埋まる) から、最初のセグメントの暗号方式とキースロットの数を読む。読めなければ ("", 0)。</summary>
    public static (string Cipher, int KeySlots) ParseJson(ReadOnlySpan<byte> area)
    {
        int end = area.IndexOf((byte)0);
        ReadOnlySpan<byte> json = end < 0 ? area : area[..end];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = doc.RootElement;
            int slots = 0;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("keyslots", out JsonElement keyslots) && keyslots.ValueKind == JsonValueKind.Object)
            {
                slots = keyslots.EnumerateObject().Count();
            }

            string cipher = string.Empty;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("segments", out JsonElement segments) && segments.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty segment in segments.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (segment.Value.ValueKind == JsonValueKind.Object && segment.Value.TryGetProperty("encryption", out JsonElement enc) && enc.ValueKind == JsonValueKind.String)
                    {
                        cipher = enc.GetString() ?? string.Empty;
                        break;
                    }
                }
            }

            return (cipher, slots);
        }
        catch (JsonException)
        {
            return (string.Empty, 0);
        }
    }

    // ---- 作る (テストデータ TD-FOR-LUKS2 と同じ定義のヘッダ) ----

    /// <summary>LUKS2 のヘッダ (主・副の 2 つ) を作る。<paramref name="headerSize"/> は JSON の領域を含む大きさ (既定 16 KiB)。</summary>
    public static byte[] BuildLuks2(string uuid, int keySlots, string cipher = "aes-xts-plain64", int headerSize = 16384, string label = "")
    {
        var json = new StringBuilder();
        json.Append("{\"keyslots\":{");
        for (int i = 0; i < keySlots; i++)
        {
            json.Append(i > 0 ? "," : string.Empty).Append('"').Append(i).Append("\":{\"type\":\"luks2\",\"key_size\":64,\"area\":{\"type\":\"raw\",\"offset\":\"32768\",\"size\":\"258048\",\"encryption\":\"")
                .Append(cipher).Append("\",\"key_size\":64},\"kdf\":{\"type\":\"pbkdf2\",\"hash\":\"sha256\",\"iterations\":1000,\"salt\":\"AAAA\"},\"af\":{\"type\":\"luks1\",\"stripes\":4000,\"hash\":\"sha256\"}}");
        }

        json.Append("},\"tokens\":{},\"segments\":{\"0\":{\"type\":\"crypt\",\"offset\":\"16777216\",\"size\":\"dynamic\",\"iv_tweak\":\"0\",\"encryption\":\"")
            .Append(cipher).Append("\",\"sector_size\":512}},\"digests\":{\"0\":{\"type\":\"pbkdf2\",\"keyslots\":[");
        for (int i = 0; i < keySlots; i++)
        {
            json.Append(i > 0 ? "," : string.Empty).Append('"').Append(i).Append('"');
        }

        json.Append("],\"segments\":[\"0\"],\"hash\":\"sha256\",\"iterations\":1000,\"salt\":\"AAAA\",\"digest\":\"AAAA\"}},\"config\":{\"json_size\":\"12288\",\"keyslots_size\":\"16744448\"}}");
        byte[] header = new byte[headerSize * 2];
        for (int copy = 0; copy < 2; copy++)
        {
            Span<byte> h = header.AsSpan(copy * headerSize, headerSize);
            if (copy == 0)
            {
                Magic.CopyTo(h);
            }
            else
            {
                "SKUL"u8.CopyTo(h);
                h[4] = 0xBA;
                h[5] = 0xBE;
            }

            BinaryPrimitives.WriteUInt16BigEndian(h[6..], 2);
            BinaryPrimitives.WriteUInt64BigEndian(h[8..], (ulong)headerSize);
            BinaryPrimitives.WriteUInt64BigEndian(h[16..], 1);
            Encoding.ASCII.GetBytes(label.Length > 47 ? label[..47] : label, h[24..72]);
            "sha256"u8.CopyTo(h[72..]);
            Encoding.ASCII.GetBytes(uuid, h[168..208]);
            BinaryPrimitives.WriteUInt64BigEndian(h[256..], (ulong)(copy * headerSize));
            Encoding.ASCII.GetBytes(json.ToString(), h[BinaryHeaderSize..]);
        }

        return header;
    }
}
