using HexEditor.Core.Disassembly;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Inspector;

/// <summary>
/// 1 命令の逆アセンブル (INSP-16)。06 の ANA-23 の逆アセンブラと ANA-26 の構文・数値の書式を使う。起点のオフセット
/// (<see cref="InspectorOptions.DisasmAddress"/>) をアドレスとして解釈し、分岐先 (ANA-25 の求め方) をオフセットとして返す (<see cref="InspectorValue.BranchTarget"/>)。
/// </summary>
public static partial class InspectorDecoder
{
    [ThreadStatic]
    private static Dictionary<(string Arch, bool? Big, DisasmFormat Format), IInstructionDecoder>? t_decoders;

    private static InspectorValue DecodeDisassembly(string architectureId, ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states,
        Endianness endian, InspectorOptions o)
    {
        DisasmArchitecture? arch = DisasmArchitectures.Find(architectureId);
        if (arch is null)
        {
            return new InspectorValue(InspectorStatus.Invalid, o.Text.DisassemblerUnavailable, 1);
        }

        // 読み込み中・読めないバイトは命令の最大長の範囲だけを見る。
        int window = Math.Min(arch.MaxLength, data.Length);
        if (window == 0)
        {
            return new InspectorValue(InspectorStatus.NotEnoughData, o.Text.NotEnoughValue, 1, string.Format(o.Culture, o.Text.NotEnoughTip, 1, 0));
        }

        if (Check(1, data, states, o) is { } problem)
        {
            return problem;
        }

        if (!states.IsEmpty)
        {
            int ready = 0;
            while (ready < window && ready < states.Length && states[ready] is ByteState.Valid or ByteState.NoData)
            {
                ready++;
            }

            window = ready == 0 ? 1 : ready;
        }

        bool? big = Disassembler.BigEndianFor(arch, endian);
        IInstructionDecoder decoder;
        try
        {
            t_decoders ??= [];
            if (!t_decoders.TryGetValue((arch.Id, big, o.DisasmFormat), out decoder!))
            {
                decoder = Disassembler.Create(arch, o.DisasmFormat, big);
                t_decoders[(arch.Id, big, o.DisasmFormat)] = decoder;
            }
        }
        catch (DisassemblerUnavailableException ex)
        {
            return new InspectorValue(InspectorStatus.Invalid, o.Text.DisassemblerUnavailable, 1, ex.Message);
        }

        DisasmInstruction? instruction;
        try
        {
            instruction = decoder.Decode(data[..window], (ulong)Math.Max(0, o.DisasmAddress), detail: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            instruction = null;
        }

        if (instruction is not { Length: > 0 } i || i.Length > window)
        {
            return new InspectorValue(InspectorStatus.Invalid, o.Text.InvalidInstruction, arch.VariableLength ? 1 : Math.Min(arch.Alignment, data.Length));
        }

        string length = i.Length == 1 ? o.Text.OneByte : string.Format(o.Culture, o.Text.Bytes, i.Length);
        return new InspectorValue(InspectorStatus.Ok, i.Text + " " + length, i.Length)
        {
            BranchTarget = i.BranchTarget is { } target ? unchecked((long)target) : null,
        };
    }
}
