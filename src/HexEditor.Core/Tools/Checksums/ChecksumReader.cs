using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.Core.Hashing;

namespace HexEditor.Core.Tools.Checksums;

/// <summary>チェックサムファイルの 1 行 (TOOL-19)。<see cref="AlgorithmId"/> は行の形 (BSD の行頭) ・ファイルの名前と拡張子・値の長さから決める。</summary>
public sealed record ChecksumLineEntry(int LineNumber, string Path, byte[] Value, string? AlgorithmId, bool Binary);

/// <summary>書式の誤りで飛ばした行 (TOOL-19 の「エラー」: 行番号を示す)。</summary>
public sealed record ChecksumLineError(int LineNumber, string Text);

/// <summary>読み込んだチェックサムファイル。</summary>
public sealed class ChecksumDocument
{
    public required ChecksumFormat Format { get; init; }

    /// <summary>ファイル全体のアルゴリズム (拡張子・ファイル名から)。分からなければ null。</summary>
    public string? FileAlgorithmId { get; init; }

    public required IReadOnlyList<ChecksumLineEntry> Entries { get; init; }

    public required IReadOnlyList<ChecksumLineError> Errors { get; init; }

    /// <summary>アルゴリズムを判定できない行があるか (仕様 1: 利用者にアルゴリズムを選ばせる)。</summary>
    public bool NeedsAlgorithm => Entries.Any(e => e.AlgorithmId is null);
}

/// <summary>
/// チェックサムファイルの判定と読み込み (TOOL-19 の仕様 1・2・7)。GNU (<c>sha256sum</c> の <c>*</c> 付きの行、coreutils のエスケープを含む)、
/// BSD、SFV を、拡張子・ファイル名・値の長さ・行の書式から自動で判定する。
/// </summary>
public static partial class ChecksumReader
{
    /// <summary>サイドカーの拡張子 (検索の順)。</summary>
    private static readonly string[] SidecarExtensions = [".sha256", ".md5", ".sha1", ".sha512", ".sha384", ".sfv"];

    [GeneratedRegex(@"^(?<tag>[A-Za-z0-9/_\-]+)\s*\((?<name>.*)\)\s*=\s*(?<value>[0-9A-Fa-f]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BsdLine();

    [GeneratedRegex(@"^(?<esc>\\)?(?<value>[0-9A-Fa-f]{8,})(?: (?<mode>[ *])|[ \t]+(?<mode>\*)?)(?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex GnuLine();

    [GeneratedRegex(@"^(?<name>.+?)\s+(?<value>[0-9A-Fa-f]{8})$", RegexOptions.CultureInvariant)]
    private static partial Regex SfvLine();

    [GeneratedRegex(@"^(?<alg>md5|sha1|sha224|sha256|sha384|sha512)sums?(\.txt)?$|^checksums?\.(?<alg2>md5|sha1|sha224|sha256|sha384|sha512)$|^(?<alg3>md5|sha1|sha224|sha256|sha384|sha512)sum(\.txt)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NamedFile();

    /// <summary>アプリで開いたときに InfoBar「チェックサムファイルです」を出すファイルか (仕様 7)。</summary>
    public static bool IsChecksumFile(string path)
    {
        string name = Path.GetFileName(path);
        return ChecksumFile.AlgorithmForExtension(Path.GetExtension(name)) is not null || NamedFile().IsMatch(name);
    }

    /// <summary>拡張子またはファイル名 (<c>SHA256SUMS</c>、<c>CHECKSUM.SHA256</c> など) から決まるアルゴリズム。分からなければ null。</summary>
    public static string? AlgorithmFromName(string path)
    {
        string name = Path.GetFileName(path);
        if (ChecksumFile.AlgorithmForExtension(Path.GetExtension(name)) is { } byExtension)
        {
            return byExtension;
        }

        Match m = NamedFile().Match(name);
        if (!m.Success)
        {
            return null;
        }

        string id = (m.Groups["alg"].Success ? m.Groups["alg"] : m.Groups["alg2"].Success ? m.Groups["alg2"] : m.Groups["alg3"]).Value.ToLowerInvariant();
        return HashCatalog.Find(id)?.Id;
    }

    /// <summary>
    /// 単体のファイルに対応するサイドカー (<c>ファイル名.sha256</c> など) を探す (仕様 2)。なければ null。
    /// </summary>
    public static string? FindSidecar(string filePath)
    {
        foreach (string extension in SidecarExtensions)
        {
            string candidate = filePath + extension;
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>ファイルを読む。UTF-8 (BOM の有無を問わない) で読めなければ、OS の ANSI コードページで読む。</summary>
    public static ChecksumDocument ReadFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }

        return Parse(text.TrimStart('﻿'), path);
    }

    /// <summary>内容を読む。<paramref name="path"/> はファイル名・拡張子の判定に使う。</summary>
    public static ChecksumDocument Parse(string content, string path)
    {
        string? fileAlgorithm = AlgorithmFromName(path);
        bool sfvName = string.Equals(Path.GetExtension(path), ".sfv", StringComparison.OrdinalIgnoreCase);
        string[] lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var entries = new List<ChecksumLineEntry>();
        var errors = new List<ChecksumLineError>();
        ChecksumFormat? format = sfvName ? ChecksumFormat.Sfv : null;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            string trimmedStart = line.TrimStart();
            if (trimmedStart.Length == 0 || trimmedStart.StartsWith(';') || (trimmedStart.StartsWith('#') && format != ChecksumFormat.Sfv))
            {
                continue;
            }

            int number = i + 1;
            if (sfvName || format == ChecksumFormat.Sfv)
            {
                if (SfvLine().Match(trimmedStart) is { Success: true } sfv)
                {
                    entries.Add(new ChecksumLineEntry(number, sfv.Groups["name"].Value, Convert.FromHexString(sfv.Groups["value"].Value), "crc32", false));
                }
                else
                {
                    errors.Add(new ChecksumLineError(number, line));
                }

                continue;
            }

            if (BsdLine().Match(trimmedStart) is { Success: true } bsd && bsd.Groups["value"].Length % 2 == 0)
            {
                format ??= ChecksumFormat.Bsd;
                string? tagAlgorithm = HashCatalog.Find(bsd.Groups["tag"].Value)?.Id;
                byte[] value = Convert.FromHexString(bsd.Groups["value"].Value);
                entries.Add(new ChecksumLineEntry(number, bsd.Groups["name"].Value, value, tagAlgorithm ?? fileAlgorithm ?? ChecksumFile.AlgorithmForLength(value.Length), false));
                continue;
            }

            if (GnuLine().Match(line) is { Success: true } gnu && gnu.Groups["value"].Length % 2 == 0)
            {
                format ??= ChecksumFormat.Gnu;
                byte[] value = Convert.FromHexString(gnu.Groups["value"].Value);
                string name = gnu.Groups["name"].Value;
                if (gnu.Groups["esc"].Success)
                {
                    // coreutils は、名前に \ や改行を含む行の先頭に \ を付け、\\ と \n に直す。
                    name = name.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
                }

                entries.Add(new ChecksumLineEntry(number, name, value, fileAlgorithm ?? ChecksumFile.AlgorithmForLength(value.Length),
                    gnu.Groups["mode"].Value == "*"));
                continue;
            }

            if (format is null && SfvLine().Match(trimmedStart) is { Success: true } looksSfv && entries.Count == 0 && fileAlgorithm is null)
            {
                // 拡張子が .sfv でなくても、SFV の書式だけの内容は SFV として読む。
                format = ChecksumFormat.Sfv;
                entries.Add(new ChecksumLineEntry(number, looksSfv.Groups["name"].Value, Convert.FromHexString(looksSfv.Groups["value"].Value), "crc32", false));
                continue;
            }

            errors.Add(new ChecksumLineError(number, line));
        }

        return new ChecksumDocument
        {
            Format = format ?? ChecksumFormat.Gnu,
            FileAlgorithmId = fileAlgorithm,
            Entries = entries,
            Errors = errors,
        };
    }
}
