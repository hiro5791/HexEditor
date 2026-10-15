using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexEditor.Core.Scripting.Debugging;

/// <summary>
/// ブレークポイント (AUTO-21 の仕様 1)。行ごとに、有効 / 無効、条件 (式が真のときだけ止まる)、ヒット回数 (N 回目以降に止まる) を持つ。
/// </summary>
public sealed record Breakpoint(int Line)
{
    public bool Enabled { get; init; } = true;

    /// <summary>条件の式。空なら常に止まる。</summary>
    public string? Condition { get; init; }

    /// <summary>ヒット回数: この回数に達してから止まる (0 以下は指定なし)。</summary>
    public int HitCount { get; init; }

    public bool HasCondition => !string.IsNullOrWhiteSpace(Condition);
}

/// <summary>
/// ブレークポイントの保存先 (アプリ全体で 1 つ)。スクリプトのパスごとに保存し、次回も使える (AUTO-21 の仕様 8)。
/// </summary>
public sealed class BreakpointStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly string? _file;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<Breakpoint>> _byScript = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="file">保存先 (JSON)。null ならメモリ上だけ。</param>
    public BreakpointStore(string? file)
    {
        _file = file;
        Load();
    }

    /// <summary>変わった (スクリプトのパス)。どのスレッドからでも発生する。</summary>
    public event EventHandler<string>? Changed;

    private static string Key(string path) => Path.GetFullPath(path);

    public IReadOnlyList<Breakpoint> For(string path)
    {
        lock (_lock)
        {
            return _byScript.TryGetValue(Key(path), out List<Breakpoint>? list) ? [.. list.OrderBy(b => b.Line)] : [];
        }
    }

    public Breakpoint? Find(string path, int line) => For(path).FirstOrDefault(b => b.Line == line);

    /// <summary>設定・解除を切り替える (F9)。設定したら true。</summary>
    public bool Toggle(string path, int line)
    {
        bool added;
        lock (_lock)
        {
            if (!_byScript.TryGetValue(Key(path), out List<Breakpoint>? list))
            {
                _byScript[Key(path)] = list = [];
            }

            int i = list.FindIndex(b => b.Line == line);
            added = i < 0;
            if (added)
            {
                list.Add(new Breakpoint(line));
            }
            else
            {
                list.RemoveAt(i);
            }

            Save();
        }

        Changed?.Invoke(this, path);
        return added;
    }

    /// <summary>ブレークポイントを置き換える (条件・ヒット回数・有効 / 無効の変更。なければ追加)。</summary>
    public void Set(string path, Breakpoint breakpoint)
    {
        lock (_lock)
        {
            if (!_byScript.TryGetValue(Key(path), out List<Breakpoint>? list))
            {
                _byScript[Key(path)] = list = [];
            }

            int i = list.FindIndex(b => b.Line == breakpoint.Line);
            if (i < 0)
            {
                list.Add(breakpoint);
            }
            else
            {
                list[i] = breakpoint;
            }

            Save();
        }

        Changed?.Invoke(this, path);
    }

    public void Remove(string path, int line)
    {
        lock (_lock)
        {
            if (_byScript.TryGetValue(Key(path), out List<Breakpoint>? list))
            {
                list.RemoveAll(b => b.Line == line);
                Save();
            }
        }

        Changed?.Invoke(this, path);
    }

    public void Clear(string path)
    {
        lock (_lock)
        {
            _byScript.Remove(Key(path));
            Save();
        }

        Changed?.Invoke(this, path);
    }

    /// <summary>行の挿入・削除で、ブレークポイントの行をずらす (エディタの編集に追従する)。</summary>
    public void Shift(string path, int fromLine, int delta)
    {
        lock (_lock)
        {
            if (!_byScript.TryGetValue(Key(path), out List<Breakpoint>? list))
            {
                return;
            }

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Line >= fromLine)
                {
                    int moved = list[i].Line + delta;
                    if (moved < fromLine)
                    {
                        list.RemoveAt(i);
                    }
                    else
                    {
                        list[i] = new Breakpoint(moved) { Enabled = list[i].Enabled, Condition = list[i].Condition, HitCount = list[i].HitCount };
                    }
                }
            }

            Save();
        }

        Changed?.Invoke(this, path);
    }

    private sealed record Entry(string Script, List<Breakpoint> Breakpoints);

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }

        try
        {
            foreach (Entry e in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_file), Json) ?? [])
            {
                _byScript[e.Script] = e.Breakpoints;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 壊れた保存先は空として扱う。
        }
    }

    private void Save()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_byScript.Where(kv => kv.Value.Count > 0).Select(kv => new Entry(kv.Key, kv.Value)).ToList(), Json));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても、このセッションの間は使える。
        }
    }
}
