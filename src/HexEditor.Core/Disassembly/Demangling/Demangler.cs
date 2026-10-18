namespace HexEditor.Core.Disassembly.Demangling;

/// <summary>マングル名の方式 (ANA-30 の仕様 1)。</summary>
public enum ManglingScheme
{
    None,
    Itanium,
    Microsoft,
    RustLegacy,
    RustV0,
    D,
}

/// <summary>デマングルの表示オプション (ANA-30 の仕様 5)。</summary>
public sealed record DemangleOptions
{
    public static DemangleOptions Default { get; } = new();

    /// <summary>型情報 (引数・戻り値の型) を省いた短い名前にする。既定は完全な名前。</summary>
    public bool ShortNames { get; init; }

    /// <summary>Rust の旧方式のハッシュ部分 (<c>::h0123456789abcdef</c>) を表示する。既定は表示しない。</summary>
    public bool ShowRustHash { get; init; }
}

/// <summary>
/// デマングルの結果。<see cref="Success"/> が false なら <see cref="Text"/> は入力のまま (「デマングルできません」を表示する。
/// <see cref="Scheme"/> は判定した方式で、方式が分からないものは <see cref="ManglingScheme.None"/>)。
/// </summary>
public readonly record struct DemangleResult(bool Success, string Text, ManglingScheme Scheme)
{
    /// <summary>マングル名と判定できたが解釈できなかった (方式は分かる)。</summary>
    public bool Invalid => !Success && Scheme != ManglingScheme.None;
}

/// <summary>解釈できないマングル名 (内部で使う)。</summary>
internal sealed class DemangleException() : Exception("invalid mangled name");

/// <summary>
/// シンボル名のデマングル (ANA-30)。方式は名前の先頭から自動で判定する (仕様 2): <c>_Z</c> (Itanium。最後が <c>17h…E</c> なら Rust の旧方式)、
/// <c>?</c> (Microsoft Visual C++)、<c>_R</c> (Rust v0)、<c>_D</c> (D)。本体の自前の実装 (LLVM のデマングラの出力の形に合わせる)。
/// 入力の長さは 64 KB まで (超える部分は変換しない)。入れ子の深さに上限を設け、不正な名前でも例外・スタックオーバーフローにしない。
/// </summary>
public static class Demangler
{
    /// <summary>1 つの名前の長さの上限 (ANA-30 の「巨大ファイル・長時間処理」)。</summary>
    public const int MaxLength = 64 * 1024;

    /// <summary>入れ子の深さの上限。</summary>
    internal const int MaxDepth = 256;

    /// <summary>出力の長さの上限 (置換の組み合わせで出力が大きくなりすぎるのを防ぐ)。</summary>
    internal const int MaxOutput = 256 * 1024;

    public static ManglingScheme Detect(string name)
    {
        if (name.StartsWith("_Z", StringComparison.Ordinal) || name.StartsWith("__Z", StringComparison.Ordinal))
        {
            return RustLegacyDemangler.Looks(name) ? ManglingScheme.RustLegacy : ManglingScheme.Itanium;
        }

        if (name.StartsWith('?'))
        {
            return ManglingScheme.Microsoft;
        }

        if (name.StartsWith("_R", StringComparison.Ordinal))
        {
            return ManglingScheme.RustV0;
        }

        return name.StartsWith("_D", StringComparison.Ordinal) && name.Length > 2 && char.IsAsciiDigit(name[2]) ? ManglingScheme.D : ManglingScheme.None;
    }

    public static DemangleResult Demangle(string name) => Demangle(name, DemangleOptions.Default);

    public static DemangleResult Demangle(string name, DemangleOptions options)
    {
        string trimmed = name.Trim();
        ManglingScheme scheme = Detect(trimmed);
        if (scheme == ManglingScheme.None)
        {
            return new DemangleResult(false, name, ManglingScheme.None);
        }

        if (trimmed.Length > MaxLength)
        {
            return new DemangleResult(false, name, scheme);
        }

        try
        {
            string? text = scheme switch
            {
                ManglingScheme.Itanium => new ItaniumDemangler(trimmed.StartsWith("__Z", StringComparison.Ordinal) ? trimmed[1..] : trimmed, options).Run(),
                ManglingScheme.RustLegacy => RustLegacyDemangler.Demangle(trimmed, options),
                ManglingScheme.Microsoft => new MicrosoftDemangler(trimmed, options).Run(),
                ManglingScheme.RustV0 => new RustV0Demangler(trimmed, options).Run(),
                ManglingScheme.D => new DDemangler(trimmed, options).Run(),
                _ => null,
            };
            return text is null || text.Length > MaxOutput ? new DemangleResult(false, name, scheme) : new DemangleResult(true, text, scheme);
        }
        catch (Exception ex) when (ex is DemangleException or IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException
            or OverflowException or InvalidOperationException or InsufficientExecutionStackException)
        {
            return new DemangleResult(false, name, scheme);
        }
    }

    /// <summary>
    /// 複数行の入力を 1 行ずつ変換する (「デマングル」ダイアログ。ANA-30 の仕様 3)。1 万行を超える入力はバックグラウンドで呼ぶ。
    /// </summary>
    public static IReadOnlyList<DemangleResult> DemangleLines(string text, DemangleOptions options, CancellationToken token = default)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int count = lines.Length > 1 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var results = new DemangleResult[count];
        for (int i = 0; i < count; i++)
        {
            if ((i & 1023) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            results[i] = Demangle(lines[i], options);
        }

        return results;
    }

    /// <summary>ダイアログをバックグラウンドで変換する行数の境目 (ANA-30 の「巨大ファイル・長時間処理」)。</summary>
    public const int BackgroundLineCount = 10_000;
}
