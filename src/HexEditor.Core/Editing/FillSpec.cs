using HexEditor.Core.Sources;

namespace HexEditor.Core.Editing;

/// <summary>塗りつぶし・挿入の内容の種類 (EDIT-29 の仕様 2)。</summary>
public enum FillKind
{
    /// <summary>1 バイトの値。</summary>
    Byte,

    /// <summary>Hex のバイト列 (00-overview 6.3)。1 B〜1 MiB。</summary>
    HexPattern,

    /// <summary>文字列を文字コードで変換したもの。1 B〜1 MiB (変換後)。</summary>
    Text,

    /// <summary>擬似乱数 (値の範囲とシード)。</summary>
    Random,

    /// <summary>暗号論的乱数 (<c>RandomNumberGenerator</c>)。</summary>
    CryptoRandom,

    /// <summary>カウンタ (開始値・増分・要素の大きさ・エンディアン・符号・あふれの扱い)。</summary>
    Counter,

    /// <summary>ファイルの内容 (パス・開始位置・長さ)。</summary>
    File,

    /// <summary>クリップボードの内容 (EDIT-23 の形式の優先順位で読んだもの。呼び出し側が渡す)。</summary>
    Clipboard,
}

/// <summary>パターンの起点 (EDIT-29 の仕様 4)。</summary>
public enum PatternOrigin
{
    /// <summary>範囲の先頭から (既定)。</summary>
    RangeStart,

    /// <summary>オフセット 0 から数える (位置 p のバイトはパターンの p mod パターン長 番目)。</summary>
    OffsetZero,
}

/// <summary>カウンタがあふれたときの扱い (EDIT-29 の仕様 2)。</summary>
public enum CounterOverflow
{
    Wrap,
    Saturate,
}

/// <summary>
/// 塗りつぶし (EDIT-29)・バイトの挿入 (EDIT-14)・ファイルサイズの変更 (EDIT-15) の内容。ダイアログの設定をそのまま表す。
/// テキストは呼び出し側が文字コードで変換して <see cref="Pattern"/> に入れる (<see cref="FillText"/>)。
/// </summary>
public sealed record FillSpec
{
    public FillKind Kind { get; init; } = FillKind.Byte;

    /// <summary>バイト値 (<see cref="FillKind.Byte"/>)。</summary>
    public byte Value { get; init; }

    /// <summary>パターン (<see cref="FillKind.HexPattern"/>・<see cref="FillKind.Text"/>・バイト列の <see cref="FillKind.Clipboard"/>)。</summary>
    public byte[]? Pattern { get; init; }

    /// <summary>乱数の最小値と最大値 (0〜255)。</summary>
    public byte RandomMin { get; init; }

    public byte RandomMax { get; init; } = 255;

    /// <summary>乱数のシード。null なら毎回変わる。</summary>
    public ulong? Seed { get; init; }

    public long CounterStart { get; init; }

    public long CounterStep { get; init; } = 1;

    /// <summary>カウンタの要素の大きさ (1 / 2 / 4 / 8 バイト)。</summary>
    public int CounterSize { get; init; } = 1;

    public bool CounterBigEndian { get; init; }

    public bool CounterSigned { get; init; }

    public CounterOverflow CounterOverflow { get; init; } = CounterOverflow.Wrap;

    /// <summary>ファイルのパス (<see cref="FillKind.File"/>)。</summary>
    public string? FilePath { get; init; }

    public long FileOffset { get; init; }

    /// <summary>ファイル内の長さ。null ならファイルの末尾まで。</summary>
    public long? FileLength { get; init; }

    /// <summary>クリップボードの内容がアプリ内クリップボードの参照のときのデータソース (<see cref="FillKind.Clipboard"/>)。</summary>
    public IByteSource? ClipboardSource { get; init; }

    /// <summary>内容が対象範囲より短いときに繰り返すか (EDIT-29 の仕様 3)。</summary>
    public bool Repeat { get; init; } = true;

    public PatternOrigin Origin { get; init; } = PatternOrigin.RangeStart;

    public static FillSpec Zero { get; } = new();

    /// <summary>パターンの上限 (Hex パターン・テキスト。EDIT-29 の仕様 2)。</summary>
    public const int MaxPatternLength = 1024 * 1024;

    /// <summary>入力の誤り。正しければ null。</summary>
    public FillSpecError? Validate()
    {
        switch (Kind)
        {
            case FillKind.HexPattern or FillKind.Text:
                if (Pattern is not { Length: > 0 })
                {
                    return FillSpecError.EmptyPattern;
                }

                return Pattern.Length > MaxPatternLength ? FillSpecError.PatternTooLong : null;
            case FillKind.Clipboard:
                return Pattern is { Length: > 0 } || ClipboardSource is { Length: > 0 } ? null : FillSpecError.EmptyClipboard;
            case FillKind.Random:
                return RandomMin > RandomMax ? FillSpecError.InvalidRange : null;
            case FillKind.Counter:
                return CounterSize is 1 or 2 or 4 or 8 ? null : FillSpecError.InvalidCounterSize;
            case FillKind.File:
                return string.IsNullOrEmpty(FilePath) ? FillSpecError.NoFile : FileOffset < 0 || FileLength < 0 ? FillSpecError.InvalidRange : null;
            default:
                return null;
        }
    }
}

/// <summary>内容の指定の誤り (入力欄のエラーとして示す)。</summary>
public enum FillSpecError
{
    EmptyPattern,
    PatternTooLong,
    EmptyClipboard,
    InvalidRange,
    InvalidCounterSize,
    NoFile,
}

/// <summary>塗りつぶしのテキストの入力 (EDIT-29 の仕様 2 のテキスト)。</summary>
public static class FillText
{
    /// <summary>
    /// `\r` `\n` `\t` `\\` `\0` のエスケープを文字にする。それ以外の `\` はそのまま残す。
    /// </summary>
    public static string Unescape(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                string? replaced = next switch
                {
                    'r' => "\r",
                    'n' => "\n",
                    't' => "\t",
                    '\\' => "\\",
                    '0' => "\0",
                    _ => null,
                };
                if (replaced is not null)
                {
                    sb.Append(replaced);
                    i++;
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
