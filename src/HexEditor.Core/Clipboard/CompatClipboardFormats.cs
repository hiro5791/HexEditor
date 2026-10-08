using System.Buffers.Binary;

namespace HexEditor.Core.Clipboard;

/// <summary>他のエディタのクリップボード形式のデータの配置 (EDIT-27 の仕様 2)。</summary>
public enum CompatLayout
{
    /// <summary>バイト列そのもの。</summary>
    Raw,

    /// <summary>先頭に 4 バイト (リトルエンディアン) の長さを置き、その後にバイト列。</summary>
    LengthPrefixed32,
}

/// <summary>他のエディタのクリップボード形式 1 つ (EDIT-27 の仕様 2 の表の 1 行)。</summary>
/// <param name="Editor">エディタの名前。</param>
/// <param name="FormatName">登録クリップボード形式名 (<c>RegisterClipboardFormat</c> の名前)。</param>
/// <param name="Layout">データの配置。</param>
public sealed record CompatClipboardFormat(string Editor, string FormatName, CompatLayout Layout);

/// <summary>
/// 他のバイナリエディタとのクリップボード互換 (EDIT-27)。形式の作成と解釈だけを行い、OS のクリップボードには触れない
/// (App が形式名ごとのバイト列をクリップボードとやり取りする)。表 (<see cref="Formats"/>) には、データの配置を確かめた形式だけを載せる
/// (仕様 2。確かめていない形式は載せない)。
/// </summary>
public static class CompatClipboardFormats
{
    /// <summary>
    /// 対応する形式 (貼り付けで探す順。仕様 3)。Frhed (WinMerge) の <c>BinaryData</c> は、公開されているソースコードの記述
    /// (先頭に 4 バイトの長さを置く) に従う。実機での確認は手動テスト TC-EDIT-27-08 で行う。それ以外のエディタは未決定のため載せない。
    /// </summary>
    public static IReadOnlyList<CompatClipboardFormat> Formats { get; } =
    [
        new("Frhed", "BinaryData", CompatLayout.LengthPrefixed32),
    ];

    /// <summary>設定「他のエディタ互換の形式でもコピーする」のキー (既定オン。仕様 4)。</summary>
    public const string SettingKey = "clipboard.compatFormats";

    /// <summary>コピーで入れるデータ (形式名 → バイト列)。</summary>
    public static IReadOnlyList<(string FormatName, byte[] Data)> Encode(ReadOnlySpan<byte> data, IReadOnlyList<CompatClipboardFormat>? formats = null)
    {
        var result = new List<(string, byte[])>();
        foreach (CompatClipboardFormat format in formats ?? Formats)
        {
            result.Add((format.FormatName, Encode(format.Layout, data)));
        }

        return result;
    }

    public static byte[] Encode(CompatLayout layout, ReadOnlySpan<byte> data)
    {
        if (layout == CompatLayout.Raw)
        {
            return data.ToArray();
        }

        byte[] bytes = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)data.Length);
        data.CopyTo(bytes.AsSpan(4));
        return bytes;
    }

    /// <summary>
    /// 形式のデータを解釈する。壊れている (長さの前置が実データより大きいなど) 場合は false (仕様の「エラー」)。
    /// クリップボードのデータは確保の都合で後ろに余りが付くことがあるため、前置の長さより長いデータは前置の長さで切る。
    /// </summary>
    public static bool TryDecode(CompatLayout layout, ReadOnlySpan<byte> raw, out byte[] data)
    {
        if (layout == CompatLayout.Raw)
        {
            data = raw.ToArray();
            return true;
        }

        data = [];
        if (raw.Length < 4)
        {
            return false;
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        if (length > raw.Length - 4)
        {
            return false;
        }

        data = raw.Slice(4, (int)length).ToArray();
        return true;
    }

    /// <summary>
    /// 貼り付けで使う形式を探す (仕様 3)。<paramref name="read"/> は形式名のデータを返す (クリップボードにない形式は null)。
    /// 表の順に探し、最初に正しく解釈できた形式のデータを返す。壊れた形式は飛ばして次を探す。見つからなければ null。
    /// </summary>
    public static (CompatClipboardFormat Format, byte[] Data)? FindForPaste(Func<string, byte[]?> read, IReadOnlyList<CompatClipboardFormat>? formats = null)
    {
        foreach (CompatClipboardFormat format in formats ?? Formats)
        {
            if (read(format.FormatName) is { } raw && TryDecode(format.Layout, raw, out byte[] data))
            {
                return (format, data);
            }
        }

        return null;
    }
}
