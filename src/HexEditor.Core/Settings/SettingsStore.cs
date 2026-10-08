using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Settings;

/// <summary>設定ファイルを読んだ結果 (UI-23 の「エラー」と仕様 6)。</summary>
public enum SettingsLoadStatus
{
    /// <summary>読めた (ファイルがない場合を含む)。</summary>
    Ok,

    /// <summary>JSON として読めなかった。既定値で動き、壊れたファイルは別名で残した。</summary>
    Broken,

    /// <summary>新しい版のアプリが書いたファイル。読めるキーだけ使い、書き込みは止める。</summary>
    TooNew,

    /// <summary>古い形式を移行した。移行前のファイルは .bak-v&lt;版&gt; で残した。</summary>
    Migrated,
}

/// <summary>
/// 設定 (<c>settings.json</c>。UI-23)。設定キーを <c>.</c> で区切った平らな JSON で、既定値と違う値だけを書く。
/// 知らないキーは捨てずに残す。書き込みは一時ファイルに書いてから置き換え、変更から 500 ms 待ってまとめて書く。
/// 外部で編集されたら読み直す。スレッドセーフ。
/// </summary>
public sealed class SettingsStore : IDisposable
{
    public const string FileName = "settings.json";
    public const int SchemaVersion = 1;
    public static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly Timer _writeTimer;
    private JsonObject _values = [];
    private FileSystemWatcher? _watcher;
    private DateTime _lastWriteByUs;
    private bool _disposed;

    public SettingsStore(string folder, string? schemaUrl = null)
    {
        Folder = folder;
        SchemaUrl = schemaUrl;
        _writeTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Folder { get; }

    public string PathName => Path.Combine(Folder, FileName);

    /// <summary>同梱の JSON スキーマの URL (<c>$schema</c> に書く。仕様 8)。</summary>
    public string? SchemaUrl { get; }

    /// <summary>書き込みを止めている (新しすぎるファイル、または書き込めないフォルダ)。</summary>
    public bool ReadOnly { get; set; }

    /// <summary>設定が変わった (アプリ内の変更、または外部の編集の読み直し)。引数は変わったキー。どのスレッドからも呼ばれる。</summary>
    public event Action<IReadOnlyCollection<string>>? Changed;

    /// <summary>外部で編集されたファイルが JSON として読めなかった (直前の値を使い続ける)。引数は理由 (行番号を含む)。</summary>
    public event Action<string>? ExternalEditFailed;

    /// <summary>ファイルを読む。読めなければ既定値で動き、壊れたファイルを残す。</summary>
    public SettingsLoadStatus Load()
    {
        lock (_lock)
        {
            if (!File.Exists(PathName))
            {
                _values = [];
                return SettingsLoadStatus.Ok;
            }

            JsonObject parsed;
            try
            {
                parsed = Parse(File.ReadAllText(PathName));
            }
            catch (JsonException)
            {
                // 壊れたファイルは別名で残し、既定値で起動する。
                try
                {
                    File.Move(PathName, Path.Combine(Folder, $"{FileName}.broken-{DateTime.Now:yyyyMMdd-HHmmss}"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }

                _values = [];
                return SettingsLoadStatus.Broken;
            }

            int version = parsed["$schemaVersion"]?.GetValue<int>() ?? SchemaVersion;
            parsed.Remove("$schema");
            parsed.Remove("$schemaVersion");
            _values = parsed;
            if (version > SchemaVersion)
            {
                ReadOnly = true;
                return SettingsLoadStatus.TooNew;
            }

            if (version < SchemaVersion)
            {
                File.Copy(PathName, Path.Combine(Folder, $"{FileName}.bak-v{version}"), overwrite: true);
                WriteNow();
                return SettingsLoadStatus.Migrated;
            }

            return SettingsLoadStatus.Ok;
        }
    }

    public string GetString(string key, string defaultValue)
    {
        lock (_lock)
        {
            return _values[key] is JsonValue v && v.TryGetValue(out string? s) ? s : defaultValue;
        }
    }

    public bool GetBool(string key, bool defaultValue)
    {
        lock (_lock)
        {
            return _values[key] is JsonValue v && v.TryGetValue(out bool b) ? b : defaultValue;
        }
    }

    public int GetInt(string key, int defaultValue)
    {
        lock (_lock)
        {
            return _values[key] is JsonValue v && v.TryGetValue(out int i) ? i : defaultValue;
        }
    }

    /// <summary>JSON の値 (配列・オブジェクト。検索履歴など) の写し。なければ null。</summary>
    public JsonNode? GetJson(string key)
    {
        lock (_lock)
        {
            return _values[key]?.DeepClone();
        }
    }

    /// <summary>JSON の値を変える。null ならファイルから消す。</summary>
    public void SetJson(string key, JsonNode? value) => Set(key, value?.DeepClone());

    /// <summary>値を変える。既定値と同じなら、ファイルから消す (仕様 4)。</summary>
    public void SetString(string key, string value, string defaultValue) =>
        Set(key, value == defaultValue ? null : JsonValue.Create(value));

    public void SetBool(string key, bool value, bool defaultValue) =>
        Set(key, value == defaultValue ? null : JsonValue.Create(value));

    public void SetInt(string key, int value, int defaultValue) =>
        Set(key, value == defaultValue ? null : JsonValue.Create(value));

    /// <summary>まだ書いていない変更をすぐに書く (終了時など)。</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_dirty && !_disposed)
            {
                WriteNow();
            }
        }
    }

    /// <summary>外部での編集を監視し、変更を 1 秒以内に読み直す (仕様 7)。</summary>
    public void StartWatching()
    {
        Directory.CreateDirectory(Folder);
        _watcher = new FileSystemWatcher(Folder, FileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => ReloadFromDisk();
        _watcher.Created += (_, _) => ReloadFromDisk();
        _watcher.Renamed += (_, _) => ReloadFromDisk();
    }

    private bool _dirty;

    private void Set(string key, JsonNode? value)
    {
        lock (_lock)
        {
            string? before = _values[key]?.ToJsonString();
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            if (before == value?.ToJsonString())
            {
                return;
            }

            _dirty = true;
            _writeTimer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke([key]);
    }

    private void WriteNow()
    {
        _dirty = false;
        if (ReadOnly)
        {
            return;
        }

        var root = new JsonObject();
        if (SchemaUrl is not null)
        {
            root["$schema"] = SchemaUrl;
        }

        root["$schemaVersion"] = SchemaVersion;
        foreach ((string key, JsonNode? value) in _values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            root[key] = value?.DeepClone();
        }

        Directory.CreateDirectory(Folder);
        string temp = PathName + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(WriteOptions), new UTF8Encoding(false));
        File.Move(temp, PathName, overwrite: true);
        _lastWriteByUs = File.GetLastWriteTimeUtc(PathName);
    }

    private void ReloadFromDisk()
    {
        // 自分の書き込みによる通知は無視する。書き込み途中のファイルを読まないよう、少し待ってから読む。
        Thread.Sleep(100);
        JsonObject parsed;
        lock (_lock)
        {
            try
            {
                if (!File.Exists(PathName) || File.GetLastWriteTimeUtc(PathName) == _lastWriteByUs)
                {
                    return;
                }

                parsed = Parse(File.ReadAllText(PathName));
            }
            catch (JsonException ex)
            {
                ExternalEditFailed?.Invoke(ex.LineNumber is { } line ? $"line {line + 1}: {ex.Message}" : ex.Message);
                return;
            }
            catch (IOException)
            {
                return;
            }

            parsed.Remove("$schema");
            parsed.Remove("$schemaVersion");
        }

        IReadOnlyCollection<string> changed;
        lock (_lock)
        {
            changed = _values.Select(p => p.Key).Union(parsed.Select(p => p.Key))
                .Where(k => _values[k]?.ToJsonString() != parsed[k]?.ToJsonString())
                .ToList();
            _values = parsed;
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(changed);
        }
    }

    private static JsonObject Parse(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? throw new JsonException("settings.json の先頭がオブジェクトではありません。");

    public void Dispose()
    {
        Flush();
        lock (_lock)
        {
            _disposed = true;
        }

        _writeTimer.Dispose();
        _watcher?.Dispose();
    }
}
