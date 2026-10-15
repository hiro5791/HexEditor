using System.Globalization;
using System.Xml;
using HexEditor.Core.Scripting;
using HexEditor.Sdk.Plugins;

namespace HexEditor.Core.Plugins;

/// <summary><see cref="IPluginContext"/> の実装 (プラグインごとに 1 つ)。</summary>
internal sealed class PluginContext(PluginManager manager, PluginRecord record) : IPluginContext
{
    private readonly List<IExtension> _pending = [];
    private bool _committed;

    public string Id => record.Id;

    public string Name => record.NameFor(manager.Options.UiLanguage);

    public string Version => record.Version;

    public string Folder => record.Folder;

    public string SdkVersion => $"{manager.Options.AppSdkVersion.Major}.{manager.Options.AppSdkVersion.Minor}";

    public bool HasUi => manager.Options.HasUi;

    public IPluginLog Log { get; } = new PluginLog(manager, record);

    public IPluginStrings Strings { get; } = new PluginStrings(record.Folder, manager.Options.UiLanguage);

    public void Register(IExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        lock (_pending)
        {
            if (!_committed)
            {
                _pending.Add(extension);
                return;
            }
        }

        // Initialize の後の登録はすぐに反映する。
        manager.Commit(record, [extension]);
    }

    /// <summary>Initialize の中で登録したものを登録簿に入れる。</summary>
    internal void CommitPending()
    {
        List<IExtension> items;
        lock (_pending)
        {
            _committed = true;
            items = [.. _pending];
            _pending.Clear();
        }

        manager.Commit(record, items);
    }
}

/// <summary>出力パネルの「プラグイン: 名前」への出力。</summary>
internal sealed class PluginLog(PluginManager manager, PluginRecord record) : IPluginLog
{
    public void Info(string text) => manager.Write(record, ScriptOutputKind.Info, text ?? string.Empty);

    public void Warning(string text) => manager.Write(record, ScriptOutputKind.Warning, text ?? string.Empty);

    public void Error(string text) => manager.Write(record, ScriptOutputKind.Error, text ?? string.Empty);
}

/// <summary>
/// プラグインの文字列 (AUTO-31 の仕様 6)。<c>Strings/&lt;言語&gt;/Resources.resw</c> (アプリと同じ形式) を読む。表示言語、中立の言語、
/// 英語の順に探し、なければキーを返す。ファイルは信頼できない入力として読む (DTD を処理しない、大きさの上限)。
/// </summary>
internal sealed class PluginStrings : IPluginStrings
{
    private const long MaxBytes = 4 * 1024 * 1024;
    private readonly IReadOnlyList<Dictionary<string, string>> _tables;

    public PluginStrings(string folder, string language)
    {
        Language = language;
        var tables = new List<Dictionary<string, string>>();
        foreach (string lang in Candidates(language))
        {
            if (Load(Path.Combine(folder, "Strings", lang, "Resources.resw")) is { } table)
            {
                tables.Add(table);
            }
        }

        _tables = tables;
    }

    public string Language { get; }

    public string Get(string key)
    {
        foreach (Dictionary<string, string> table in _tables)
        {
            if (table.TryGetValue(key, out string? value))
            {
                return value;
            }
        }

        return key;
    }

    public string Format(string key, params object?[] args)
    {
        string format = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private static IEnumerable<string> Candidates(string language)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(language) && PluginManifest.IsSafeFileName(language))
        {
            list.Add(language);
            int dash = language.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                list.Add(language[..dash]);
            }
        }

        list.Add("en");
        list.Add("en-US");
        return list.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string>? Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes)
            {
                return null;
            }

            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes * 2 };
            using var reader = XmlReader.Create(path, settings);
            string? name = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.Name == "data")
                {
                    name = reader.GetAttribute("name");
                }
                else if (reader.Name == "value" && name is not null)
                {
                    table[name] = reader.ReadElementContentAsString();
                    name = null;
                }
            }

            return table;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
