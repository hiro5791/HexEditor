using HexEditor.Sdk.Plugins;

namespace HexEditor.Sdk.Testing;

/// <summary>
/// A plugin context for unit tests: records the registered extensions and the log lines.
/// <ja>単体テスト用のプラグインの環境。登録した拡張ポイントと出力の行を記録する。</ja>
/// </summary>
public sealed class FakePluginContext : IPluginContext
{
    /// <summary>Creates the context. <ja>環境を作る。</ja></summary>
    public FakePluginContext(string id = "test.plugin", string? folder = null, bool hasUi = false)
    {
        Id = id;
        Folder = folder ?? AppContext.BaseDirectory;
        HasUi = hasUi;
        Log = new FakeLog(Lines);
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Name { get; init; } = "Test Plugin";

    /// <inheritdoc />
    public string Version { get; init; } = "1.0.0";

    /// <inheritdoc />
    public string Folder { get; }

    /// <inheritdoc />
    public string SdkVersion => SdkInfo.Version;

    /// <inheritdoc />
    public bool HasUi { get; }

    /// <inheritdoc />
    public IPluginLog Log { get; }

    /// <inheritdoc />
    public IPluginStrings Strings { get; init; } = new FakeStrings();

    /// <summary>The registered extensions. <ja>登録した拡張ポイント。</ja></summary>
    public List<IExtension> Extensions { get; } = [];

    /// <summary>The log lines ("info: ...", "warning: ...", "error: ..."). <ja>出力の行 ("info: ..." など)。</ja></summary>
    public List<string> Lines { get; } = [];

    /// <inheritdoc />
    public void Register(IExtension extension) => Extensions.Add(extension ?? throw new ArgumentNullException(nameof(extension)));

    /// <summary>The registered extension of type <typeparamref name="T"/> with <paramref name="id"/>. <ja>ID が <paramref name="id"/> の <typeparamref name="T"/> の拡張ポイント。</ja></summary>
    public T Get<T>(string id) where T : class, IExtension => Extensions.OfType<T>().Single(e => e.Id == id);

    /// <summary>Runs a registered command against <paramref name="document"/>. <ja>登録したコマンドを <paramref name="document"/> に対して実行する。</ja></summary>
    public void Execute(string commandId, IPluginDocument? document = null) => Get<ICommand>(commandId).Execute(new FakeCommandContext(document, Log));

    private sealed class FakeLog(List<string> lines) : IPluginLog
    {
        public void Info(string text) => lines.Add("info: " + text);

        public void Warning(string text) => lines.Add("warning: " + text);

        public void Error(string text) => lines.Add("error: " + text);
    }
}

/// <summary>Strings that return the key (or a value set by the test). <ja>キーを返す文字列 (テストで値を設定できる)。</ja></summary>
public sealed class FakeStrings : IPluginStrings
{
    /// <summary>Values by key. <ja>キーごとの値。</ja></summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string Language { get; init; } = "en";

    /// <inheritdoc />
    public string Get(string key) => Values.TryGetValue(key, out string? value) ? value : key;

    /// <inheritdoc />
    public string Format(string key, params object?[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, Get(key), args);
}

/// <summary>A command context for unit tests. <ja>単体テスト用のコマンドの環境。</ja></summary>
public sealed class FakeCommandContext(IPluginDocument? document, IPluginLog log) : ICommandContext
{
    /// <inheritdoc />
    public IPluginDocument? Document => document;

    /// <inheritdoc />
    public IPluginLog Log => log;
}

/// <summary>An in-memory document for unit tests. <ja>単体テスト用のメモリ上のドキュメント。</ja></summary>
public sealed class FakePluginDocument(byte[] data, string name = "test.bin") : IPluginDocument
{
    /// <summary>The current bytes. <ja>今のバイト列。</ja></summary>
    public byte[] Data { get; private set; } = data;

    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public string? Path => null;

    /// <inheritdoc />
    public long Length => Data.Length;

    /// <inheritdoc />
    public long Cursor { get; set; }

    /// <inheritdoc />
    public (long Start, long Length) Selection { get; set; }

    /// <inheritdoc />
    public byte[] Read(long offset, int count) => Data.AsSpan((int)offset, (int)Math.Min(count, Data.Length - offset)).ToArray();

    /// <inheritdoc />
    public void Write(long offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(Data.AsSpan((int)offset));
}
