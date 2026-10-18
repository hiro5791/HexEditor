using System.Diagnostics;

namespace HexEditor.Core.Disassembly;

/// <summary>
/// プラグインの逆アセンブラ (08 の AUTO-31 の <c>IDisassembler</c>) を本体の逆アセンブラとして使う。命令の文字列は最初の空白で
/// ニーモニックとオペランドに分ける。分岐の種類はプラグインが返さないので「次へ進む」とする。
/// 1 回の呼び出しが 100 ms を超えることが 2 回続いたら、それ以降は呼ばない (AUTO-31 の仕様 5、AUTO-30 の注記と同じ扱い)。
/// </summary>
internal sealed class PluginDecoder : IInstructionDecoder
{
    /// <summary>遅い呼び出しとみなす時間。</summary>
    public static readonly TimeSpan SlowCall = TimeSpan.FromMilliseconds(100);

    private readonly HexEditor.Sdk.Plugins.IDisassembler _plugin;
    private readonly DisasmFormat _format;
    private int _slowCalls;

    public PluginDecoder(DisasmArchitecture architecture, DisasmFormat format)
    {
        Architecture = architecture;
        _plugin = architecture.Plugin ?? throw new DisassemblerUnavailableException("No plugin disassembler");
        _format = format;
    }

    public DisasmArchitecture Architecture { get; }

    /// <summary>遅い呼び出しが続いたため止めた。</summary>
    public bool Stopped => _slowCalls >= 2;

    public void Dispose()
    {
    }

    public DisasmInstruction? Decode(ReadOnlySpan<byte> code, ulong address, bool detail = false)
    {
        if (Stopped)
        {
            throw new DisassemblerUnavailableException("The plugin disassembler was stopped because it was too slow");
        }

        long start = Stopwatch.GetTimestamp();
        IReadOnlyList<HexEditor.Sdk.Plugins.DisassembledInstruction> list = _plugin.Disassemble(code, (long)address, 1);
        _slowCalls = Stopwatch.GetElapsedTime(start) > SlowCall ? _slowCalls + 1 : 0;
        if (list.Count == 0 || list[0] is not { Length: > 0 } first || first.Length > code.Length)
        {
            return null;
        }

        string text = (first.Text ?? string.Empty).Trim();
        int space = text.IndexOfAny([' ', '\t']);
        string mnemonic = space < 0 ? text : text[..space];
        string operands = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        if (_format.UppercaseMnemonic)
        {
            mnemonic = mnemonic.ToUpperInvariant();
        }

        return new DisasmInstruction(address, first.Length, mnemonic, operands);
    }
}
