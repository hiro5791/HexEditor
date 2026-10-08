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

    /// <summary>
    /// ファイルはあるが読めなかった (ほかのプロセスが使っている、アクセス権がないなど)。既定値で動き、ファイルを上書きしないよう
    /// 書き込みを止める (<see cref="SettingsStore.ReadOnly"/>)。理由は <see cref="SettingsStore.LoadError"/>。
    /// </summary>
    Unreadable,
}

/// <summary>外部で編集された設定ファイルが読めなかった (UI-23 の「エラー」)。<see cref="Line"/> は 1 始まり (0 は不明)。</summary>
public sealed record SettingsEditError(int Line, string Detail);

/// <summary>設定ファイルの書き込みの時点 (テスト用の強制終了。TC-UI-23-05)。</summary>
public enum SettingsWritePoint
{
    TempHalfWritten,
    BeforeReplace,
    AfterReplace,
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

    /// <summary>外部で編集されたファイルが JSON として読めなかった (直前の値を使い続ける)。</summary>
    public event Action<SettingsEditError>? ExternalEditFailed;

    /// <summary>
    /// 書き込みに失敗した (UI-22 の「エラー」。値はセッション中だけ有効)。引数は理由。失敗が続いても、成功するまでは 1 回だけ知らせる。
    /// 書き込みのスレッド (タイマー) から呼ばれることがある。
    /// </summary>
    public event Action<string>? WriteFailed;

    /// <summary>JSON として読めなかったファイルを残した場所 (<see cref="SettingsLoadStatus.Broken"/> のとき。残せなければ null)。</summary>
    public string? BrokenFilePath { get; private set; }

    /// <summary>ファイルが読めなかった理由 (<see cref="SettingsLoadStatus.Unreadable"/> のとき)。</summary>
    public string? LoadError { get; private set; }

    /// <summary>ファイルを読む。読めなければ既定値で動き、壊れたファイルを残す。</summary>
    public SettingsLoadStatus Load()
    {
        lock (_lock)
        {
            BrokenFilePath = null;
            LoadError = null;
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
                    string broken = Path.Combine(Folder, $"{FileName}.broken-{DateTime.Now:yyyyMMdd-HHmmss}");
                    File.Move(PathName, broken);
                    BrokenFilePath = broken;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 名前を変えられなければ、上書きしないよう書き込みを止める。
                    ReadOnly = true;
                }

                _values = [];
                return SettingsLoadStatus.Broken;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 読めないファイルを既定値で上書きしない。
                _values = [];
                ReadOnly = true;
                LoadError = ex.Message;
                return SettingsLoadStatus.Unreadable;
            }

            int version = Commands.KeyBindingsDocument.ReadVersion(parsed["$schemaVersion"], SchemaVersion);
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
                try
                {
                    File.Copy(PathName, Path.Combine(Folder, $"{FileName}.bak-v{version}"), overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 移行前のファイルを残せなければ、移行した内容で上書きしない。
                    ReadOnly = true;
                    LoadError = ex.Message;
                    return SettingsLoadStatus.Unreadable;
                }

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

    public double GetDouble(string key, double defaultValue)
    {
        lock (_lock)
        {
            return SettingDefinition.TryGetNumber(_values[key], out double d) ? d : defaultValue;
        }
    }

    /// <summary>値 (複製)。設定されていなければ null。</summary>
    public JsonNode? GetNode(string key)
    {
        lock (_lock)
        {
            return _values[key]?.DeepClone();
        }
    }

    /// <summary>既定値から変更されている (ファイルにある) か。</summary>
    public bool Contains(string key)
    {
        lock (_lock)
        {
            return _values.ContainsKey(key);
        }
    }

    /// <summary>ファイルにあるすべての値の複製 (知らないキーを含む)。</summary>
    public JsonObject Snapshot()
    {
        lock (_lock)
        {
            return (JsonObject)_values.DeepClone();
        }
    }

    /// <summary>値を変える。既定値と同じ (<paramref name="defaultValue"/> と JSON として等しい) か null なら、ファイルから消す。</summary>
    public void SetNode(string key, JsonNode? value, JsonNode? defaultValue) =>
        Set(key, value is null || JsonNode.DeepEquals(value, defaultValue) ? null : value.DeepClone());

    /// <summary>
    /// すべての値を置き換える (リセット・インポート。UI-24、UI-25)。変わったキーを <see cref="Changed"/> で知らせる。
    /// </summary>
    public void ReplaceAll(JsonObject values)
    {
        IReadOnlyCollection<string> changed;
        lock (_lock)
        {
            changed = _values.Select(p => p.Key).Union(values.Select(p => p.Key))
                .Where(k => _values[k]?.ToJsonString() != values[k]?.ToJsonString())
                .ToList();
            _values = (JsonObject)values.DeepClone();
            if (changed.Count > 0)
            {
                _dirty = true;
                _pendingKeys.UnionWith(changed);
                _writeTimer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
            }
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(changed);
        }
    }

    /// <summary>
    /// 書き込みの各時点で呼ぶ (テスト用のビルドの強制終了。TC-UI-23-05、テスト方針 7.2)。製品では null。
    /// </summary>
    public static Action<SettingsWritePoint>? WriteHook { get; set; }

    /// <summary>オブジェクト・配列の値を変える。null または空のオブジェクトならファイルから消す (表示設定の既定値など)。</summary>
    public void SetNode(string key, JsonNode? value) =>
        Set(key, value is null || value is JsonObject { Count: 0 } ? null : value.DeepClone());

    /// <summary>値を変える。既定値と同じなら、ファイルから消す (仕様 4)。</summary>
    public void SetString(string key, string value, string defaultValue) =>
        Set(key, value == defaultValue ? null : JsonValue.Create(value));

    public void SetBool(string key, bool value, bool defaultValue) =>
        Set(key, value == defaultValue ? null : JsonValue.Create(value));

    public void SetInt(string key, int value, int defaultValue) =>
        Set(key, value == defaultValue ? null : JsonValue.Create(value));

    /// <summary>
    /// まだ書いていない変更をすぐに書く (終了時など)。書けなければ <see cref="WriteFailed"/> で知らせ、例外は投げない
    /// (書き込みのタイマーのスレッドで例外を投げるとプロセスが終わるため)。
    /// </summary>
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

    /// <summary>今の内容ですぐに書く (ファイルがなくても書く。「設定ファイルを開く」の前など)。書けたら true。</summary>
    public bool SaveNow()
    {
        lock (_lock)
        {
            return !_disposed && WriteNow();
        }
    }

    /// <summary>まだ書いていない変更がある (書き込み待ち、または書き込みに失敗した)。</summary>
    public bool HasPendingChanges
    {
        get
        {
            lock (_lock)
            {
                return _dirty;
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
    private bool _writeFailing;

    /// <summary>アプリ内で変えて、まだファイルに書いていないキー (外部の編集を読み直すときに失わないため)。</summary>
    private readonly HashSet<string> _pendingKeys = new(StringComparer.Ordinal);

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
            _pendingKeys.Add(key);
            _writeTimer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
        }

        Changed?.Invoke([key]);
    }

    /// <summary>書く (ロックの中で呼ぶ)。書けなければ変更を残したまま (次の変更でもう一度書く) <see cref="WriteFailed"/> で知らせる。</summary>
    private bool WriteNow()
    {
        if (ReadOnly)
        {
            _dirty = false;
            return false;
        }

        // 外部の編集を監視しているときは、通知を読み直す前に書くと外部の編集を上書きして失う (通知の処理が遅れたとき)。
        // 書く前に、前回の書き込みの後に変わったファイルを取り込む。
        if (_watcher is not null && MergeExternalEdits() is { Count: > 0 } external)
        {
            ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke(external));
        }

        _dirty = false;
        try
        {
            WriteFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dirty = true;
            if (!_writeFailing)
            {
                _writeFailing = true;
                WriteFailed?.Invoke(ex.Message);
            }

            return false;
        }

        _writeFailing = false;
        _pendingKeys.Clear();
        return true;
    }

    private void WriteFile()
    {
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
        byte[] bytes = new UTF8Encoding(false).GetBytes(root.ToJsonString(WriteOptions));
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // 一時ファイルに半分まで書いた時点 (テスト用の強制終了の時点)。
            stream.Write(bytes, 0, bytes.Length / 2);
            if (WriteHook is { } hook)
            {
                stream.Flush(flushToDisk: true);
                hook(SettingsWritePoint.TempHalfWritten);
            }

            stream.Write(bytes, bytes.Length / 2, bytes.Length - (bytes.Length / 2));
            stream.Flush(flushToDisk: true);
        }

        WriteHook?.Invoke(SettingsWritePoint.BeforeReplace);
        File.Move(temp, PathName, overwrite: true);
        WriteHook?.Invoke(SettingsWritePoint.AfterReplace);
        _lastWriteByUs = File.GetLastWriteTimeUtc(PathName);
    }

    private void ReloadFromDisk()
    {
        // 自分の書き込みによる通知は無視する。書き込み途中のファイルを読まないよう、少し待ってから読む。
        Thread.Sleep(100);
        IReadOnlyCollection<string> changed;
        lock (_lock)
        {
            changed = MergeExternalEdits();
        }

        if (changed.Count > 0)
        {
            Changed?.Invoke(changed);
        }
    }

    /// <summary>
    /// 外部で書き換えたファイルを読み、値に取り込む (ロックの中で呼ぶ)。変わったキーを返す。前回この store が書いた後に
    /// ファイルが変わっていなければ何もしない。アプリ内で変えてまだ書いていない値は、読み直した内容より優先する (変更を失わない)。
    /// </summary>
    private IReadOnlyCollection<string> MergeExternalEdits()
    {
        JsonObject parsed;
        try
        {
            if (!File.Exists(PathName) || File.GetLastWriteTimeUtc(PathName) == _lastWriteByUs)
            {
                return [];
            }

            parsed = Parse(File.ReadAllText(PathName));
        }
        catch (JsonException ex)
        {
            ExternalEditFailed?.Invoke(new SettingsEditError((int)(ex.LineNumber ?? -1) + 1, ex.Message));
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        parsed.Remove("$schema");
        parsed.Remove("$schemaVersion");
        foreach (string key in _pendingKeys)
        {
            if (_values[key] is { } local)
            {
                parsed[key] = local.DeepClone();
            }
            else
            {
                parsed.Remove(key);
            }
        }

        List<string> changed = [.. _values.Select(p => p.Key).Union(parsed.Select(p => p.Key))
            .Where(k => _values[k]?.ToJsonString() != parsed[k]?.ToJsonString())];
        _values = parsed;
        return changed;
    }

    private static JsonObject Parse(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            as JsonObject ?? throw new JsonException("The root of settings.json is not an object.", null, 0, 0);

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
