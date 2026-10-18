using HexEditor.Core.Inspector;

namespace HexEditor.Core.Disassembly;

/// <summary>
/// 1 命令ずつ解釈する逆アセンブラ。スレッドセーフではない (使うスレッドごとに <see cref="Disassembler.Create"/> で作る)。
/// </summary>
public interface IInstructionDecoder : IDisposable
{
    DisasmArchitecture Architecture { get; }

    /// <summary>
    /// <paramref name="code"/> の先頭の 1 命令を解釈する。解釈できなければ null (呼び出し側が <c>db</c> にする)。
    /// <paramref name="detail"/> が true なら <see cref="DisasmInstruction.Detail"/> も作る (ツールチップ用。遅い)。
    /// </summary>
    DisasmInstruction? Decode(ReadOnlySpan<byte> code, ulong address, bool detail = false);
}

/// <summary>逆アセンブルエンジンを読み込めない (Capstone のネイティブライブラリがないなど。ANA-23 の「エラー」)。</summary>
public sealed class DisassemblerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 逆アセンブラの入口 (ANA-23 の仕様 10: 逆アセンブルビュー、インスペクタの 1 命令 (INSP-16)、テンプレートのビジュアライザ (TPL-16)、
/// スクリプト API が同じものを使う)。
/// </summary>
public static class Disassembler
{
    /// <summary>
    /// 逆アセンブラを作る。<paramref name="bigEndian"/> はエンディアンを持つアーキテクチャのときだけ使う (null ならアーキテクチャの既定)。
    /// エンジンを読み込めなければ <see cref="DisassemblerUnavailableException"/>。
    /// </summary>
    public static IInstructionDecoder Create(DisasmArchitecture architecture, DisasmFormat format, bool? bigEndian = null)
    {
        bool big = architecture.HasEndian ? bigEndian ?? architecture.BigEndianDefault : architecture.BigEndianDefault;
        return architecture.Engine switch
        {
            DisasmEngine.Iced => new IcedDecoder(architecture, format),
            DisasmEngine.Capstone => new CapstoneDecoder(architecture, format, big),
            DisasmEngine.Plugin => new PluginDecoder(architecture, format),
            _ => architecture.Family switch
            {
                "RISC-V" => new RiscVDecoder(architecture, format),
                "6502" => new Mos65xxDecoder(architecture, format),
                "BPF" => new BpfDecoder(architecture, format, big),
                "WebAssembly" => new WasmDecoder(architecture, format),
                _ => throw new DisassemblerUnavailableException("Unknown architecture: " + architecture.Id),
            },
        };
    }

    public static IInstructionDecoder Create(string architectureId, DisasmFormat format, bool? bigEndian = null) =>
        Create(DisasmArchitectures.Get(architectureId), format, bigEndian);

    /// <summary>
    /// 1 命令を解釈する。解釈できなければ <c>db</c> の 1 バイト。データが足りないときは null。
    /// </summary>
    public static DisasmInstruction DecodeOrData(IInstructionDecoder decoder, ReadOnlySpan<byte> code, ulong address, DisasmFormat format, bool detail = false)
    {
        DisasmInstruction? instruction = null;
        try
        {
            instruction = decoder.Decode(code, address, detail);
        }
        catch (Exception ex) when (ex is not DisassemblerUnavailableException && ex is not OutOfMemoryException)
        {
            // プラグインなどの失敗は解釈できないバイトとして扱う。
        }

        return instruction is { Length: > 0 } i && i.Length <= code.Length ? i : DisasmInstruction.Data(address, code[0], format);
    }

    /// <summary>
    /// 線形スイープで解釈する (ANA-23 の仕様 5)。<paramref name="maxInstructions"/> 個か、データの終わりまで。
    /// </summary>
    public static IReadOnlyList<DisasmInstruction> Sweep(IInstructionDecoder decoder, ReadOnlySpan<byte> code, ulong address, DisasmFormat format,
        int maxInstructions = int.MaxValue, bool detail = false)
    {
        var list = new List<DisasmInstruction>();
        int at = 0;
        DisasmArchitecture arch = decoder.Architecture;
        int alignment = arch.VariableLength ? 1 : Math.Max(1, arch.Alignment);
        while (at < code.Length && list.Count < maxInstructions)
        {
            // 固定長の命令のアーキテクチャでは、解釈できない語の各バイトを db にし、次の命令は語の境界から解釈する。
            DisasmInstruction i = at % alignment != 0
                ? DisasmInstruction.Data(address + (ulong)at, code[at], format)
                : DecodeOrData(decoder, code[at..], address + (ulong)at, format, detail);
            if (i.IsInvalid && alignment > 1)
            {
                i = i with { Length = 1 };
            }

            list.Add(i);
            at += i.Length;
        }

        return list;
    }

    /// <summary>エンディアンの選択 (インスペクタのエンディアンに従う。INSP-16 の仕様 1)。</summary>
    public static bool? BigEndianFor(DisasmArchitecture architecture, Endianness endian) =>
        architecture.HasEndian ? endian == Endianness.Big : null;
}
