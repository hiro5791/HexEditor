using System.Text.Json;

namespace HexEditor.Core.Scripting.Repl;

/// <summary>
/// コンソールの入力履歴 (AUTO-23 の仕様 5): 1,000 件まで保存し、再起動後も使える。同じ入力が続くときは 1 件にする。
/// ↑ / ↓ でたどる位置は <see cref="Navigator"/> が持つ。
/// </summary>
public sealed class ReplHistory
{
    public const int DefaultCapacity = 1000;

    private readonly string? _file;
    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly List<string> _entries = [];

    /// <param name="file">保存先 (JSON の配列)。null ならメモリ上だけ。</param>
    public ReplHistory(string? file, int capacity = DefaultCapacity)
    {
        _file = file;
        _capacity = Math.Max(1, capacity);
        Load();
    }

    /// <summary>古い順の履歴。</summary>
    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>入力を加える (空白だけの入力は加えない)。上限を超えたら古いものから消す。</summary>
    public void Add(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        lock (_lock)
        {
            if (_entries.Count == 0 || _entries[^1] != input)
            {
                _entries.Add(input);
            }

            while (_entries.Count > _capacity)
            {
                _entries.RemoveAt(0);
            }

            Save();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Save();
        }
    }

    public Navigator Navigate() => new(this);

    private void Load()
    {
        if (_file is null || !File.Exists(_file))
        {
            return;
        }

        try
        {
            List<string>? loaded = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_file));
            _entries.AddRange((loaded ?? []).TakeLast(_capacity));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 壊れた履歴は空として扱う。
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
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存できなくても、この起動の間は使える。
        }
    }

    /// <summary>履歴をたどる位置。↑ で前の入力、↓ で次の入力 (一番新しいところの次は、たどる前に書いていた入力)。</summary>
    public sealed class Navigator(ReplHistory history)
    {
        private int _index = -1;
        private string _draft = string.Empty;

        /// <summary>↑。今の入力を覚えておき、1 つ前の入力を返す。履歴がなければ null。</summary>
        public string? Up(string current)
        {
            IReadOnlyList<string> entries = history.Entries;
            if (entries.Count == 0)
            {
                return null;
            }

            if (_index < 0)
            {
                _draft = current;
                _index = entries.Count;
            }

            _index = Math.Max(0, _index - 1);
            return entries[_index];
        }

        /// <summary>↓。1 つ新しい入力、一番新しいところを過ぎたら、たどる前に書いていた入力。たどっていなければ null。</summary>
        public string? Down()
        {
            if (_index < 0)
            {
                return null;
            }

            IReadOnlyList<string> entries = history.Entries;
            _index++;
            if (_index >= entries.Count)
            {
                _index = -1;
                return _draft;
            }

            return entries[_index];
        }

        /// <summary>入力を実行した・編集した: たどる位置を戻す。</summary>
        public void Reset() => _index = -1;
    }
}
