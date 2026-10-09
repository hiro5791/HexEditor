using System.Security.Cryptography;

namespace HexEditor.Core.Hashing;

/// <summary>アルゴリズムが持つパラメータ (ANA-19 の表の「パラメータ」)。</summary>
[Flags]
public enum HashParameterKinds
{
    None = 0,
    Seed = 1,
    Complement = 2,

    /// <summary>ワードのエンディアン。</summary>
    Endian = 4,

    /// <summary>ワードの符号 (符号あり / なし)。</summary>
    Signed = 8,

    /// <summary>鍵。</summary>
    Key = 16,

    /// <summary>出力長。</summary>
    OutputLength = 32,

    /// <summary>ed2k の旧方式 / 新方式。</summary>
    Ed2kMode = 64,
}

/// <summary>パラメータの誤り (ANA-18 の「エラー」の「パラメータが不正」。その行だけ計算しない)。</summary>
public enum HashParameterError
{
    None,

    /// <summary>鍵の長さが違う。</summary>
    KeyLength,

    /// <summary>出力長が範囲外。</summary>
    OutputLength,
}

/// <summary>ハッシュパネルで選べるアルゴリズム 1 つ (ANA-19)。</summary>
public sealed class HashAlgorithmInfo
{
    private readonly Func<HashParameters, IHasher> _create;

    internal HashAlgorithmInfo(string id, string name, HashGroup group, int bits, Func<HashParameters, IHasher> create,
        IReadOnlyList<string>? aliases = null, HashParameterKinds parameters = HashParameterKinds.None, bool insecure = false,
        bool available = true, CrcParameters? crc = null, Func<HashParameters, int>? bitsFor = null,
        Func<HashParameters, HashParameterError>? validate = null, bool slow = false, bool preferBase32 = false)
    {
        Id = id;
        Name = name;
        Group = group;
        Bits = bits;
        _create = create;
        Aliases = aliases ?? [];
        Parameters = parameters;
        IsInsecure = insecure;
        IsAvailable = available;
        Crc = crc;
        _bitsFor = bitsFor;
        _validate = validate;
        IsSlow = slow;
        PrefersBase32 = preferBase32;
    }

    private readonly Func<HashParameters, int>? _bitsFor;
    private readonly Func<HashParameters, HashParameterError>? _validate;

    /// <summary>設定・スクリプトで使う識別子 (地域設定に依存しない小文字)。</summary>
    public string Id { get; }

    /// <summary>表示名 (CRC はカタログの名前)。</summary>
    public string Name { get; }

    /// <summary>別名 (絞り込みで一致させる)。</summary>
    public IReadOnlyList<string> Aliases { get; }

    public HashGroup Group { get; }

    /// <summary>出力のビット数 (出力長を選べるものは既定の長さ)。</summary>
    public int Bits { get; }

    /// <summary>パラメータに応じた出力のビット数。</summary>
    public int BitsFor(HashParameters? parameters) => _bitsFor is { } f && parameters is not null ? f(parameters) : Bits;

    /// <summary>パラメータの誤り (なければ <see cref="HashParameterError.None"/>)。</summary>
    public HashParameterError Validate(HashParameters? parameters) =>
        _validate is { } f ? f(parameters ?? HashParameters.Default) : HashParameterError.None;

    /// <summary>遅いアルゴリズム (全体の速度がこのアルゴリズムで決まることを行の注記に表示する。ANA-18 の「巨大ファイル」)。</summary>
    public bool IsSlow { get; }

    /// <summary>Base32 の表示を既定にする (TTH。ANA-19 の仕様 4)。</summary>
    public bool PrefersBase32 { get; }

    /// <summary>利用者が定義したカスタム CRC (ANA-20)。</summary>
    public bool IsCustom { get; init; }

    public HashParameterKinds Parameters { get; }

    /// <summary>安全でないことを行の注記に表示する (MD5、SHA-1)。</summary>
    public bool IsInsecure { get; }

    /// <summary>この環境で使えるか (ANA-19 の「エラー」。使えなければ一覧で無効にする)。</summary>
    public bool IsAvailable { get; }

    /// <summary>CRC のパラメータ (CRC 以外は null)。</summary>
    public CrcParameters? Crc { get; }

    /// <summary>64 bit 以下の値 (10 進表示とバイト順の選択ができる。ANA-18 の仕様 5・6)。</summary>
    public bool IsNumeric => Bits <= 64 && Group is HashGroup.Checksum or HashGroup.Crc or HashGroup.NonCryptographic;

    public IHasher CreateHasher(HashParameters? parameters = null)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException($"{Name} はこの環境では利用できません。");
        }

        return _create(parameters ?? HashParameters.Default);
    }

    /// <summary>名前・別名が絞り込みの文字列を含むか (大文字・小文字を区別しない序数比較。ANA-19 の「画面」)。</summary>
    public bool MatchesFilter(string filter)
    {
        filter = filter.Trim();
        return filter.Length == 0
            || Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Aliases.Any(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>結果の行に出す名前。既定と異なるパラメータを付ける (例: <c>xxHash64 (seed=0x1234)</c>。ANA-18 の仕様 8)。</summary>
    public string DisplayName(HashParameters? parameters)
    {
        parameters ??= HashParameters.Default;
        var parts = new List<string>();
        if (Parameters.HasFlag(HashParameterKinds.Seed) && parameters.Seed != 0)
        {
            parts.Add($"seed=0x{parameters.Seed:X}");
        }

        if (Parameters.HasFlag(HashParameterKinds.Complement) && parameters.Complement != HashComplement.None)
        {
            parts.Add(parameters.Complement == HashComplement.Ones ? "ones' complement" : "two's complement");
        }

        if (Parameters.HasFlag(HashParameterKinds.Endian) && parameters.BigEndian is bool be)
        {
            parts.Add(be ? "BE" : "LE");
        }

        if (Parameters.HasFlag(HashParameterKinds.Signed) && parameters.Signed)
        {
            parts.Add("signed");
        }

        if (Parameters.HasFlag(HashParameterKinds.Key) && parameters.KeyHex is { Length: > 0 } key)
        {
            parts.Add($"key={key}");
        }

        if (Parameters.HasFlag(HashParameterKinds.OutputLength) && parameters.OutputBits != 0 && parameters.OutputBits != Bits)
        {
            parts.Add($"{parameters.OutputBits} bit");
        }

        if (Parameters.HasFlag(HashParameterKinds.Ed2kMode) && parameters.Ed2k == Ed2kMode.Red)
        {
            parts.Add("red");
        }

        return parts.Count == 0 ? Name : $"{Name} ({string.Join(", ", parts)})";
    }

    public override string ToString() => Name;
}

/// <summary>最初から用意するアルゴリズムのセット (ANA-18 の仕様 3)。</summary>
public sealed record HashAlgorithmSet(string Id, IReadOnlyList<string> AlgorithmIds);

/// <summary>
/// 対応するアルゴリズムの一覧 (ANA-19)。チェックサム・CRC・非暗号学的ハッシュは HashCatalog.Checksums.cs、暗号学的ハッシュは
/// HashCatalog.Cryptographic.cs に表の順で並べる。利用者のカスタム CRC (ANA-20) は <see cref="Custom"/>。
/// </summary>
public static partial class HashCatalog
{
    public static readonly CrcParameters Crc16Arc = new(16, 0x8005, 0, true, true, 0, 0xBB3D);
    public static readonly CrcParameters Crc16Ibm3740 = new(16, 0x1021, 0xFFFF, false, false, 0, 0x29B1);
    public static readonly CrcParameters Crc32 = new(32, 0x04C11DB7, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0xCBF43926);
    public static readonly CrcParameters Crc32C = new(32, 0x1EDC6F41, 0xFFFFFFFF, true, true, 0xFFFFFFFF, 0xE3069283);

    /// <summary>最初から用意する一覧 (グループの順: チェックサム、CRC、非暗号学的ハッシュ、暗号学的ハッシュ。グループの中は表の順)。</summary>
    public static IReadOnlyList<HashAlgorithmInfo> BuiltIn { get; } = [.. ChecksumAlgorithms(), .. CryptographicAlgorithms()];

    private static IReadOnlyList<HashAlgorithmInfo> _custom = [];

    /// <summary>利用者が定義したカスタム CRC (ANA-20。設定から読み込んだもの)。</summary>
    public static IReadOnlyList<HashAlgorithmInfo> Custom => _custom;

    /// <summary>一覧の順の全アルゴリズム (カスタム CRC は CRC のグループの最後)。</summary>
    public static IReadOnlyList<HashAlgorithmInfo> All { get; private set; } = BuiltIn;

    /// <summary>カスタム CRC の一覧が変わった。</summary>
    public static event EventHandler? CustomChanged;

    /// <summary>カスタム CRC の一覧を置き換える (ANA-20 の仕様 5)。</summary>
    public static void SetCustom(IReadOnlyList<HashAlgorithmInfo> custom)
    {
        _custom = [.. custom];
        int lastCrc = BuiltIn.ToList().FindLastIndex(a => a.Group == HashGroup.Crc);
        All = [.. BuiltIn.Take(lastCrc + 1), .. _custom, .. BuiltIn.Skip(lastCrc + 1)];
        CustomChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>最初から用意するセット (ANA-18 の仕様 3)。一覧にない (この版で未対応の) アルゴリズムは含めない。</summary>
    public static IReadOnlyList<HashAlgorithmSet> BuiltInSets { get; } =
    [
        new("common", ["crc32", "md5", "sha1", "sha256"]),
        new("cryptographic", Existing("md5", "sha1", "sha256", "sha512", "sha3-256", "blake3")),
        new("checksums", Existing("sum8", "sum16", "sum32", "xor8", "crc16-arc", "crc32", "adler32")),
    ];

    /// <summary>既定のセット (「よく使う」)。</summary>
    public static HashAlgorithmSet DefaultSet => BuiltInSets[0];

    public static HashAlgorithmInfo? Find(string idOrName)
    {
        foreach (HashAlgorithmInfo a in All)
        {
            if (string.Equals(a.Id, idOrName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Name, idOrName, StringComparison.OrdinalIgnoreCase)
                || a.Aliases.Any(x => string.Equals(x, idOrName, StringComparison.OrdinalIgnoreCase)))
            {
                return a;
            }
        }

        return null;
    }

    public static HashAlgorithmInfo Get(string id) => Find(id) ?? throw new KeyNotFoundException($"未知のアルゴリズム: {id}");

    /// <summary>絞り込み (ANA-19 の「画面」)。</summary>
    public static IEnumerable<HashAlgorithmInfo> Filter(string filter) => All.Where(a => a.MatchesFilter(filter));

    private static string[] Existing(params string[] ids) =>
        [.. ids.Where(id => BuiltIn.Any(a => a.Id == id && a.IsAvailable))];
}
