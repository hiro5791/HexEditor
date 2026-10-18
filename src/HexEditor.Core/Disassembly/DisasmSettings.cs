namespace HexEditor.Core.Disassembly;

/// <summary>
/// 逆アセンブルの表示設定 (ANA-26) の設定キーと、設定の値から <see cref="DisasmFormat"/> を作る処理。設定はアーキテクチャごとではなく
/// 全体で共有する (仕様 8)。逆アセンブルパネルのツールバー・表示オプションと、設定画面の「逆アセンブル」が同じ値を変える。
/// </summary>
public static class DisasmSettings
{
    public const string SyntaxKey = "disassembly.syntax";
    public const string HexStyleKey = "disassembly.hexStyle";
    public const string UppercaseHexKey = "disassembly.uppercaseHex";
    public const string SignedImmediatesKey = "disassembly.signedImmediates";
    public const string UppercaseMnemonicsKey = "disassembly.uppercaseMnemonics";
    public const string ArmRegisterAliasesKey = "disassembly.armRegisterAliases";
    public const string ShowBytesKey = "disassembly.showBytes";
    public const string MaxBytesKey = "disassembly.maxBytes";
    public const string AddressColumnKey = "disassembly.addressColumn";
    public const string BranchLinesKey = "disassembly.branchLines";
    public const string CommentsKey = "disassembly.comments";

    /// <summary>すべての設定キー (変更の通知を見る)。</summary>
    public static IReadOnlyList<string> Keys { get; } =
    [
        SyntaxKey, HexStyleKey, UppercaseHexKey, SignedImmediatesKey, UppercaseMnemonicsKey, ArmRegisterAliasesKey, ShowBytesKey, MaxBytesKey,
        AddressColumnKey, BranchLinesKey, CommentsKey,
    ];

    /// <summary>設定の値から表示設定を作る (<paramref name="getString"/>・<paramref name="getBool"/>・<paramref name="getInt"/> は既定値付きの読み出し)。</summary>
    public static DisasmFormat Read(Func<string, string, string> getString, Func<string, bool, bool> getBool, Func<string, int, int> getInt) => new()
    {
        Syntax = getString(SyntaxKey, "intel") switch { "att" => DisasmSyntax.Att, "masm" => DisasmSyntax.Masm, _ => DisasmSyntax.Intel },
        HexStyle = getString(HexStyleKey, "prefix") == "suffix" ? HexStyle.Suffix : HexStyle.Prefix,
        UppercaseHex = getBool(UppercaseHexKey, false),
        SignedImmediates = getBool(SignedImmediatesKey, false),
        UppercaseMnemonic = getBool(UppercaseMnemonicsKey, false),
        ArmRegisterAlias = getBool(ArmRegisterAliasesKey, true),
        ShowBytes = getBool(ShowBytesKey, true),
        MaxBytes = Math.Clamp(getInt(MaxBytesKey, 8), DisasmFormat.MinBytes, DisasmFormat.MaxBytesLimit),
        AddressColumn = getString(AddressColumnKey, "virtual") switch { "offset" => AddressColumn.Offset, "both" => AddressColumn.Both, _ => AddressColumn.Virtual },
        ShowBranchLines = getBool(BranchLinesKey, true),
        ShowComments = getBool(CommentsKey, true),
    };

    /// <summary>構文の設定の値 (Choice の選択肢)。</summary>
    public static string SyntaxValue(DisasmSyntax syntax) => syntax switch { DisasmSyntax.Att => "att", DisasmSyntax.Masm => "masm", _ => "intel" };

    /// <summary>アドレス列の設定の値。</summary>
    public static string AddressColumnValue(AddressColumn column) => column switch { AddressColumn.Offset => "offset", AddressColumn.Both => "both", _ => "virtual" };
}
