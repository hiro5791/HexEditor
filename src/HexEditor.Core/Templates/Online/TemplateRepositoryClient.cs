using System.Security.Cryptography;
using HexEditor.Core.Security;

namespace HexEditor.Core.Templates.Online;

/// <summary>
/// HTTPS の通信 (アプリの通信の入口。App は 09 の UI-58 の NetworkClient を使い、通信の可否 (オフライン・ダウンロードの設定) を
/// 確かめる)。要求には URL だけを渡し、開いているファイルの名前や内容を渡す手段を持たない (TPL-36 の仕様 7)。
/// </summary>
public interface ITemplateFeedTransport
{
    /// <summary>
    /// GET して本文を返す。<paramref name="maxBytes"/> を超える本文は読まずに <see cref="TemplateFeedTooLargeException"/>。
    /// 接続できなければ <see cref="TemplateFeedUnavailableException"/>。
    /// </summary>
    Task<byte[]> GetAsync(Uri uri, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>リポジトリに接続できない (TPL-36 の「エラー」: 「リポジトリに接続できません: 理由」)。</summary>
public sealed class TemplateFeedUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>本文が上限 (索引 16 MB、テンプレート 4 MB) を超えた。</summary>
public sealed class TemplateFeedTooLargeException(long limit) : Exception($"The response is larger than {limit} bytes.")
{
    public long Limit { get; } = limit;
}

/// <summary>署名・ハッシュの検証に失敗した (「ファイルが改ざんされている可能性があります」)。</summary>
public sealed class TemplateTamperedException(string message) : Exception(message);

/// <summary>
/// リポジトリ (TPL-36 の仕様 1・3)。<see cref="IndexUrl"/> は索引の URL (例: <c>https://example.org/templates/index.json</c>)。
/// <see cref="PublicKeys"/> があるもの (公式のリポジトリ) は、署名を検証できない索引を使わない。利用者が追加したリポジトリは
/// 鍵を持たず、署名がなくても使う (一覧に「署名なし」と表示する)。
/// </summary>
public sealed record TemplateRepository(Uri IndexUrl, string Name, IReadOnlyList<byte[]> PublicKeys)
{
    public bool IsOfficial => PublicKeys.Count > 0;

    /// <summary>署名のファイルの URL (索引の URL に <c>.sig</c> を付けたもの)。</summary>
    public Uri SignatureUrl => new(IndexUrl.AbsoluteUri + ".sig");

    /// <summary>利用者が追加するリポジトリの URL として使えるか (HTTPS。テスト用のビルドではループバックの HTTP も可)。</summary>
    public static bool IsAllowedUrl(Uri url, bool allowLoopbackHttp) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttps || (allowLoopbackHttp && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));
}

/// <summary>読み込んだリポジトリの一覧 (エラーなら <see cref="Error"/>、テンプレートは空)。</summary>
public sealed record TemplateRepositoryListing(TemplateRepository Repository, IReadOnlyList<OnlineTemplateEntry> Templates, bool Signed,
    TemplateRepositoryError Error, string? Message)
{
    public bool Ok => Error == TemplateRepositoryError.None;
}

public enum TemplateRepositoryError
{
    None,

    /// <summary>接続できない。</summary>
    Unavailable,

    /// <summary>署名を検証できない (公式のリポジトリ)。</summary>
    Tampered,

    /// <summary>索引の形式が違う・大きすぎる。</summary>
    Invalid,

    /// <summary>通信がオフ (オフラインモード・ダウンロードの設定)。</summary>
    Disabled,
}

/// <summary>
/// オンラインのテンプレートのリポジトリから索引を読み、テンプレートをダウンロードする (TPL-36)。ダウンロードは長時間処理として
/// キャンセルできる。検証 (署名・SHA-256・大きさ) に失敗したものは使わない・書かない。
/// </summary>
public sealed class TemplateRepositoryClient(ITemplateFeedTransport transport)
{
    /// <summary>索引を読み、署名を確かめる。例外にせず結果に理由を返す (他のリポジトリの読み込みは続ける)。</summary>
    public async Task<TemplateRepositoryListing> LoadAsync(TemplateRepository repository, CancellationToken cancellationToken = default)
    {
        byte[] index;
        try
        {
            index = await transport.GetAsync(repository.IndexUrl, TemplateRepositoryIndex.MaxIndexBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (TemplateFeedTooLargeException ex)
        {
            return new(repository, [], false, TemplateRepositoryError.Invalid, ex.Message);
        }
        catch (TemplateFeedUnavailableException ex)
        {
            return new(repository, [], false, TemplateRepositoryError.Unavailable, ex.Message);
        }

        bool signed = false;
        if (repository.IsOfficial)
        {
            byte[]? signature = await TryGetSignatureAsync(repository, cancellationToken).ConfigureAwait(false);
            if (signature is null || !repository.PublicKeys.Any(k => Ed25519.Verify(k, index, signature)))
            {
                // 署名を検証できない索引は使わない (仕様 3)。
                return new(repository, [], false, TemplateRepositoryError.Tampered, "The index signature is not valid.");
            }

            signed = true;
        }

        try
        {
            TemplateRepositoryIndex parsed = TemplateRepositoryIndex.Parse(index);
            return new(repository, parsed.Templates, signed, TemplateRepositoryError.None, null);
        }
        catch (TemplateIndexFormatException ex)
        {
            return new(repository, [], signed, TemplateRepositoryError.Invalid, ex.Message);
        }
    }

    private async Task<byte[]?> TryGetSignatureAsync(TemplateRepository repository, CancellationToken cancellationToken)
    {
        byte[] raw;
        try
        {
            raw = await transport.GetAsync(repository.SignatureUrl, 4096, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TemplateFeedUnavailableException or TemplateFeedTooLargeException)
        {
            return null;
        }

        return DecodeSignature(raw);
    }

    /// <summary>64 バイトの署名、または Base64 の文字列。</summary>
    public static byte[]? DecodeSignature(byte[] raw)
    {
        if (raw.Length == Ed25519.SignatureSize)
        {
            return raw;
        }

        try
        {
            byte[] decoded = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(raw).Trim());
            return decoded.Length == Ed25519.SignatureSize ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// テンプレートのファイルをダウンロードし、索引の SHA-256 (と大きさ) と照合する。合わなければ <see cref="TemplateTamperedException"/>。
    /// </summary>
    public async Task<byte[]> DownloadAsync(TemplateRepository repository, OnlineTemplateEntry entry, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(repository.IndexUrl, entry.File, out Uri? uri))
        {
            // 索引の file が URL として読めない (索引は信頼できない入力)。
            throw new TemplateTamperedException("The template file name in the index is not valid.");
        }

        if (uri.Scheme != repository.IndexUrl.Scheme || uri.Host != repository.IndexUrl.Host || uri.Port != repository.IndexUrl.Port)
        {
            // 索引と別のサーバーのファイルは取りに行かない (索引の URL だけを信頼する)。
            throw new TemplateTamperedException("The template file is not on the repository server.");
        }

        byte[] data = await transport.GetAsync(uri, TemplateRepositoryIndex.MaxTemplateBytes, cancellationToken).ConfigureAwait(false);
        if ((entry.Size is long size && data.LongLength != size)
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), entry.Sha256, StringComparison.Ordinal))
        {
            throw new TemplateTamperedException("The SHA-256 of the downloaded file does not match the index.");
        }

        return data;
    }
}
