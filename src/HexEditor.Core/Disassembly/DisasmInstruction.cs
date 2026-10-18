namespace HexEditor.Core.Disassembly;

/// <summary>命令の制御の流れの種類 (分岐先への移動・分岐の線・相互参照に使う。06 の ANA-25)。</summary>
public enum FlowKind
{
    /// <summary>次の命令へ進む。</summary>
    Normal,

    /// <summary>無条件の分岐 (jmp、b など)。</summary>
    Jump,

    /// <summary>条件分岐 (jcc、beq など)。</summary>
    ConditionalJump,

    Call,
    Return,

    /// <summary>割り込み・システムコール。</summary>
    Interrupt,

    /// <summary>解釈できないバイト (<c>db 0xNN</c>。ANA-23 の仕様 5)。</summary>
    Invalid,
}

/// <summary>
/// 命令の詳細 (リスティングのツールチップ。ANA-23 の仕様 7): オペランドの種類と読み書き、使うレジスタ、命令グループ。
/// 取れない項目は空の一覧。
/// </summary>
public sealed record InstructionDetail(
    IReadOnlyList<string> Operands,
    IReadOnlyList<string> RegistersRead,
    IReadOnlyList<string> RegistersWritten,
    IReadOnlyList<string> Groups);

/// <summary>
/// 逆アセンブルした命令 1 つ。<see cref="Address"/> は表示上のアドレス (仮想アドレス)。<see cref="BranchTarget"/> は即値の分岐先
/// (call / jmp / 条件分岐。間接分岐は null で <see cref="IndirectBranch"/> が true)。<see cref="MemoryTarget"/> は RIP 相対・PC 相対の
/// メモリ参照の先 (ANA-25 の仕様 1、ANA-24 の仕様 8 のコメント)。
/// </summary>
public sealed record DisasmInstruction(
    ulong Address,
    int Length,
    string Mnemonic,
    string Operands,
    FlowKind Flow = FlowKind.Normal,
    ulong? BranchTarget = null,
    bool IndirectBranch = false,
    ulong? MemoryTarget = null,
    InstructionDetail? Detail = null)
{
    public bool IsInvalid => Flow == FlowKind.Invalid;

    /// <summary>分岐命令か (分岐の矢印を描く。ANA-25 の仕様 5)。</summary>
    public bool IsBranch => Flow is FlowKind.Jump or FlowKind.ConditionalJump or FlowKind.Call;

    /// <summary>ニーモニックとオペランドを空白 1 つでつないだ文字列。</summary>
    public string Text => Operands.Length == 0 ? Mnemonic : Mnemonic + " " + Operands;

    /// <summary>移動先 (分岐先、なければメモリ参照の先)。</summary>
    public ulong? Target => BranchTarget ?? MemoryTarget;

    /// <summary>解釈できないバイト 1 つ (<c>db 0xNN</c>)。</summary>
    public static DisasmInstruction Data(ulong address, byte value, DisasmFormat format) =>
        new(address, 1, format.UppercaseMnemonic ? "DB" : "db", format.Byte(value), FlowKind.Invalid);
}

/// <summary>x86 の構文 (ANA-26 の仕様 1)。</summary>
public enum DisasmSyntax
{
    Intel,

    /// <summary>AT&amp;T (GNU as)。オペランドのサイズの接尾辞を付ける。</summary>
    Att,
    Masm,
}

/// <summary>16 進の書式 (ANA-26 の仕様 2)。</summary>
public enum HexStyle
{
    /// <summary><c>0x10</c> (既定)。</summary>
    Prefix,

    /// <summary><c>10h</c> (先頭が文字なら <c>0FFh</c>)。</summary>
    Suffix,
}

/// <summary>アドレス列 (ANA-26 の仕様 6)。</summary>
public enum AddressColumn
{
    /// <summary>仮想アドレス (既定)。</summary>
    Virtual,
    Offset,
    Both,
}

/// <summary>
/// 逆アセンブルの表示設定 (ANA-26)。アーキテクチャごとではなく全体で共有する (仕様 8。構文は x86 にだけ使う)。
/// </summary>
public sealed record DisasmFormat
{
    public static DisasmFormat Default { get; } = new();

    public DisasmSyntax Syntax { get; init; } = DisasmSyntax.Intel;

    public HexStyle HexStyle { get; init; } = HexStyle.Prefix;

    public bool UppercaseHex { get; init; }

    /// <summary>負の即値を符号付きで表示する (<c>-0x10</c>)。false なら符号なし (<c>0xfffffff0</c>)。</summary>
    public bool SignedImmediates { get; init; }

    /// <summary>ニーモニックとレジスタ名を大文字にする (ANA-26 の仕様 3)。</summary>
    public bool UppercaseMnemonic { get; init; }

    /// <summary>ARM のレジスタの別名 (<c>sp</c>、<c>lr</c>) を使う (既定)。false なら番号 (<c>r13</c>)。ANA-26 の仕様 4。</summary>
    public bool ArmRegisterAlias { get; init; } = true;

    /// <summary>バイト列の列を表示する (仕様 5)。</summary>
    public bool ShowBytes { get; init; } = true;

    /// <summary>バイト列の列の最大バイト数 (4〜16、既定 8)。超える分は <c>…</c>。</summary>
    public int MaxBytes { get; init; } = 8;

    public AddressColumn AddressColumn { get; init; } = AddressColumn.Virtual;

    /// <summary>分岐の線を描く (ANA-25 の仕様 5)。</summary>
    public bool ShowBranchLines { get; init; } = true;

    public bool ShowComments { get; init; } = true;

    public const int MinBytes = 4;
    public const int MaxBytesLimit = 16;

    /// <summary>16 進の数値を設定の書式で書く。</summary>
    public string Hex(ulong value)
    {
        string digits = value.ToString(UppercaseHex ? "X" : "x", System.Globalization.CultureInfo.InvariantCulture);
        if (HexStyle == HexStyle.Prefix)
        {
            return "0x" + digits;
        }

        return (char.IsAsciiLetter(digits[0]) ? "0" : string.Empty) + digits + "h";
    }

    /// <summary>1 バイトの値 (2 桁。<c>db 0x06</c>)。</summary>
    public string Byte(byte value)
    {
        string digits = value.ToString(UppercaseHex ? "X2" : "x2", System.Globalization.CultureInfo.InvariantCulture);
        return HexStyle == HexStyle.Prefix ? "0x" + digits : (char.IsAsciiLetter(digits[0]) ? "0" : string.Empty) + digits + "h";
    }

    /// <summary>アドレスの表示 (桁を 8 / 16 桁にそろえる。接頭辞・接尾辞は付けない)。</summary>
    public string Address(ulong value, bool wide) =>
        value.ToString((UppercaseHex ? "X" : "x") + (wide ? "16" : "8"), System.Globalization.CultureInfo.InvariantCulture);
}
