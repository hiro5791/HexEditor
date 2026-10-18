using System.Globalization;
using System.Text;
using HexEditor.Core.Disassembly;

namespace HexEditor.Core.Templates.Visualization;

/// <summary>ビジュアライザの種類 (TPL-16 の仕様 1 の表)。</summary>
public enum TemplateVisualKind
{
    Bitmap,
    Image,
    Audio,
    Disassembly,
    Table,
    Chart,
    Color,
    Text,

    /// <summary>表示できない (未知の種類・引数の誤り・デコードの失敗)。<see cref="TemplateVisual.Error"/> に理由。</summary>
    Error,
}

/// <summary>
/// ビジュアライザの内容 (TPL-16)。<see cref="Truncated"/> はデータが上限 (64 MB、画像は 16,384 × 16,384 画素) を超えたため
/// 先頭の上限分だけにした印 (仕様 3)。<see cref="Description"/> は種類と大きさの言語によらない表示 (例: <c>4 × 4, bgra8</c>)。
/// 画面の文言 (代替テキストなど) は App が言語のリソースで組み立てる (仕様 6)。
/// </summary>
public abstract class TemplateVisual
{
    public abstract TemplateVisualKind Kind { get; }

    /// <summary>表示したノードのパス。</summary>
    public string NodePath { get; init; } = string.Empty;

    /// <summary>ノードのデータの長さ (上限で切る前)。</summary>
    public long SourceLength { get; init; }

    public bool Truncated { get; init; }

    /// <summary>表示できない理由 (<see cref="TemplateVisualKind.Error"/> のとき)。</summary>
    public string? Error { get; init; }

    public abstract string Description { get; }

    internal static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>表示できない (理由は <see cref="TemplateVisual.Error"/>)。</summary>
public sealed class ErrorVisual : TemplateVisual
{
    public override TemplateVisualKind Kind => TemplateVisualKind.Error;

    /// <summary>要求された種類 (<c>visualize</c> の最初の引数)。</summary>
    public string RequestedKind { get; init; } = string.Empty;

    public override string Description => RequestedKind;
}

/// <summary>画像 (生データの <c>bitmap</c>)。<see cref="Pixels"/> は 0xAARRGGBB、上の行から。</summary>
public sealed class BitmapVisual : TemplateVisual
{
    public override TemplateVisualKind Kind => TemplateVisualKind.Bitmap;

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>形式の名前 (<c>bgra8</c> など)。</summary>
    public required string Format { get; init; }

    public required uint[] Pixels { get; init; }

    public override string Description => Invariant($"{Width} × {Height}, {Format}");
}

/// <summary>
/// 画像のファイル形式 (<c>image</c>)。デコードは App が Windows の画像コーデックで行う (仕様 4: 失敗しても落ちない)。
/// <see cref="FormatName"/> は先頭のバイトから判定した形式 (分からなければ null)。
/// </summary>
public sealed class EncodedImageVisual : TemplateVisual
{
    public override TemplateVisualKind Kind => TemplateVisualKind.Image;

    public required byte[] Data { get; init; }

    public string? FormatName { get; init; }

    public override string Description => FormatName ?? "image";

    /// <summary>先頭のバイトから形式を判定する (PNG、JPEG、GIF、BMP、ICO、TIFF、WebP)。</summary>
    public static string? Sniff(ReadOnlySpan<byte> d)
    {
        if (d.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]))
        {
            return "PNG";
        }

        if (d.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "JPEG";
        }

        if (d.StartsWith("GIF8"u8))
        {
            return "GIF";
        }

        if (d.StartsWith("BM"u8))
        {
            return "BMP";
        }

        if (d.StartsWith((ReadOnlySpan<byte>)[0, 0, 1, 0]) || d.StartsWith((ReadOnlySpan<byte>)[0, 0, 2, 0]))
        {
            return "ICO";
        }

        if (d.StartsWith("II*\0"u8) || d.StartsWith("MM\0*"u8))
        {
            return "TIFF";
        }

        if (d.Length >= 12 && d.StartsWith("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8))
        {
            return "WebP";
        }

        return null;
    }
}

/// <summary>
/// 音声 (<c>audio</c>)。<see cref="Samples"/> はチャンネルを交互に並べた −1〜1 の値。保存は WAV (仕様 5。元の形式のまま)。
/// </summary>
public sealed class AudioVisual : TemplateVisual
{
    public override TemplateVisualKind Kind => TemplateVisualKind.Audio;

    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public required PcmFormat Format { get; init; }

    /// <summary>元のデータ (上限で切ったもの。サンプルの境界に揃える)。</summary>
    public required byte[] Data { get; init; }

    public required float[] Samples { get; init; }

    /// <summary>フレーム (全チャンネルの 1 サンプル) の数。</summary>
    public long FrameCount => Channels == 0 ? 0 : Samples.LongLength / Channels;

    public TimeSpan Duration => SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)FrameCount / SampleRate);

    public override string Description => Invariant($"{Duration.TotalSeconds:0.###} s, {SampleRate} Hz, {Channels} ch, {PcmAudio.Name(Format)}");

    /// <summary>WAV ファイルの内容 (元の形式。<c>pcm_s16be</c> は WAV に合わせてリトルエンディアンにする)。</summary>
    public byte[] ToWav() => PcmAudio.Wav(Format, SampleRate, Channels, Data);

    /// <summary>波形の表示用に、<paramref name="buckets"/> 個の区間ごとの最小・最大 (全チャンネル)。</summary>
    public (float Min, float Max)[] Waveform(int buckets) => PcmAudio.Waveform(Samples, Channels, buckets);
}

/// <summary>逆アセンブル (<c>disasm</c>。06 の ANA-23 のエンジン)。</summary>
public sealed class DisassemblyVisual : TemplateVisual
{
    public override TemplateVisualKind Kind => TemplateVisualKind.Disassembly;

    public required string Architecture { get; init; }

    public required ulong BaseAddress { get; init; }

    public required IReadOnlyList<DisasmInstruction> Instructions { get; init; }

    /// <summary>命令の行の表示 (アドレス、バイト、命令)。</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    public override string Description => Invariant($"{Architecture}, {Instructions.Count}");
}

/// <summary>
/// 表 (<c>table</c>)。行は配列の要素、列は要素のメンバー (要素が構造体でなければ「値」の 1 列)。行は必要になったときに作る
/// (大きな配列でもすべてを先に作らない)。
/// </summary>
public sealed class TableVisual : TemplateVisual
{
    private readonly Func<long, IReadOnlyList<string>> _row;

    internal TableVisual(IReadOnlyList<string> columns, long rowCount, Func<long, IReadOnlyList<string>> row)
    {
        Columns = columns;
        RowCount = rowCount;
        _row = row;
    }

    public override TemplateVisualKind Kind => TemplateVisualKind.Table;

    public IReadOnlyList<string> Columns { get; }

    public long RowCount { get; }

    public IReadOnlyList<string> Row(long index) => _row(index);

    public override string Description => Invariant($"{RowCount} × {Columns.Count}");

    /// <summary>CSV (RFC 4180。見出しの行の後にデータの行。改行は CRLF) で書く (仕様 5)。</summary>
    public void WriteCsv(TextWriter writer, CancellationToken cancellationToken = default)
    {
        writer.Write(string.Join(",", Columns.Select(Csv)));
        writer.Write("\r\n");
        for (long i = 0; i < RowCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(string.Join(",", Row(i).Select(Csv)));
            writer.Write("\r\n");
        }
    }

    public string ToCsv()
    {
        var sb = new StringWriter(CultureInfo.InvariantCulture);
        WriteCsv(sb);
        return sb.ToString();
    }

    internal static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}

/// <summary>グラフの種類 (<c>chart</c> の 2 番目の引数)。</summary>
public enum TemplateChartKind
{
    Line,
    Bar,
}

/// <summary>グラフ (<c>chart</c>)。数値の配列の要素の値。</summary>
public sealed class ChartVisual : TemplateVisual
{
    public override TemplateVisualKind Kind => TemplateVisualKind.Chart;

    public required TemplateChartKind Chart { get; init; }

    public required double[] Values { get; init; }

    public double Minimum => Values.Length == 0 ? 0 : Values.Where(double.IsFinite).DefaultIfEmpty(0).Min();

    public double Maximum => Values.Length == 0 ? 0 : Values.Where(double.IsFinite).DefaultIfEmpty(0).Max();

    public override string Description => Invariant($"{Chart.ToString().ToLowerInvariant()}, {Values.Length}");
}

/// <summary>色 (<c>color</c>)。</summary>
public sealed class ColorVisual : TemplateVisual
{
    public override TemplateVisualKind Kind => TemplateVisualKind.Color;

    /// <summary>0xAARRGGBB。</summary>
    public required uint Argb { get; init; }

    /// <summary>値の表示 (結果ツリーと同じ)。</summary>
    public required string ValueText { get; init; }

    public string Hex => Invariant($"#{(Argb >> 16) & 0xFF:X2}{(Argb >> 8) & 0xFF:X2}{Argb & 0xFF:X2}") + ((Argb >> 24) == 0xFF ? "" : Invariant($"{Argb >> 24:X2}"));

    public override string Description => Hex;
}

/// <summary>テキスト (<c>text</c>)。</summary>
public sealed class TextVisual : TemplateVisual
{
    public override TemplateVisualKind Kind => TemplateVisualKind.Text;

    public required string Text { get; init; }

    public required string Encoding { get; init; }

    public override string Description => Invariant($"{Encoding}, {Text.Length}");

    internal static string Decode(ReadOnlySpan<byte> data, string encoding)
    {
        Encoding enc = Resolve(encoding);
        return enc.GetString(data);
    }

    internal static Encoding Resolve(string name)
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (View.EncodingCatalog.Find(name) is { CodePage: > 0 } entry)
        {
            return System.Text.Encoding.GetEncoding(entry.CodePage, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("�"));
        }

        string normalized = name.Trim().ToLowerInvariant() switch
        {
            "utf8" => "utf-8",
            "utf16" or "utf16le" or "utf-16" => "utf-16",
            "utf16be" => "utf-16BE",
            "latin1" => "iso-8859-1",
            "ascii" => "us-ascii",
            "sjis" or "cp932" => "shift_jis",
            var n => n,
        };
        return System.Text.Encoding.GetEncoding(normalized, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("�"));
    }
}
