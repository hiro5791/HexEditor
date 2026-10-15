using HexEditor.Core.Scripting;

namespace HexEditor.Core.Plugins;

/// <summary>署名の状態 (AUTO-35 の仕様 1)。</summary>
public enum PluginSignatureState
{
    /// <summary>署名されていない (読み込みは可能。信頼の確認で警告を強める)。</summary>
    Unsigned,

    /// <summary>有効な署名がある。</summary>
    Valid,

    /// <summary>署名が無効 (改ざん、期限切れの証明書でタイムスタンプなし、失効、信頼されていないルート)。読み込まない。</summary>
    Invalid,
}

/// <summary>
/// 1 つのファイルの署名の確認の結果。<see cref="Thumbprint"/> は署名者の証明書の SHA-256 (発行者を信頼する記録に使う)。
/// <see cref="RevocationUnchecked"/> は失効を確認できなかった (オフライン。署名ありとして扱い、その旨を表示する。AUTO-35 の仕様 5)。
/// </summary>
public sealed record FileSignature(PluginSignatureState State, string? Publisher = null, string? Thumbprint = null, string? Problem = null,
    bool RevocationUnchecked = false)
{
    public static FileSignature Unsigned { get; } = new(PluginSignatureState.Unsigned);
}

/// <summary>Authenticode の署名の確認 (AUTO-35)。App・CLI は WinVerifyTrust を使う (Platform の AuthenticodeVerifier)。</summary>
public interface IPluginSignatureVerifier
{
    /// <summary>ファイルの署名を確かめる。確認に失敗した場合は <see cref="PluginSignatureState.Invalid"/> を返す (エラーを「有効」にしない)。</summary>
    FileSignature Verify(string path);
}

/// <summary>プラグインのフォルダの署名の状態 (全 DLL をまとめたもの)。</summary>
/// <param name="Files">DLL ごとの結果 (相対パス)。</param>
public sealed record PluginSignatureInfo(PluginSignatureState State, string? Publisher, string? Thumbprint, string? Problem, bool RevocationUnchecked,
    IReadOnlyDictionary<string, FileSignature> Files)
{
    public static PluginSignatureInfo Unchecked { get; } = new(PluginSignatureState.Unsigned, null, null, null, false, new Dictionary<string, FileSignature>());
}

/// <summary>フォルダの署名の確認 (AUTO-35 の仕様 1)。</summary>
public static class PluginSignatures
{
    /// <summary>
    /// フォルダ内のすべての <c>.dll</c> の署名を確かめてまとめる:
    /// 1 つでも無効なら無効、1 つでも署名なしなら署名なし、すべて有効なら有効 (発行者は入口のアセンブリの署名者。
    /// 全 DLL が同じ証明書で署名されている場合だけ <see cref="PluginSignatureInfo.Thumbprint"/> を持つ)。
    /// 確認の途中の例外は無効として扱う。
    /// </summary>
    public static PluginSignatureInfo VerifyFolder(string folder, string entry, IPluginSignatureVerifier verifier)
    {
        var files = new SortedDictionary<string, FileSignature>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(folder, "*.dll", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            FileSignature result;
            try
            {
                result = verifier.Verify(file);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new FileSignature(PluginSignatureState.Invalid, Problem: ex.Message);
            }

            files[relative] = result;
        }

        if (files.Count == 0)
        {
            return new PluginSignatureInfo(PluginSignatureState.Unsigned, null, null, null, false, files);
        }

        if (files.FirstOrDefault(f => f.Value.State == PluginSignatureState.Invalid) is { Key: not null } invalid)
        {
            return new PluginSignatureInfo(PluginSignatureState.Invalid, null, null,
                Msg.T($"署名が無効です: {invalid.Key} ({invalid.Value.Problem})", $"Invalid signature: {invalid.Key} ({invalid.Value.Problem})"), false, files);
        }

        if (files.Any(f => f.Value.State == PluginSignatureState.Unsigned))
        {
            return new PluginSignatureInfo(PluginSignatureState.Unsigned, null, null, null, false, files);
        }

        FileSignature main = files.TryGetValue(entry, out FileSignature? e) ? e : files.Values.First();
        string? thumbprint = files.Values.Select(f => f.Thumbprint).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? main.Thumbprint : null;
        return new PluginSignatureInfo(PluginSignatureState.Valid, main.Publisher, thumbprint, null, files.Values.Any(f => f.RevocationUnchecked), files);
    }
}
