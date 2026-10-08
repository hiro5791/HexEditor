using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Settings;

/// <summary>
/// アプリの状態 (<c>state.json</c>。UI-23 の仕様 1): 最近使ったコマンド、閉じたタブ、ダイアログの「次回から表示しない」、
/// 最後に閉じたウィンドウのパネルの配置など。利用者が変える設定ではないので settings.json とは分ける。
/// キーは機能ごとに <c>&lt;機能&gt;.&lt;名前&gt;</c> (例: <c>commandPalette.recent</c>)。
/// 書き込みは一時ファイルに書いてから置き換え、変更から 500 ms 待ってまとめて書く。スレッドセーフ。
/// </summary>
public sealed class StateStore : IDisposable
{
    public const string FileName = "state.json";

    private readonly object _lock = new();
    private readonly Timer _writeTimer;
    private JsonObject _values = [];
    private bool _dirty;
    private bool _disposed;

    public StateStore(string folder)
    {
        Folder = folder;
        _writeTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Folder { get; }

    public string PathName => Path.Combine(Folder, FileName);

    /// <summary>
    /// 最初に <see cref="Load"/> したときに state.json があったか (初回起動の判定。09 の UI-38 の仕様 2)。読んだ後に他の機能が書いても
    /// 変わらない。
    /// </summary>
    public bool ExistedAtFirstLoad { get; private set; }

    private bool _loadedOnce;

    /// <summary>読む。読めなければ空で始める (状態は失っても困らない)。</summary>
    public void Load()
    {
        lock (_lock)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                ExistedAtFirstLoad = File.Exists(PathName);
            }

            try
            {
                _values = File.Exists(PathName) && JsonNode.Parse(File.ReadAllText(PathName)) is JsonObject o ? o : [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _values = [];
            }
        }
    }

    public JsonNode? Get(string key)
    {
        lock (_lock)
        {
            return _values[key]?.DeepClone();
        }
    }

    public void Set(string key, JsonNode? value)
    {
        lock (_lock)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value.DeepClone();
            }

            _dirty = true;
            if (!_disposed)
            {
                _writeTimer.Change(SettingsStore.WriteDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>すべて消す (UI-24 のリセットの「最近使ったファイルと状態」)。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _values = [];
            _dirty = true;
            WriteNow();
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            if (_dirty)
            {
                WriteNow();
            }
        }
    }

    private void WriteNow()
    {
        _dirty = false;
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = PathName + ".tmp";
            File.WriteAllText(temp, _values.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(temp, PathName, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 状態は保存できなくても動作を続ける。
        }
    }

    public void Dispose()
    {
        Flush();
        lock (_lock)
        {
            _disposed = true;
        }

        _writeTimer.Dispose();
    }
}
