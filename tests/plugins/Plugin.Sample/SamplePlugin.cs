using System.Globalization;
using System.Text;
using HexEditor.Sdk.Plugins;

namespace Plugin.Sample;

/// <summary>
/// サンプルプラグイン (TD-AUTO-PLG-SAMPLE)。拡張ポイントを 1 つずつ登録する (08 の AUTO-31 の受け入れ基準 1)。
/// </summary>
public sealed class SamplePlugin : IPlugin
{
    public void Initialize(IPluginContext context)
    {
        string p = context.Id;
        context.Register(new NibbleSwap(p));
        context.Register(new Sum8(p));
        context.Register(new HexLines(p));
        context.Register(new PatternSource(p));
        context.Register(new NotView(p));
        context.Register(new NotTransform(p));
        context.Register(new Hello(p));
        context.Register(new HelloMenu(p));
        context.Register(new DoubleFunction(p));
        context.Register(new Detector(p));
        context.Register(new Colors(p));
        context.Register(new Disassembler(p));
        context.Register(new ScriptApi(p));
        if (context.HasUi)
        {
            // WinUI の型はウィンドウのあるアプリでだけ使う (別のメソッドにして、ヘッドレス実行では JIT させない)。
            SampleUi.Register(context);
        }
    }

    private abstract class Extension(string id, string name) : IExtension
    {
        public string Id { get; } = id;

        public string DisplayName { get; } = name;
    }

    /// <summary>上位と下位のニブルを入れ替えた値 (例: 12 → 21)。</summary>
    private sealed class NibbleSwap(string p) : Extension(p + ".nibble-swap8", "nibble-swap8"), IInspectorType
    {
        public int Size => 1;

        public string Decode(ReadOnlySpan<byte> data, bool bigEndian) => ((byte)((data[0] << 4) | (data[0] >> 4))).ToString("X2", CultureInfo.InvariantCulture);

        public byte[]? Encode(string text, bool bigEndian)
        {
            byte b = byte.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return [(byte)((b << 4) | (b >> 4))];
        }
    }

    /// <summary>全バイトの和の下位 8 bit。</summary>
    private sealed class Sum8(string p) : Extension(p + ".sum8", "sum8 (Sample)"), IHashAlgorithm
    {
        public int Bits => 8;

        public IHashComputation Create() => new State();

        private sealed class State : IHashComputation
        {
            private byte _sum;

            public void Append(ReadOnlySpan<byte> data)
            {
                foreach (byte b in data)
                {
                    _sum += b;
                }
            }

            public byte[] Finish() => [_sum];
        }
    }

    /// <summary>1 行に 16 バイトの Hex を空白で区切った形式 (インポート・エクスポート)。</summary>
    private sealed class HexLines(string p) : Extension(p + ".hexlines", "sample-hexlines"), IImportFormat, IExportFormat
    {
        public IReadOnlyList<string> FileExtensions => [".hexlines"];

        public string FileExtension => ".hexlines";

        public bool IsText => true;

        public bool Detect(ReadOnlySpan<byte> head) => head.StartsWith("#hexlines"u8);

        public IImportSession Begin(PluginOperation operation) => new Import();

        public IExportSession Begin(ExportInfo info, PluginOperation operation) => new Export();

        private sealed class Import : IImportSession
        {
            private readonly StringBuilder _pending = new();

            public void Write(ReadOnlySpan<byte> input, IByteSink output)
            {
                _pending.Append(Encoding.ASCII.GetString(input));
                string text = _pending.ToString();
                int last = text.LastIndexOf('\n');
                if (last < 0)
                {
                    return;
                }

                Lines(text[..last], output);
                _pending.Clear().Append(text[(last + 1)..]);
            }

            public void Complete(IByteSink output) => Lines(_pending.ToString(), output);

            private static void Lines(string text, IByteSink output)
            {
                foreach (string line in text.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith('#'))
                    {
                        continue;
                    }

                    byte[] bytes = [.. t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
                    output.Write(bytes);
                }
            }
        }

        private sealed class Export : IExportSession
        {
            private int _column;

            public void Write(long offset, ReadOnlySpan<byte> data, IByteSink output)
            {
                var sb = new StringBuilder();
                foreach (byte b in data)
                {
                    sb.Append(_column == 0 ? "" : " ").Append(b.ToString("X2", CultureInfo.InvariantCulture));
                    if (++_column == 16)
                    {
                        sb.Append('\n');
                        _column = 0;
                    }
                }

                output.Write(Encoding.ASCII.GetBytes(sb.ToString()));
            }

            public void Complete(IByteSink output)
            {
                if (_column != 0)
                {
                    output.Write("\n"u8);
                }
            }
        }
    }

    /// <summary>オフセット i の値が i の下位 8 bit のデータ (長さは引数)。</summary>
    private sealed class PatternSource(string p) : Extension(p + ".pattern", "sample-pattern"), IByteSourceProvider
    {
        public bool IsReadOnly => true;

        public bool CanResize => false;

        public string? ArgumentLabel => "Length";

        public IPluginByteSource Open(string? argument) => new Source(long.Parse(argument ?? "256", CultureInfo.InvariantCulture));

        private sealed class Source(long length) : IPluginByteSource
        {
            public string DisplayName => $"sample-pattern ({length})";

            public long Length => length;

            public int Read(long offset, Span<byte> buffer)
            {
                int n = (int)Math.Clamp(length - offset, 0, buffer.Length);
                for (int i = 0; i < n; i++)
                {
                    buffer[i] = (byte)(offset + i);
                }

                return n;
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>ビット反転して見せるビュー。</summary>
    private sealed class NotView(string p) : Extension(p + ".not-view", "sample-not"), ITransformView
    {
        public bool CanInverse => true;

        public void Forward(long offset, Span<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)~data[i];
            }
        }

        public void Inverse(long offset, Span<byte> data) => Forward(offset, data);
    }

    /// <summary>データ演算: ビット反転。</summary>
    private sealed class NotTransform(string p) : Extension(p + ".not", "sample-not"), IDataTransform
    {
        public void Apply(long offset, Span<byte> data, PluginOperation operation)
        {
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)~data[i];
            }
        }
    }

    private sealed class Hello(string p) : Extension(p + ".hello", "sample: hello"), ICommand
    {
        public void Execute(ICommandContext context) => context.Log.Info("hello");
    }

    private sealed class HelloMenu(string p) : Extension(p + ".hello-menu", "sample: hello"), IMenuContribution
    {
        public string CommandId { get; } = p + ".hello";

        public MenuLocation Location => MenuLocation.ToolsPlugins;
    }

    /// <summary>テンプレートの関数 sample_double(x) = 2x。</summary>
    private sealed class DoubleFunction(string p) : Extension(p + ".double", "sample_double"), ITemplateFunction
    {
        public string FunctionName => "sample_double";

        public int ParameterCount => 1;

        public object? Invoke(IReadOnlyList<object?> args) => args[0] switch
        {
            long l => l * 2,
            double d => d * 2,
            _ => throw new ArgumentException("sample_double expects a number"),
        };
    }

    /// <summary>先頭が "SMPL" のデータを「Sample format」と判定する。</summary>
    private sealed class Detector(string p) : Extension(p + ".detector", "Sample format"), IFileTypeDetector
    {
        public IReadOnlyList<FileTypeMatch> Detect(ReadOnlySpan<byte> head) =>
            head.StartsWith("SMPL"u8) ? [new FileTypeMatch("Sample format", "application/x-sample", "smpl")] : [];
    }

    private sealed class Colors(string p) : Extension(p + ".colors", "Sample colors"), IColorRuleProvider
    {
        public IReadOnlyList<PluginColorRule> Rules => [new PluginColorRule("Sample FF", "byteValues", "FF", null, 0xFFCC00)];
    }

    private sealed class Disassembler(string p) : Extension(p + ".disasm", "sample-arch"), IDisassembler
    {
        public string Architecture => "sample-arch";

        public IReadOnlyList<DisassembledInstruction> Disassemble(ReadOnlySpan<byte> code, long address, int maxInstructions)
        {
            var list = new List<DisassembledInstruction>();
            for (int i = 0; i < code.Length && i < maxInstructions; i++)
            {
                list.Add(new DisassembledInstruction(address + i, 1, "db 0x" + code[i].ToString("X2", CultureInfo.InvariantCulture)));
            }

            return list;
        }
    }

    private sealed class ScriptApi(string p) : Extension(p + ".api", "Sample API"), IScriptApiProvider
    {
        public object CreateApi() => new SampleApi();
    }
}

/// <summary>スクリプトから <c>app.plugins.get("test.sample").api</c> で使うオブジェクト。</summary>
public sealed class SampleApi
{
    public string Greeting => "hello from sample";

    public double Twice(double x) => x * 2;
}
