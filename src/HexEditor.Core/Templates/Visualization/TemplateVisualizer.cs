using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using HexEditor.Core.Disassembly;

namespace HexEditor.Core.Templates.Visualization;

/// <summary>
/// テンプレートのビジュアライザ (TPL-16)。<c>visualize("種類", 引数…)</c> の属性を持つノードのデータを、画像・音声・逆アセンブル・表・
/// グラフ・色・テキストにする。データの読み込みとデコードは呼び出し側のバックグラウンドのスレッドで行う (UI スレッドから呼ばない)。
/// 引数の誤り・デコードの失敗は例外にせず <see cref="ErrorVisual"/> を返す (仕様 4 と「エラー」)。
/// </summary>
public static class TemplateVisualizer
{
    /// <summary>表示するデータの上限 (仕様 3)。</summary>
    public const long MaxDataBytes = 64L * 1024 * 1024;

    /// <summary>画像の幅・高さの上限 (仕様 3)。</summary>
    public const int MaxImageSide = 16_384;

    /// <summary>逆アセンブルで表示する命令の数の上限 (64 MB を全部解釈すると命令の一覧が大きくなりすぎるため)。</summary>
    public const int MaxInstructions = 100_000;

    /// <summary>種類の一覧 (<c>visualize</c> の最初の引数)。</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["bitmap", "image", "audio", "disasm", "table", "chart", "color", "text"];

    /// <summary>ビジュアライザを持つノードか (<c>visualize</c> の属性がある)。</summary>
    public static bool HasVisualizer(TemplateNode node) => !string.IsNullOrEmpty(node.Attributes.VisualizeKind);

    /// <summary>
    /// ノードのビジュアライザの内容を作る。<paramref name="display"/> は表・テキストの値の表示に使う地域設定 (既定は現在の設定)。
    /// </summary>
    public static TemplateVisual Create(TemplateNode node, TemplateDisplayContext? display = null, CancellationToken cancellationToken = default)
    {
        string kind = (node.Attributes.VisualizeKind ?? string.Empty).Trim().ToLowerInvariant();
        IReadOnlyList<string> args = node.Attributes.VisualizeArguments;
        try
        {
            return kind switch
            {
                "bitmap" => Bitmap(node, args),
                "image" => Image(node),
                "audio" => Audio(node, args),
                "disasm" => Disasm(node, args),
                "table" => Table(node, display ?? TemplateDisplayContext.Current),
                "chart" => Chart(node, args, cancellationToken),
                "color" => Color(node, display ?? TemplateDisplayContext.Current),
                "text" => Text(node, args),
                _ => Fail(node, kind, Msg($"未知のビジュアライザの種類です: {kind}", $"Unknown visualizer kind: {kind}")),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 引数の誤り・データの読み込みの失敗・デコードの失敗 (仕様 4: 落ちずに理由を表示する)。
            return Fail(node, kind, ex.Message);
        }
    }

    private static string Msg(string ja, string en) =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? ja : en;

    private static ErrorVisual Fail(TemplateNode node, string kind, string reason) =>
        new() { NodePath = node.Path, RequestedKind = kind, Error = reason, SourceLength = node.Size };

    /// <summary>ノードのデータ (上限まで)。ドキュメントかセクションから読む。</summary>
    internal static byte[] ReadNode(TemplateNode node, long max, out bool truncated)
    {
        long size = Math.Max(0, node.Size);
        truncated = size > max;
        int length = (int)Math.Min(size, max);
        byte[] data = new byte[length];
        int read = node.Section is { } section ? section.Read(node.Offset, data) : node.Result.Data.Read(node.Offset, data, out _);
        if (read < length)
        {
            Array.Resize(ref data, Math.Max(0, read));
        }

        return data;
    }

    private static int IntArg(IReadOnlyList<string> args, int index, string name)
    {
        if (index >= args.Count || !TryNumber(args[index], out long value) || value is < int.MinValue or > int.MaxValue)
        {
            throw new ArgumentException(Msg($"引数 {name} がありません、または数値ではありません。", $"The argument {name} is missing or not a number."));
        }

        return (int)value;
    }

    internal static bool TryNumber(string text, out long value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d == Math.Floor(d) && Math.Abs(d) < 9e18)
        {
            value = (long)d;
            return true;
        }

        return false;
    }

    // ───────────── 画像 (生データ) ─────────────

    private static readonly string[] BitmapFormats = ["gray8", "rgb8", "rgba8", "bgra8", "rgb565", "indexed8"];

    private static TemplateVisual Bitmap(TemplateNode node, IReadOnlyList<string> args)
    {
        int width = IntArg(args, 0, "width");
        int height = IntArg(args, 1, "height");
        string format = args.Count > 2 ? args[2].Trim().ToLowerInvariant() : "rgba8";
        if (Array.IndexOf(BitmapFormats, format) < 0)
        {
            throw new ArgumentException(Msg($"画素の形式が違います: {format} ({string.Join("、", BitmapFormats)})", $"Unknown pixel format: {format} ({string.Join(", ", BitmapFormats)})"));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException(Msg("幅と高さは 1 以上にしてください。", "Width and height must be 1 or more."));
        }

        int bpp = format switch { "gray8" or "indexed8" => 1, "rgb565" => 2, "rgb8" => 3, _ => 4 };
        long stride = (long)width * bpp;

        // 上限 (仕様 3): 幅・高さは 16,384 画素、データは 64 MB。超える分は先頭 (左上) から上限までにする。
        int shownWidth = Math.Min(width, MaxImageSide);
        long maxRows = Math.Max(1, MaxDataBytes / stride);
        int shownHeight = (int)Math.Min(Math.Min(height, MaxImageSide), maxRows);
        bool truncated = shownWidth < width || shownHeight < height;
        byte[] data = ReadNode(node, Math.Min(node.Size, stride * shownHeight), out _);

        uint[]? palette = format == "indexed8" ? Palette(node, args) : null;
        bool opaqueBgra = format == "bgra8" && AllAlphaZero(data, 4, 3);
        bool opaqueRgba = format == "rgba8" && AllAlphaZero(data, 4, 3);
        uint[] pixels = new uint[shownWidth * shownHeight];
        for (int y = 0; y < shownHeight; y++)
        {
            for (int x = 0; x < shownWidth; x++)
            {
                long o = y * stride + (long)x * bpp;
                if (o + bpp > data.Length)
                {
                    continue; // データが足りない画素は透明のまま。
                }

                ReadOnlySpan<byte> p = data.AsSpan((int)o, bpp);
                pixels[y * shownWidth + x] = format switch
                {
                    "gray8" => 0xFF000000u | ((uint)p[0] << 16) | ((uint)p[0] << 8) | p[0],
                    "rgb8" => 0xFF000000u | ((uint)p[0] << 16) | ((uint)p[1] << 8) | p[2],
                    "rgba8" => ((opaqueRgba ? 0xFFu : p[3]) << 24) | ((uint)p[0] << 16) | ((uint)p[1] << 8) | p[2],
                    "bgra8" => ((opaqueBgra ? 0xFFu : p[3]) << 24) | ((uint)p[2] << 16) | ((uint)p[1] << 8) | p[0],
                    "rgb565" => Rgb565(BinaryPrimitives.ReadUInt16LittleEndian(p)),
                    _ => palette is not null && p[0] < palette.Length ? palette[p[0]] : 0xFF000000u,
                };
            }
        }

        return new BitmapVisual
        {
            NodePath = node.Path,
            Width = shownWidth,
            Height = shownHeight,
            Format = format,
            Pixels = pixels,
            Truncated = truncated,
            SourceLength = node.Size,
        };
    }

    /// <summary>
    /// 4 バイトの画素のアルファがすべて 0 か (32 bit の BI_RGB の BMP では 4 バイト目は予約で 0。その場合は不透明として表示する)。
    /// </summary>
    private static bool AllAlphaZero(byte[] data, int bpp, int alphaIndex)
    {
        if (data.Length < bpp)
        {
            return false;
        }

        for (int i = alphaIndex; i < data.Length; i += bpp)
        {
            if (data[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static uint Rgb565(ushort v)
    {
        uint r = (uint)((v >> 11) & 0x1F);
        uint g = (uint)((v >> 5) & 0x3F);
        uint b = (uint)(v & 0x1F);
        return 0xFF000000u | (((r << 3) | (r >> 2)) << 16) | (((g << 2) | (g >> 4)) << 8) | ((b << 3) | (b >> 2));
    }

    /// <summary>
    /// <c>indexed8</c> のパレット (4 番目の引数)。パレットのパターン (パス) か Hex の文字列。5 番目の引数でパレットの形式
    /// (<c>rgb8</c> (既定) か <c>bgra8</c>。BMP のパレットは bgra8) を選べる。
    /// </summary>
    private static uint[] Palette(TemplateNode node, IReadOnlyList<string> args)
    {
        if (args.Count < 4 || string.IsNullOrWhiteSpace(args[3]))
        {
            throw new ArgumentException(Msg("indexed8 にはパレットの引数が必要です。", "indexed8 needs a palette argument."));
        }

        string spec = args[3].Trim();
        byte[] bytes;
        if (node.Result.FindByPath(spec) is { } paletteNode)
        {
            bytes = ReadNode(paletteNode, 1024, out _);
        }
        else
        {
            string hex = new([.. spec.Where(Uri.IsHexDigit)]);
            if (hex.Length == 0 || hex.Length % 2 != 0)
            {
                throw new ArgumentException(Msg($"パレットが見つかりません: {spec}", $"The palette was not found: {spec}"));
            }

            bytes = Convert.FromHexString(hex);
        }

        bool bgra = args.Count > 4 && args[4].Trim().Equals("bgra8", StringComparison.OrdinalIgnoreCase);
        int size = bgra ? 4 : 3;
        uint[] palette = new uint[Math.Min(256, bytes.Length / size)];
        for (int i = 0; i < palette.Length; i++)
        {
            ReadOnlySpan<byte> p = bytes.AsSpan(i * size, size);
            palette[i] = bgra
                ? 0xFF000000u | ((uint)p[2] << 16) | ((uint)p[1] << 8) | p[0]
                : 0xFF000000u | ((uint)p[0] << 16) | ((uint)p[1] << 8) | p[2];
        }

        return palette;
    }

    // ───────────── 画像 (ファイル形式) ─────────────

    private static TemplateVisual Image(TemplateNode node)
    {
        byte[] data = ReadNode(node, MaxDataBytes, out bool truncated);
        return new EncodedImageVisual
        {
            NodePath = node.Path,
            Data = data,
            FormatName = EncodedImageVisual.Sniff(data),
            Truncated = truncated,
            SourceLength = node.Size,
        };
    }

    // ───────────── 音声 ─────────────

    private static TemplateVisual Audio(TemplateNode node, IReadOnlyList<string> args)
    {
        int rate = IntArg(args, 0, "sampleRate");
        int channels = IntArg(args, 1, "channels");
        string formatName = args.Count > 2 ? args[2] : "pcm_s16le";
        if (!PcmAudio.TryParse(formatName, out PcmFormat format))
        {
            throw new ArgumentException(Msg($"音声の形式が違います: {formatName}", $"Unknown audio format: {formatName}"));
        }

        if (rate is <= 0 or > 1_000_000 || channels is <= 0 or > 32)
        {
            throw new ArgumentException(Msg("サンプルレートまたはチャンネル数が範囲外です。", "The sample rate or channel count is out of range."));
        }

        byte[] data = ReadNode(node, MaxDataBytes, out bool truncated);
        int frame = PcmAudio.BytesPerSample(format) * channels;
        if (data.Length % frame != 0)
        {
            Array.Resize(ref data, data.Length / frame * frame);
        }

        return new AudioVisual
        {
            NodePath = node.Path,
            SampleRate = rate,
            Channels = channels,
            Format = format,
            Data = data,
            Samples = PcmAudio.Decode(data, format),
            Truncated = truncated,
            SourceLength = node.Size,
        };
    }

    // ───────────── 逆アセンブル ─────────────

    private static TemplateVisual Disasm(TemplateNode node, IReadOnlyList<string> args)
    {
        string arch = args.Count > 0 && args[0].Length > 0 ? args[0].Trim() : DisasmArchitectures.Default;
        ulong address = 0;
        if (args.Count > 1)
        {
            if (!TryNumber(args[1], out long a))
            {
                throw new ArgumentException(Msg("ベースアドレスが数値ではありません。", "The base address is not a number."));
            }

            address = unchecked((ulong)a);
        }

        DisasmArchitecture architecture = DisasmArchitectures.Find(arch)
            ?? throw new ArgumentException(Msg($"未知のアーキテクチャです: {arch}", $"Unknown architecture: {arch}"));
        byte[] data = ReadNode(node, MaxDataBytes, out bool truncated);
        DisasmFormat format = DisasmFormat.Default;
        using IInstructionDecoder decoder = Disassembler.Create(architecture, format);
        IReadOnlyList<DisasmInstruction> list = Disassembler.Sweep(decoder, data, address, format, MaxInstructions);
        long decoded = list.Count == 0 ? 0 : (long)(list[^1].Address - address) + list[^1].Length;
        bool more = decoded < data.Length;
        var lines = new List<string>(list.Count);
        foreach (DisasmInstruction i in list)
        {
            long at = (long)(i.Address - address);
            string bytes = Convert.ToHexString(data, (int)at, Math.Min(i.Length, 8));
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{i.Address.ToString(architecture.WideAddress ? "X16" : "X8", CultureInfo.InvariantCulture)}  {SpacedHex(bytes),-24}  {i.Mnemonic} {i.Operands}").TrimEnd());
        }

        return new DisassemblyVisual
        {
            NodePath = node.Path,
            Architecture = architecture.Id,
            BaseAddress = address,
            Instructions = list,
            Lines = lines,
            Truncated = truncated || more,
            SourceLength = node.Size,
        };
    }

    private static string SpacedHex(string hex)
    {
        var sb = new StringBuilder(hex.Length * 3 / 2);
        for (int i = 0; i < hex.Length; i += 2)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(hex, i, 2);
        }

        return sb.ToString();
    }

    // ───────────── 表 ─────────────

    private static TemplateVisual Table(TemplateNode node, TemplateDisplayContext display)
    {
        if (node.Kind != TemplateNodeKind.Array)
        {
            if (node.HasChildren)
            {
                // 構造体 1 つは 1 行の表にする。
                IReadOnlyList<TemplateNode> members = node.Children;
                string[] cols = [.. members.Select(m => m.DisplayName)];
                string[] values = [.. members.Select(m => m.GetDisplay(display))];
                return new TableVisual(cols, 1, _ => values) { NodePath = node.Path, SourceLength = node.Size };
            }

            throw new ArgumentException(Msg("表にできるのは構造体の配列です。", "Only an array of structures can be shown as a table."));
        }

        long count = node.ChildCount;
        long elementSize = count == 0 ? 0 : Math.Max(1, node.Size / count);
        long shown = elementSize == 0 ? count : Math.Min(count, Math.Max(1, MaxDataBytes / elementSize));
        if (count == 0)
        {
            return new TableVisual([ValueColumn], 0, _ => []) { NodePath = node.Path, SourceLength = node.Size };
        }

        TemplateNode first = node.GetChild(0);
        bool structured = first.HasChildren && first.Kind is not TemplateNodeKind.Array and not TemplateNodeKind.Pointer;
        string[] columns = structured ? [.. first.Children.Select(m => m.DisplayName)] : [ValueColumn];
        return new TableVisual(columns, shown, i =>
        {
            TemplateNode element = node.GetChild(i);
            if (!structured)
            {
                return [element.GetDisplay(display)];
            }

            IReadOnlyList<TemplateNode> members = element.Children;
            string[] row = new string[columns.Length];
            for (int c = 0; c < row.Length; c++)
            {
                row[c] = c < members.Count ? members[c].GetDisplay(display) : string.Empty;
            }

            return row;
        })
        { NodePath = node.Path, Truncated = shown < count, SourceLength = node.Size };
    }

    /// <summary>要素が構造体でない配列の表の列の名前。</summary>
    public const string ValueColumn = "value";

    // ───────────── グラフ ─────────────

    private static TemplateVisual Chart(TemplateNode node, IReadOnlyList<string> args, CancellationToken ct)
    {
        TemplateChartKind chart = args.Count > 0 && args[0].Trim().Equals("bar", StringComparison.OrdinalIgnoreCase) ? TemplateChartKind.Bar : TemplateChartKind.Line;
        if (node.Kind != TemplateNodeKind.Array)
        {
            throw new ArgumentException(Msg("グラフにできるのは数値の配列です。", "Only an array of numbers can be shown as a chart."));
        }

        long count = node.ChildCount;
        long elementSize = count == 0 ? 1 : Math.Max(1, node.Size / count);
        long shown = Math.Min(count, Math.Max(1, MaxDataBytes / elementSize));
        double[] values;
        if (node.IsByteOrCharArray && elementSize == 1)
        {
            byte[] data = ReadNode(node, shown, out _);
            values = [.. data.Select(b => (double)b)];
        }
        else
        {
            values = new double[shown];
            for (long i = 0; i < shown; i++)
            {
                if ((i & 0xFFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                values[i] = ToDouble(node.GetChild(i).Value)
                    ?? throw new ArgumentException(Msg($"[{i}] は数値ではありません。", $"[{i}] is not a number."));
            }
        }

        return new ChartVisual { NodePath = node.Path, Chart = chart, Values = values, Truncated = shown < count, SourceLength = node.Size };
    }

    private static double? ToDouble(object? value) => value switch
    {
        double d => d,
        float f => f,
        long l => l,
        ulong u => u,
        int i => i,
        uint u => u,
        Int128 i => (double)i,
        UInt128 u => (double)u,
        bool b => b ? 1 : 0,
        _ => null,
    };

    // ───────────── 色 ─────────────

    private static TemplateVisual Color(TemplateNode node, TemplateDisplayContext display)
    {
        uint argb;
        if (node.ColorArgb is { } c)
        {
            argb = c;
        }
        else if (node.Value is long or ulong or int or uint && Convert.ToUInt64(node.Value, CultureInfo.InvariantCulture) is var v)
        {
            argb = v <= 0xFFFFFF ? 0xFF000000u | (uint)v : unchecked((uint)v);
        }
        else
        {
            throw new ArgumentException(Msg("色の型でも整数でもありません。", "The pattern is neither a color type nor an integer."));
        }

        return new ColorVisual { NodePath = node.Path, Argb = argb, ValueText = node.GetDisplay(display), SourceLength = node.Size };
    }

    // ───────────── テキスト ─────────────

    private static TemplateVisual Text(TemplateNode node, IReadOnlyList<string> args)
    {
        string? encoding = args.Count > 0 && args[0].Trim().Length > 0 ? args[0].Trim() : null;
        string text;
        bool truncated = false;
        if (encoding is null && node.Value is string s)
        {
            text = s;
            encoding = node.Attributes.Encoding ?? node.Result.Options.DefaultEncoding;
        }
        else
        {
            encoding ??= node.Attributes.Encoding ?? "utf-8";
            byte[] data = ReadNode(node, MaxDataBytes, out truncated);
            text = TextVisual.Decode(data, encoding);
        }

        return new TextVisual { NodePath = node.Path, Text = text, Encoding = encoding, Truncated = truncated, SourceLength = node.Size };
    }
}
