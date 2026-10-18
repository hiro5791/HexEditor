using HexEditor.Core.FileTypes;
using HexEditor.Core.Formats;
using HexEditor.Core.Hashing;
using HexEditor.Core.Scripting;
using HexEditor.Core.Templates.Extensions;
using HexEditor.Sdk.Plugins;
using CoreFileTypeDetector = HexEditor.Core.FileTypes.IFileTypeDetector;
using SdkFileTypeDetector = HexEditor.Sdk.Plugins.IFileTypeDetector;

namespace HexEditor.Core.Plugins;

/// <summary>
/// 拡張ポイントをアプリ本体の同じ種類の機能につなぐ (AUTO-31 の仕様 2: 本体と同じ場所・同じ見た目。プラグイン名をツールチップに出す)。
/// プラグインのコードの呼び出しはすべて <see cref="PluginGuard"/> を通す。データ量に比例する処理はチャンクで渡す (仕様 4)。
/// </summary>
public static class PluginIntegration
{
    /// <summary>登録簿の内容を本体の一覧 (ハッシュ、形式の判定) に反映する。登録簿が変わるたびに呼ぶ。</summary>
    public static void Apply(PluginManager manager)
    {
        HashCatalog.SetPlugins([.. manager.Registry.Usable<IHashAlgorithm>().Select(x => HashAlgorithmOf(manager, x.Registration, x.Extension))]);
        FileTypeDetector.GlobalDetectors = () => [.. manager.Registry.Usable<SdkFileTypeDetector>().Select(x => (CoreFileTypeDetector)new FileTypeAdapter(manager, x.Registration, x.Extension))];

        // 逆アセンブラ (06 の ANA-23。アーキテクチャの一覧の「その他」に出す)。呼び出しは PluginGuard を通す。
        Disassembly.DisasmArchitectures.SetPlugins([.. manager.Registry.Usable<IDisassembler>().Select(x =>
            (x.Registration.Id, (IDisassembler)new GuardedDisassembler(x.Registration, x.Extension), PluginNameOf(manager, x.Registration)))]);
    }

    /// <summary>プラグインの逆アセンブラの呼び出しを <see cref="PluginGuard"/> で包む (例外・止めたプラグインは「解釈できない」にする)。</summary>
    private sealed class GuardedDisassembler(RegisteredExtension registration, IDisassembler inner) : IDisassembler
    {
        public string Id => registration.Id;

        public string DisplayName => registration.DisplayName;

        public string Architecture => registration.Guard.Invoke("Architecture", () => inner.Architecture, registration.DisplayName);

        public IReadOnlyList<DisassembledInstruction> Disassemble(ReadOnlySpan<byte> code, long address, int maxInstructions)
        {
            // ReadOnlySpan はラムダに渡せないため、配列に写してから呼ぶ (渡すのは 1 命令ぶんの最大長だけ)。
            byte[] copy = code.ToArray();
            return registration.Guard.Invoke("Disassemble", () => inner.Disassemble(copy, address, maxInstructions), [], uiThread: true);
        }
    }

    /// <summary>登録簿が変わったら <see cref="Apply"/> し直す (App・CLI が 1 回呼ぶ)。</summary>
    public static void Attach(PluginManager manager)
    {
        Apply(manager);
        manager.Registry.Changed += (_, _) => Apply(manager);
        manager.Suspended += (_, _) => Apply(manager);
    }

    /// <summary>ツールチップなどに出すプラグイン名。</summary>
    public static string PluginNameOf(PluginManager manager, RegisteredExtension registration) => registration.Plugin.NameFor(manager.Options.UiLanguage);

    // ---- ハッシュ ----

    private static HashAlgorithmInfo HashAlgorithmOf(PluginManager manager, RegisteredExtension registration, IHashAlgorithm algorithm)
    {
        int bits = registration.Guard.Invoke("Bits", () => algorithm.Bits, 0);
        HashGroup group = bits <= 64 ? HashGroup.Checksum : HashGroup.NonCryptographic;
        return new HashAlgorithmInfo(registration.Id, registration.DisplayName, group, bits,
            _ => new PluginHasher(registration, registration.Guard.InvokeOrThrow("Create", algorithm.Create)))
        {
            PluginName = PluginNameOf(manager, registration),
        };
    }

    private sealed class PluginHasher(RegisteredExtension registration, IHashComputation computation) : IHasher
    {
        public void Append(ReadOnlySpan<byte> data)
        {
            // ReadOnlySpan はラムダに渡せないため、例外の扱いを直接書く。
            if (registration.Guard.IsStopped)
            {
                throw new PluginCallException(registration.Guard.Stopped());
            }

            try
            {
                computation.Append(data);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                registration.Guard.Fault("Append", ex);
                throw new PluginCallException(ex.Message, ex);
            }
        }

        public byte[] Finish() => registration.Guard.InvokeOrThrow("Finish", computation.Finish) ?? [];
    }

    // ---- インスペクタの型 ----

    /// <summary>プラグインのインスペクタの型 (スクリプトの <c>inspector.types</c> とインスペクタパネル)。</summary>
    public sealed record InspectorTypeEntry(RegisteredExtension Registration, IInspectorType Type, string PluginName)
    {
        public string Id => Registration.Id;

        public string Name => Registration.DisplayName;

        public int Size => Registration.Guard.Invoke("Size", () => Type.Size, 0);

        /// <summary>止めた・読めないときは null。</summary>
        public string? Decode(ReadOnlySpan<byte> data, bool bigEndian, bool uiThread)
        {
            int size = Size;
            if (Registration.Guard.IsStopped || size <= 0 || data.Length < size)
            {
                return null;
            }

            byte[] copy = data[..size].ToArray();
            bool endian = Registration.Guard.Invoke("HasEndian", () => Type.HasEndian, false) && bigEndian;
            return Registration.Guard.Invoke("Decode", () => Type.Decode(copy, endian), null, uiThread);
        }

        /// <summary>
        /// インスペクタの行の値 (ユーザー定義型の評価。別のスレッドから呼ぶ)。止めたプラグインは null、例外は <see cref="PluginCallException"/>
        /// (行に「エラー: メッセージ」を出す)。時間は UI スレッドの呼び出しと同じ決まりで見張る (AUTO-31 の仕様 5)。
        /// </summary>
        public string? DecodeOrThrow(ReadOnlySpan<byte> data, bool bigEndian)
        {
            if (Registration.Guard.IsStopped)
            {
                return null;
            }

            byte[] copy = data.ToArray();
            bool endian = Registration.Guard.Invoke("HasEndian", () => Type.HasEndian, false) && bigEndian;
            return Registration.Guard.InvokeOrThrow("Decode", () => Type.Decode(copy, endian), timed: true);
        }

        /// <summary>値の文字列からバイト列を作る (書き換え)。書き換えられない型・不正な値・止めたプラグインは null。</summary>
        public byte[]? Encode(string text, bool bigEndian)
        {
            if (Registration.Guard.IsStopped)
            {
                return null;
            }

            bool endian = Registration.Guard.Invoke("HasEndian", () => Type.HasEndian, false) && bigEndian;
            return Registration.Guard.Invoke("Encode", () => Type.Encode(text, endian), null);
        }
    }

    public static IReadOnlyList<InspectorTypeEntry> InspectorTypes(PluginManager? manager) => manager is null
        ? []
        : [.. manager.Registry.Usable<IInspectorType>().Select(x => new InspectorTypeEntry(x.Registration, x.Extension, PluginNameOf(manager, x.Registration)))];

    // ---- 形式の判定 ----

    private sealed class FileTypeAdapter(PluginManager manager, RegisteredExtension registration, SdkFileTypeDetector detector) : CoreFileTypeDetector
    {
        public string Name => $"{registration.DisplayName} ({PluginNameOf(manager, registration)})";

        public IEnumerable<FileTypeCandidate> Detect(IMagicData data)
        {
            byte[] head = new byte[(int)Math.Min(data.Length, 64 * 1024)];
            int n = data.Read(0, head);
            IReadOnlyList<FileTypeMatch> matches = registration.Guard.Invoke("Detect", () => detector.Detect(head.AsSpan(0, n)), []);
            return [.. matches.Take(16).Select(m => new FileTypeCandidate(m.Name, m.Mime ?? "application/octet-stream",
                m.Extension is { } e ? [e] : [], 50, [], Name))];
        }
    }

    // ---- インポート・エクスポート (11、03 の Copy As) ----

    /// <summary>プラグインのインポート形式 (ID で探す)。</summary>
    public static (RegisteredExtension Registration, IImportFormat Format)? FindImport(string id) =>
        PluginManager.Current?.Registry.Usable<IImportFormat>().FirstOrDefault(x => x.Registration.Id == id) is { Registration: not null } x ? x : null;

    public static (RegisteredExtension Registration, IExportFormat Format)? FindExport(string id) =>
        PluginManager.Current?.Registry.Usable<IExportFormat>().FirstOrDefault(x => x.Registration.Id == id) is { Registration: not null } x ? x : null;

    /// <summary>インポートできる形式 (ID、表示名、プラグイン名)。</summary>
    public static IReadOnlyList<(string Id, string Name, string Plugin)> ImportFormats() => PluginManager.Current is { } m
        ? [.. m.Registry.Usable<IImportFormat>().Select(x => (x.Registration.Id, x.Registration.DisplayName, PluginNameOf(m, x.Registration)))]
        : [];

    public static IReadOnlyList<(string Id, string Name, string Plugin, bool IsText)> ExportFormats() => PluginManager.Current is { } m
        ? [.. m.Registry.Usable<IExportFormat>().Select(x => (x.Registration.Id, x.Registration.DisplayName, PluginNameOf(m, x.Registration),
            x.Registration.Guard.Invoke("IsText", () => x.Extension.IsText, false)))]
        : [];

    /// <summary>プラグインの形式の自動判定 (先頭 64 KB)。どれにも当たらなければ null。</summary>
    public static string? DetectImport(ReadOnlySpan<byte> head)
    {
        if (PluginManager.Current is not { } m)
        {
            return null;
        }

        byte[] copy = head.ToArray();
        foreach ((RegisteredExtension registration, IImportFormat format) in m.Registry.Usable<IImportFormat>())
        {
            if (registration.Guard.Invoke("Detect", () => format.Detect(copy), false))
            {
                return registration.Id;
            }
        }

        return null;
    }

    /// <summary>プラグインの形式で変換する (入力を 1 MB ずつ渡す)。</summary>
    public static ImportResult Import(RegisteredExtension registration, IImportFormat format, Stream input, string tempDirectory, string displayName,
        ImportProgress? progress)
    {
        CancellationToken ct = progress?.CancellationToken ?? CancellationToken.None;
        using var builder = new SparseImageBuilder(tempDirectory);
        var sink = new BuilderSink(builder);
        IImportSession session = registration.Guard.InvokeOrThrow("Begin", () => format.Begin(new PluginOperation(ct)));
        byte[] buffer = new byte[1024 * 1024];
        long read = 0;
        int n;
        while ((n = input.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int count = n;
            registration.Guard.InvokeOrThrow("Write", () => session.Write(buffer.AsSpan(0, count), sink));
            read += n;
            progress?.Report(read);
        }

        registration.Guard.InvokeOrThrow("Complete", () => session.Complete(sink));
        SparseImage image = builder.SegmentCount <= 1 && builder.MinAddress is null or 0 ? builder.BuildContiguous(displayName) : builder.Build(alignLowest: false, 0, displayName);
        return new ImportResult(image, new ImportIssueList(), null) { Length = image.Length, DataBytes = image.DataBytes };
    }

    /// <summary>プラグインの形式で書き出す (範囲を 1 MB ずつ読んで渡す)。</summary>
    public static void Export(RegisteredExtension registration, IExportFormat format, ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges,
        Stream output, CancellationToken cancellationToken, Action<long>? progress)
    {
        long total = ranges.Sum(r => r.Length);
        long first = ranges.Count > 0 ? ranges[0].Offset : 0;
        var sink = new StreamSink(output);
        IExportSession session = registration.Guard.InvokeOrThrow("Begin", () => format.Begin(new ExportInfo(first, total, source.FileName), new PluginOperation(cancellationToken)));
        byte[] buffer = new byte[1024 * 1024];
        long done = 0;
        foreach ((long offset, long length) in ranges)
        {
            for (long at = offset; at < offset + length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, offset + length - at);
                source.Read(at, buffer.AsSpan(0, n));
                long chunkOffset = at;
                registration.Guard.InvokeOrThrow("Write", () => session.Write(chunkOffset, buffer.AsSpan(0, n), sink));
                at += n;
                done += n;
                progress?.Invoke(done);
            }
        }

        registration.Guard.InvokeOrThrow("Complete", () => session.Complete(sink));
    }

    private sealed class BuilderSink(SparseImageBuilder builder) : IByteSink
    {
        public long Position { get; set; }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return;
            }

            if (Position < 0)
            {
                throw new InvalidOperationException("Position must not be negative.");
            }

            builder.Add(Position, data);
            Position += data.Length;
        }
    }

    private sealed class StreamSink(Stream stream) : IByteSink
    {
        private long _position;

        public long Position
        {
            get => _position;
            set => throw new NotSupportedException("Export output is sequential.");
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            stream.Write(data);
            _position += data.Length;
        }
    }

    // ---- データ演算 (03) ----

    /// <summary>データ演算の一覧に出すプラグインの変換。</summary>
    public static IReadOnlyList<(RegisteredExtension Registration, IDataTransform Transform)> DataTransforms() =>
        PluginManager.Current?.Registry.Usable<IDataTransform>() ?? [];

    // ---- テンプレートの関数 (07) ----

    /// <summary>テンプレートの関数を登録簿に入れる (登録簿が変わるたびに呼ぶ。プラグインの関数は名前の前に何も付けない)。</summary>
    public static void RegisterTemplateFunctions(PluginManager manager, TemplateExtensionRegistry registry)
    {
        foreach ((RegisteredExtension registration, ITemplateFunction function) in manager.Registry.Usable<ITemplateFunction>())
        {
            string name = registration.Guard.Invoke("FunctionName", () => function.FunctionName, string.Empty);
            if (string.IsNullOrWhiteSpace(name) || registry.Find(name) is { } existing && existing.Owner != "plugin:" + registration.Id)
            {
                continue;
            }

            int count = registration.Guard.Invoke("ParameterCount", () => function.ParameterCount, 0);
            registry.Register(new TemplateExtensionFunction
            {
                Name = name,
                Owner = "plugin:" + registration.Id,
                Parameters = [.. Enumerable.Repeat(TemplateValueType.Any, Math.Clamp(count, 0, 16))],
                Invoke = (args, _) => registration.Guard.InvokeOrThrow("Invoke", () => function.Invoke([.. args.Select(TemplateArgument)])),
            });
        }
    }

    /// <summary>テンプレートの値をプラグインに渡す形にする (整数は long、浮動小数点は double)。</summary>
    private static object? TemplateArgument(object? value) => value switch
    {
        ulong u when u <= long.MaxValue => (long)u,
        Int128 i when i >= long.MinValue && i <= long.MaxValue => (long)i,
        int i => (long)i,
        float f => (double)f,
        Templates.TemplateNode node => node.Value,
        _ => value,
    };

    // ---- 出力 ----

    /// <summary>プラグインの出力元の行 (テスト用)。</summary>
    public static IReadOnlyList<ScriptOutputLine> OutputOf(PluginManager manager, PluginRecord plugin) =>
        manager.Options.Output?.LinesOf(manager.ChannelOf(plugin)) ?? [];
}
