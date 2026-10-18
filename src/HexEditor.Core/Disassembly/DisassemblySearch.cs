using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HexEditor.Core.Engine;

namespace HexEditor.Core.Disassembly;

/// <summary>相互参照の種類 (ANA-25 の仕様 7)。</summary>
public enum ReferenceKind
{
    Call,
    Jump,
    Data,
}

/// <summary>相互参照の結果 1 件 (参照元)。</summary>
public sealed record CrossReference(long Offset, int Length, ulong Address, string Text, ReferenceKind Kind);

/// <summary>命令パターン検索の結果 1 件 (一致した命令列。ANA-28 の仕様 6)。</summary>
public sealed record PatternMatch(long Offset, long Length, ulong Address, string Text);

/// <summary>命令パターンの構文エラー (ANA-28 の「エラー」)。<see cref="Position"/> は 0 から数えた文字の位置。</summary>
public sealed class InstructionPatternException(int position, string reason) : Exception(reason)
{
    public int Position { get; } = position;

    /// <summary>理由の種類 (リソース <c>Disasm_PatternError_&lt;Reason&gt;</c>): Empty、Range、Brace、Regex。</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// 命令パターン (ANA-28)。1 行に 1 命令、または <c>;</c> 区切り。<c>*</c> は任意の文字列、<c>?</c> は任意の 1 文字、命令の位置の <c>{n}</c>・
/// <c>{n-m}</c> は任意の命令 n〜m 個。比較は表示した文字列に対して、大文字・小文字を区別せず (序数比較)、空白の違いを無視する
/// (区切りの記号の前後の空白は取り除き、続く空白は 1 つにする)。「正規表現」では 1 命令ごとの .NET の正規表現。
/// </summary>
public sealed class InstructionPattern
{
    private abstract record Item;

    private sealed record Instruction(Regex Regex) : Item;

    private sealed record Gap(int Min, int Max) : Item;

    private readonly List<Item> _items;

    private InstructionPattern(List<Item> items)
    {
        _items = items;
        MaxSpan = items.Sum(i => i is Gap g ? g.Max : 1);
    }

    /// <summary>1 件の一致の命令数の最大。</summary>
    public int MaxSpan { get; }

    /// <summary>{n} の上限。</summary>
    public const int MaxGap = 64;

    public static InstructionPattern Parse(string text, bool regex)
    {
        var items = new List<Item>();
        int start = 0;
        foreach (string part in Split(text))
        {
            string trimmed = part.Trim();
            int position = start + part.Length - part.TrimStart().Length;
            start += part.Length + 1;
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('{'))
            {
                items.Add(ParseGap(trimmed, position));
                continue;
            }

            if (!regex && (trimmed.Contains('{') || trimmed.Contains('}')))
            {
                throw new InstructionPatternException(position + Math.Max(trimmed.IndexOf('{'), trimmed.IndexOf('}')), "Brace");
            }

            try
            {
                string body = regex ? trimmed : "^" + Wildcard(Normalize(trimmed)) + "$";
                items.Add(new Instruction(new Regex(body, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))));
            }
            catch (ArgumentException)
            {
                throw new InstructionPatternException(position, "Regex");
            }
        }

        if (!items.Any(i => i is Instruction))
        {
            throw new InstructionPatternException(0, "Empty");
        }

        return new InstructionPattern(items);
    }

    private static IEnumerable<string> Split(string text)
    {
        // 改行も区切りにする (1 行に 1 命令)。
        return text.Replace("\r\n", ";", StringComparison.Ordinal).Replace('\n', ';').Split(';');
    }

    private static Gap ParseGap(string text, int position)
    {
        if (!text.EndsWith('}'))
        {
            throw new InstructionPatternException(position + text.Length, "Brace");
        }

        string body = text[1..^1].Trim();
        string[] range = body.Split('-');
        if (range.Length > 2 || range.Any(r => !int.TryParse(r.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new InstructionPatternException(position, "Range");
        }

        int min = int.Parse(range[0].Trim(), CultureInfo.InvariantCulture);
        int max = range.Length == 2 ? int.Parse(range[1].Trim(), CultureInfo.InvariantCulture) : min;
        if (min > max || max > MaxGap)
        {
            throw new InstructionPatternException(position, "Range");
        }

        return new Gap(min, max);
    }

    private static string Wildcard(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }

        return sb.ToString();
    }

    /// <summary>比較のための正規化 (区切りの記号の前後の空白を除き、続く空白を 1 つにして、小文字にする)。</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ' && !IsPunctuation(sb[^1]))
                {
                    sb.Append(' ');
                }

                continue;
            }

            if (IsPunctuation(c) && sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsPunctuation(char c) => c is ',' or '[' or ']' or '+' or '-' or ':' or '(' or ')' or '{' or '}' or '#' or '!';

    private bool IsMatch(Instruction item, DisasmInstruction instruction, bool regex) =>
        item.Regex.IsMatch(regex ? instruction.Text : Normalize(instruction.Text));

    private int Match(IReadOnlyList<DisasmInstruction> window, int at, int item, bool regex)
    {
        if (item == _items.Count)
        {
            return at;
        }

        switch (_items[item])
        {
            case Instruction i:
                return at < window.Count && IsMatch(i, window[at], regex) ? Match(window, at + 1, item + 1, regex) : -1;
            case Gap g:
                for (int n = g.Min; n <= g.Max; n++)
                {
                    if (at + n > window.Count)
                    {
                        break;
                    }

                    int r = Match(window, at + n, item + 1, regex);
                    if (r >= 0)
                    {
                        return r;
                    }
                }

                return -1;
            default:
                return -1;
        }
    }

    /// <summary>先頭から一致した命令の数 (一致しなければ 0)。</summary>
    public int MatchLength(IReadOnlyList<DisasmInstruction> window, int start, bool regex)
    {
        int end = Match(window, start, 0, regex);
        return end < 0 ? 0 : end - start;
    }
}

/// <summary>
/// 逆アセンブルの探索 (相互参照 ANA-25 の仕様 7、命令パターン検索 ANA-28)。対象は実行可能なセクション全体 (実行形式の場合) か、指定した範囲。
/// 線形スイープで解釈し、見つかった分を順に返す (キャンセルしたら見つかった分が残る)。
/// </summary>
public static class DisassemblySearch
{
    /// <summary>既定の対象の範囲: 実行形式なら実行可能なセクション、そうでなければ null (対象範囲を指定させる)。</summary>
    public static IReadOnlyList<(long Offset, long Length)>? DefaultRanges(DisassemblyListing listing) =>
        listing.Image.IsExecutable ? [.. listing.Image.ExecutableSections.Select(s => (s.FileOffset, s.FileSize))] : null;

    /// <summary>
    /// <paramref name="target"/> (表示上のアドレス) を分岐先・参照先に持つ命令を探す。
    /// </summary>
    public static IEnumerable<CrossReference> FindReferences(DisassemblyListing listing, DocumentSnapshot snapshot, ulong target,
        IReadOnlyList<(long Offset, long Length)> ranges, CancellationToken token, Action<long>? progress = null)
    {
        using IInstructionDecoder decoder = Disassembler.Create(listing.Architecture, listing.Format, listing.Options.BigEndian
            ?? (listing.Architecture.HasEndian && listing.Image.IsExecutable ? listing.Image.BigEndian : null));
        long done = 0;
        foreach ((long offset, long length) in ranges)
        {
            long end = Math.Min(offset + length, snapshot.Length);
            long base_ = done;
            foreach ((long at, DisasmInstruction i) in listing.SweepRange(snapshot, decoder, offset, end, token, p => progress?.Invoke(base_ + p)))
            {
                if (i.Target == target)
                {
                    ReferenceKind kind = i.BranchTarget == target
                        ? i.Flow == FlowKind.Call ? ReferenceKind.Call : ReferenceKind.Jump
                        : ReferenceKind.Data;
                    yield return new CrossReference(at, i.Length, i.Address, i.Text, kind);
                }
            }

            done += end - offset;
        }
    }

    /// <summary>命令パターンを探す (ANA-28 の仕様 5)。</summary>
    public static IEnumerable<PatternMatch> FindPattern(DisassemblyListing listing, DocumentSnapshot snapshot, InstructionPattern pattern, bool regex,
        IReadOnlyList<(long Offset, long Length)> ranges, CancellationToken token, Action<long>? progress = null)
    {
        using IInstructionDecoder decoder = Disassembler.Create(listing.Architecture, listing.Format, listing.Options.BigEndian
            ?? (listing.Architecture.HasEndian && listing.Image.IsExecutable ? listing.Image.BigEndian : null));
        int span = Math.Max(1, pattern.MaxSpan);
        long done = 0;
        foreach ((long offset, long length) in ranges)
        {
            long end = Math.Min(offset + length, snapshot.Length);
            var window = new List<DisasmInstruction>(span * 2 + 8);
            var offsets = new List<long>(span * 2 + 8);
            long base_ = done;
            foreach ((long at, DisasmInstruction i) in listing.SweepRange(snapshot, decoder, offset, end, token, p => progress?.Invoke(base_ + p)))
            {
                window.Add(i);
                offsets.Add(at);
                if (window.Count >= span)
                {
                    if (Try(window, offsets, pattern, regex) is { } m)
                    {
                        yield return m;
                    }

                    window.RemoveAt(0);
                    offsets.RemoveAt(0);
                }
            }

            while (window.Count > 0)
            {
                if (Try(window, offsets, pattern, regex) is { } m)
                {
                    yield return m;
                }

                window.RemoveAt(0);
                offsets.RemoveAt(0);
            }

            done += end - offset;
        }
    }

    private static PatternMatch? Try(List<DisasmInstruction> window, List<long> offsets, InstructionPattern pattern, bool regex)
    {
        int n = pattern.MatchLength(window, 0, regex);
        if (n == 0)
        {
            return null;
        }

        long length = offsets[n - 1] + window[n - 1].Length - offsets[0];
        return new PatternMatch(offsets[0], length, window[0].Address, string.Join("; ", window.Take(n).Select(i => i.Text)));
    }

    /// <summary>所要時間の目安 (ANA-28 の「巨大ファイル」: 対象が 100 MB を超える場合に表示する。x64 で 50 MB/s 程度)。</summary>
    public static TimeSpan EstimatedTime(long bytes) => TimeSpan.FromSeconds(bytes / (50.0 * 1024 * 1024));

    /// <summary>所要時間の目安を表示する対象の大きさ。</summary>
    public const long EstimateThreshold = 100L * 1024 * 1024;
}
