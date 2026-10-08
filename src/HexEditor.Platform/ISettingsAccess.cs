namespace HexEditor.Platform;

/// <summary>
/// 設定 (settings.json。09 の UI-23) を読み書きする口。Platform は Core を参照しないため、App が SettingsStore をこの形で渡す。
/// テストでは <see cref="MemorySettings"/> を使う。
/// </summary>
public interface ISettingsAccess
{
    string GetString(string key, string defaultValue);

    bool GetBool(string key, bool defaultValue);

    /// <summary>既定値と同じ値を書くと、ファイルからキーを消す (UI-23 の「既定値と違う値だけを書く」)。</summary>
    void SetString(string key, string value, string defaultValue);
}

/// <summary>メモリの中だけの設定 (テスト用、設定を読めないときの代わり)。</summary>
public sealed class MemorySettings : ISettingsAccess
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public MemorySettings(IEnumerable<KeyValuePair<string, object>>? values = null)
    {
        foreach ((string key, object value) in values ?? [])
        {
            _values[key] = value;
        }
    }

    public object? this[string key]
    {
        get => _values.GetValueOrDefault(key);
        set
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }
        }
    }

    public string GetString(string key, string defaultValue) => _values.TryGetValue(key, out object? v) && v is string s ? s : defaultValue;

    public bool GetBool(string key, bool defaultValue) => _values.TryGetValue(key, out object? v) && v is bool b ? b : defaultValue;

    public void SetString(string key, string value, string defaultValue)
    {
        if (value == defaultValue)
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }
    }

    public void SetBool(string key, bool value) => _values[key] = value;
}
