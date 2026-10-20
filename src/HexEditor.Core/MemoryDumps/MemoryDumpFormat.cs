using System.Buffers.Binary;

namespace HexEditor.Core.MemoryDumps;

/// <summary>メモリダンプの種類 (01 の ENG-36)。</summary>
public enum MemoryDumpKind
{
    /// <summary>物理メモリのイメージ (raw。WinPmem などの <c>.raw</c> <c>.mem</c> <c>.bin</c>)。オフセット = 物理アドレス (仕様 2)。</summary>
    Raw,

    /// <summary>Windows の完全メモリダンプ (物理メモリの範囲の一覧。仕様 3)。</summary>
    CompleteDump,

    /// <summary>Windows のカーネルメモリダンプ (ページのビットマップ。要約ダンプ・自動メモリダンプを含む)。</summary>
    KernelDump,

    /// <summary>Windows の完全メモリダンプ (ページのビットマップの形式)。</summary>
    BitmapCompleteDump,

    /// <summary>ミニダンプ (MDMP。プロセスの仮想アドレス空間。仕様 4)。</summary>
    Minidump,
}

/// <summary>開けないダンプの形式 (仕様 6 の圧縮されたダンプ・休止状態のファイルなど)。</summary>
public enum UnsupportedDumpKind
{
    None,

    /// <summary>休止状態のファイル (hiberfil.sys。圧縮されている)。</summary>
    Hibernation,

    /// <summary>トリアージダンプなど、物理メモリの範囲を持たないカーネルのダンプ、または知らない形式のダンプの種類。</summary>
    UnknownDumpType,
}

/// <summary>先頭のシグネチャで判定した結果 (仕様 5)。</summary>
public sealed record MemoryDumpDetection(MemoryDumpKind Kind, UnsupportedDumpKind Unsupported = UnsupportedDumpKind.None, int DumpType = 0, bool Is64Bit = true)
{
    /// <summary>形式としては分かったが、この版では読めない。</summary>
    public bool IsUnsupported => Unsupported != UnsupportedDumpKind.None;
}

/// <summary>メモリダンプの形式が壊れている (エラー: 「メモリダンプの形式を読めませんでした。raw として開きますか」)。</summary>
public sealed class MemoryDumpFormatException(string message) : IOException(message);

/// <summary>メモリダンプの形式の判定 (仕様 5: ファイルの先頭のシグネチャ。判定できなければ raw)。</summary>
public static class MemoryDumpFormat
{
    /// <summary>Windows のクラッシュダンプの DumpType。</summary>
    public const int DumpTypeFull = 1;
    public const int DumpTypeSummary = 2;
    public const int DumpTypeHeader = 3;
    public const int DumpTypeTriage = 4;
    public const int DumpTypeBitmapFull = 5;
    public const int DumpTypeBitmapKernel = 6;
    public const int DumpTypeAutomatic = 7;

    /// <summary>先頭の 8 バイト (と 64 bit のヘッダの DumpType) から判定する。</summary>
    public static MemoryDumpDetection Detect(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[..4].SequenceEqual("MDMP"u8))
        {
            return new MemoryDumpDetection(MemoryDumpKind.Minidump);
        }

        if (head.Length >= 4 && (head[..4].SequenceEqual("hibr"u8) || head[..4].SequenceEqual("HIBR"u8) || head[..4].SequenceEqual("wake"u8)
            || head[..4].SequenceEqual("WAKE"u8) || head[..4].SequenceEqual("RSTR"u8)))
        {
            return new MemoryDumpDetection(MemoryDumpKind.Raw, UnsupportedDumpKind.Hibernation);
        }

        if (head.Length >= 8 && head[..4].SequenceEqual("PAGE"u8))
        {
            bool is64 = head.Slice(4, 4).SequenceEqual("DU64"u8);
            bool is32 = head.Slice(4, 4).SequenceEqual("DUMP"u8);
            if (!is64 && !is32)
            {
                return new MemoryDumpDetection(MemoryDumpKind.Raw);
            }

            int typeAt = is64 ? CrashDumpHeader.DumpTypeOffset64 : CrashDumpHeader.DumpTypeOffset32;
            int type = head.Length >= typeAt + 4 ? BinaryPrimitives.ReadInt32LittleEndian(head[typeAt..]) : 0;
            return type switch
            {
                DumpTypeFull => new MemoryDumpDetection(MemoryDumpKind.CompleteDump, DumpType: type, Is64Bit: is64),
                DumpTypeSummary or DumpTypeBitmapKernel or DumpTypeAutomatic => new MemoryDumpDetection(MemoryDumpKind.KernelDump, DumpType: type, Is64Bit: is64),
                DumpTypeBitmapFull => new MemoryDumpDetection(MemoryDumpKind.BitmapCompleteDump, DumpType: type, Is64Bit: is64),
                _ => new MemoryDumpDetection(MemoryDumpKind.CompleteDump, UnsupportedDumpKind.UnknownDumpType, type, is64),
            };
        }

        return new MemoryDumpDetection(MemoryDumpKind.Raw);
    }

    /// <summary>ファイルの先頭を読んで判定する。</summary>
    public static MemoryDumpDetection Detect(string path)
    {
        byte[] head = new byte[CrashDumpHeader.HeaderSize64];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        return Detect(head.AsSpan(0, read));
    }
}
