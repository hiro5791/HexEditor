using System.Runtime.InteropServices;

namespace HexEditor.Core.Editing.Codecs;

/// <summary>
/// liblzma (XZ Utils 5.6.4、0BSD。Joveler.Compression.XZ のパッケージのネイティブ DLL) の呼び出し。XZ (<c>.xz</c>) と LZMA (<c>.lzma</c>、
/// LZMA_Alone) の圧縮・展開に使う (EDIT-41)。展開ではメモリの上限 (<see cref="DecoderMemoryLimit"/>) を渡し、辞書の大きさを偽ったデータで
/// メモリを使い切らないようにする。<c>lzma_stream</c> は 64 ビット (x64・arm64) の配置。
/// </summary>
internal static unsafe partial class LibLzma
{
    private const string Library = "liblzma";

    /// <summary>展開に使うメモリの上限 (xz -9 の展開に必要な約 65 MiB の 16 倍)。</summary>
    public const ulong DecoderMemoryLimit = 1024UL * 1024 * 1024;

    public enum Ret
    {
        Ok = 0,
        StreamEnd = 1,
        NoCheck = 2,
        UnsupportedCheck = 3,
        GetCheck = 4,
        MemError = 5,
        MemLimitError = 6,
        FormatError = 7,
        OptionsError = 8,
        DataError = 9,
        BufError = 10,
        ProgError = 11,
    }

    public enum Action
    {
        Run = 0,
        Finish = 3,
    }

    /// <summary><c>lzma_stream</c> (LZMA_STREAM_INIT はすべて 0)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Stream
    {
        public byte* NextIn;
        public nuint AvailIn;
        public ulong TotalIn;
        public byte* NextOut;
        public nuint AvailOut;
        public ulong TotalOut;
        public nint Allocator;
        public nint Internal;
        public nint ReservedPtr1;
        public nint ReservedPtr2;
        public nint ReservedPtr3;
        public nint ReservedPtr4;
        public ulong SeekPos;
        public ulong ReservedInt2;
        public nuint ReservedInt3;
        public nuint ReservedInt4;
        public int ReservedEnum1;
        public int ReservedEnum2;
    }

    /// <summary>CRC64 (xz コマンドの既定の整合性チェック)。</summary>
    public const int CheckCrc64 = 4;

    [LibraryImport(Library, EntryPoint = "lzma_easy_encoder")]
    public static partial Ret EasyEncoder(Stream* stream, uint preset, int check);

    [LibraryImport(Library, EntryPoint = "lzma_stream_decoder")]
    public static partial Ret StreamDecoder(Stream* stream, ulong memlimit, uint flags);

    [LibraryImport(Library, EntryPoint = "lzma_alone_encoder")]
    public static partial Ret AloneEncoder(Stream* stream, void* options);

    [LibraryImport(Library, EntryPoint = "lzma_alone_decoder")]
    public static partial Ret AloneDecoder(Stream* stream, ulong memlimit);

    /// <summary>プリセットの <c>lzma_options_lzma</c> を作る (失敗すると 0 以外)。</summary>
    [LibraryImport(Library, EntryPoint = "lzma_lzma_preset")]
    public static partial byte LzmaPreset(void* options, uint preset);

    [LibraryImport(Library, EntryPoint = "lzma_code")]
    public static partial Ret Code(Stream* stream, Action action);

    [LibraryImport(Library, EntryPoint = "lzma_end")]
    public static partial void End(Stream* stream);

    /// <summary>liblzma を読み込めるか (読み込めなければ XZ・LZMA を一覧で無効にする)。</summary>
    public static bool IsAvailable(out string? reason)
    {
        try
        {
            Stream s = default;
            Ret r = AloneDecoder(&s, DecoderMemoryLimit);
            End(&s);
            reason = r == Ret.Ok ? null : r.ToString();
            return r == Ret.Ok;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            reason = e.Message;
            return false;
        }
    }
}
