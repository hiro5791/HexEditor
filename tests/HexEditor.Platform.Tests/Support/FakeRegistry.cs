using HexEditor.Platform.Shell;

namespace HexEditor.Platform.Tests.Support;

/// <summary>
/// HKCU の代わり (この PC のレジストリには書かない)。値を持つキーだけを覚え、親のキーは子があれば「ある」とみなす
/// (実際のレジストリでサブキーを作ると親も作られるのと同じ)。
/// </summary>
public sealed class FakeRegistry : IUserRegistry
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandable = new(StringComparer.OrdinalIgnoreCase);

    public bool FailWrites { get; set; }

    /// <summary>値を書いたキー (空のキーとして作ったものを含む)。</summary>
    public IReadOnlyCollection<string> Keys => _keys.Keys;

    /// <summary>すべての値 (キー\名前 = 値)。差分を調べるのに使う。</summary>
    public IReadOnlyDictionary<string, string> Snapshot() =>
        _keys.SelectMany(k => k.Value.Select(v => (Key: $"{k.Key}|{v.Key}", v.Value))).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>テストの準備: 他のアプリの値を置く。</summary>
    public void Seed(string key, string? name, string value) => Set(key, name, value);

    public void SetValue(string key, string? name, string value)
    {
        if (FailWrites)
        {
            throw new UnauthorizedAccessException();
        }

        Set(key, name, value);
    }

    public string? GetValue(string key, string? name) =>
        _keys.TryGetValue(key, out Dictionary<string, string>? values) && values.TryGetValue(name ?? string.Empty, out string? v) ? v : null;

    public bool KeyExists(string key) => _keys.Keys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase));

    public void DeleteKeyTree(string key)
    {
        foreach (string k in _keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _keys.Remove(k);
        }
    }

    public void DeleteValue(string key, string name)
    {
        if (FailWrites)
        {
            throw new UnauthorizedAccessException();
        }

        if (_keys.TryGetValue(key, out Dictionary<string, string>? values))
        {
            values.Remove(name);

            // 値がなくなってもキーは残る (実際のレジストリと同じ)。
        }
    }

    public (string Value, bool Expandable)? GetRawString(string key, string name) =>
        GetValue(key, name) is { } value ? (value, _expandable.Contains($"{key}|{name}")) : null;

    public void SetString(string key, string name, string value, bool expandable)
    {
        SetValue(key, name, value);
        if (expandable)
        {
            _expandable.Add($"{key}|{name}");
        }
        else
        {
            _expandable.Remove($"{key}|{name}");
        }
    }

    /// <summary>テストの準備: 種類を指定して値を置く (REG_EXPAND_SZ の PATH など)。</summary>
    public void SeedString(string key, string name, string value, bool expandable)
    {
        Set(key, name, value);
        if (expandable)
        {
            _expandable.Add($"{key}|{name}");
        }
    }

    public bool IsKeyEmpty(string key) =>
        KeyExists(key)
        && (!_keys.TryGetValue(key, out Dictionary<string, string>? values) || values.Count == 0)
        && !_keys.Keys.Any(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase));

    private void Set(string key, string? name, string value)
    {
        if (!_keys.TryGetValue(key, out Dictionary<string, string>? values))
        {
            _keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
        }

        values[name ?? string.Empty] = value;
    }
}
