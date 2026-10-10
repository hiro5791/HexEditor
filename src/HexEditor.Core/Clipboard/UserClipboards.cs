using System.Globalization;
using System.Text.Json;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Clipboard;

/// <summary>
/// ユーザークリップボード・クリップボード履歴の項目 1 つ (EDIT-28 の仕様 1・5)。コピー・切り取りと同じく範囲の参照で持ち、データを複製しない
/// (元のドキュメントを閉じるときは、ドキュメントの参照の実体化 (EDIT-24) で一時ファイルに書き出される)。終了後も残した項目は、
/// 読み込んだバイト列で持つ。
/// </summary>
public sealed class ClipboardEntry
{
    private readonly SnapshotRange? _range;
    private readonly byte[]? _data;
    private int _references;

    private ClipboardEntry(SnapshotRange? range, byte[]? data, long length, string sourceName, long sourceOffset, DateTimeOffset time, byte[] head)
    {
        _range = range;
        _data = data;
        Length = length;
        SourceName = sourceName;
        SourceOffset = sourceOffset;
        Time = time;
        Head = head;
    }

    /// <summary>大きさ (バイト)。</summary>
    public long Length { get; }

    /// <summary>コピー元のファイル名。</summary>
    public string SourceName { get; }

    /// <summary>コピー元のオフセット。</summary>
    public long SourceOffset { get; }

    /// <summary>コピーした日時。</summary>
    public DateTimeOffset Time { get; }

    /// <summary>先頭 16 バイト (パネルの表示。仕様 5)。</summary>
    public byte[] Head { get; }

    /// <summary>範囲の参照 (貼り付けに使う)。終了後も残した項目では null。</summary>
    public SnapshotRange? Range => _range;

    /// <summary>読み込んだバイト列 (終了後も残した項目)。参照で持つ項目では null。</summary>
    public byte[]? Data => _data;

    /// <summary>内容を読むデータソース。</summary>
    public IByteSource Source => (IByteSource?)_range ?? new MemoryByteSource(_data!);

    /// <summary>ドキュメントの範囲を参照する項目を作る。先頭 16 バイトはここで読む (キャッシュにあれば一瞬)。</summary>
    public static ClipboardEntry Capture(Document document, long offset, long length, DateTimeOffset? time = null)
    {
        SnapshotRange range = document.CreateRange(document.Current, offset, length);
        byte[] head = new byte[(int)Math.Min(16, length)];
        document.Current.Read(offset, head);
        return new ClipboardEntry(range, null, length, document.Source.DisplayName, offset, time ?? DateTimeOffset.Now, head);
    }

    /// <summary>バイト列の項目 (保存から読み込んだもの)。</summary>
    public static ClipboardEntry FromBytes(byte[] data, string sourceName, long sourceOffset, DateTimeOffset time) =>
        new(null, data, data.Length, sourceName, sourceOffset, time, data.AsSpan(0, Math.Min(16, data.Length)).ToArray());

    /// <summary>内容を全部読む (保存用。<paramref name="limit"/> を超える場合は null)。</summary>
    public byte[]? ReadAll(long limit)
    {
        if (Length > limit)
        {
            return null;
        }

        if (_data is not null)
        {
            return _data;
        }

        byte[] buffer = new byte[Length];
        ReadResult result = _range!.Read(0, buffer);
        return result.BytesReturned == Length && result.Unreadable.Count == 0 ? buffer : null;
    }

    internal void AddReference()
    {
        if (_references++ == 0)
        {
            _range?.AddReference();
        }
    }

    internal void ReleaseReference()
    {
        if (--_references == 0)
        {
            _range?.ReleaseReference();
        }
    }
}

/// <summary>
/// ユーザークリップボード 1〜9 とクリップボード履歴 (EDIT-28)。システムのクリップボードとは別に持つ。アプリ全体で 1 つ (UI のスレッドから使う)。
/// </summary>
public sealed class UserClipboards : IDisposable
{
    /// <summary>ユーザークリップボードの数 (仕様 1)。</summary>
    public const int SlotCount = 9;

    /// <summary>終了後も残す 1 個あたりの上限 (仕様 6)。</summary>
    public const long PersistLimit = 16L * 1024 * 1024;

    /// <summary>名前の最大の長さ (仕様 3)。</summary>
    public const int MaxNameLength = 50;

    /// <summary>保存先のファイル名 (設定フォルダの中)。</summary>
    public const string IndexFileName = "user-clipboards.json";

    private readonly ClipboardEntry?[] _slots = new ClipboardEntry?[SlotCount];
    private readonly string?[] _names = new string?[SlotCount];
    private readonly List<ClipboardEntry> _history = [];
    private int _historyLimit = 20;

    /// <summary>番号・名前・履歴が変わった。</summary>
    public event EventHandler? Changed;

    /// <summary>番号の内容・名前が変わった (終了後も残す設定のとき、保存し直す)。履歴の変化では出さない。</summary>
    public event EventHandler? SlotsChanged;

    private void RaiseSlotsChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        SlotsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>クリップボード履歴の件数 (設定 <c>clipboard.history.count</c>。0〜100)。</summary>
    public int HistoryLimit
    {
        get => _historyLimit;
        set
        {
            _historyLimit = Math.Clamp(value, 0, 100);
            TrimHistory();
        }
    }

    /// <summary>設定「ユーザークリップボードを終了後も残す」(仕様 6)。</summary>
    public bool Persist { get; set; }

    /// <summary>クリップボード履歴 (新しい順)。</summary>
    public IReadOnlyList<ClipboardEntry> History => _history;

    /// <summary>番号 <paramref name="number"/> (1〜9) の内容。空なら null。</summary>
    public ClipboardEntry? Get(int number) => _slots[Index(number)];

    /// <summary>番号の名前 (なければ null)。</summary>
    public string? NameOf(int number) => _names[Index(number)];

    /// <summary>番号に入れる (「N にコピー」。仕様 2)。前の内容は手放す。</summary>
    public void Set(int number, ClipboardEntry entry)
    {
        int i = Index(number);
        entry.AddReference();
        _slots[i]?.ReleaseReference();
        _slots[i] = entry;
        RaiseSlotsChanged();
    }

    /// <summary>番号を空にする (パネルの「消去」)。</summary>
    public void Clear(int number)
    {
        int i = Index(number);
        if (_slots[i] is { } old)
        {
            _slots[i] = null;
            old.ReleaseReference();
            RaiseSlotsChanged();
        }
    }

    /// <summary>番号に名前を付ける (1〜50 文字。空なら名前を消す)。正しくない名前なら false。</summary>
    public bool SetName(int number, string? name)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (name is { Length: > MaxNameLength })
        {
            return false;
        }

        _names[Index(number)] = name;
        RaiseSlotsChanged();
        return true;
    }

    /// <summary>アプリ内のコピー・切り取りを履歴に加える (仕様 4)。新しいものを先頭に置き、件数を超えた古いものを捨てる。</summary>
    public void AddHistory(ClipboardEntry entry)
    {
        if (_historyLimit == 0)
        {
            return;
        }

        entry.AddReference();
        _history.Insert(0, entry);
        TrimHistory();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>履歴の項目をユーザークリップボードに移す (仕様 4)。</summary>
    public void CopyHistoryToSlot(int historyIndex, int number) => Set(number, _history[historyIndex]);

    /// <summary>終了時に消える項目か (設定がオンで 16 MiB を超える。パネルの「終了時に消えます」。仕様 6)。</summary>
    public bool WillBeLost(int number) => Persist && Get(number) is { Length: > PersistLimit };

    /// <summary>終了後も残す内容を <paramref name="folder"/> に書く (設定がオフなら消す)。16 MiB を超える項目は書かない。</summary>
    public void Save(string folder) => CaptureSave(folder)();

    /// <summary>
    /// 今の番号の内容・名前・設定を写し取り、それを <paramref name="folder"/> に書く処理を返す (UI スレッドで写し、書くのは別のスレッドでよい)。
    /// 書く前に番号が変わって項目の参照が手放された場合、その項目は書けない (次の保存で書き直す)。
    /// </summary>
    public Action CaptureSave(string folder)
    {
        ClipboardEntry?[] entries = [.. _slots];
        string?[] names = [.. _names];
        bool persist = Persist;
        return () => Write(folder, entries, names, persist);
    }

    private static void Write(string folder, ClipboardEntry?[] entries, string?[] names, bool persist)
    {
        string index = Path.Combine(folder, IndexFileName);
        for (int n = 1; n <= SlotCount; n++)
        {
            string file = DataFile(folder, n);
            byte[]? data = persist ? TryReadAll(entries[n - 1]) : null;
            if (data is null)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                continue;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllBytes(file, data);
        }

        if (!persist)
        {
            if (File.Exists(index))
            {
                File.Delete(index);
            }

            return;
        }

        Directory.CreateDirectory(folder);
        using var stream = new FileStream(index, FileMode.Create, FileAccess.Write);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray("slots");
        for (int n = 1; n <= SlotCount; n++)
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", n);
            if (names[n - 1] is { } name)
            {
                writer.WriteString("name", name);
            }

            if (entries[n - 1] is { Length: <= PersistLimit } entry)
            {
                writer.WriteString("source", entry.SourceName);
                writer.WriteNumber("offset", entry.SourceOffset);
                writer.WriteString("time", entry.Time);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>項目の内容を読む (16 MiB 以下)。参照が手放された後なら null。</summary>
    private static byte[]? TryReadAll(ClipboardEntry? entry)
    {
        try
        {
            return entry?.ReadAll(PersistLimit);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>保存した内容を読む (起動時。設定がオンのとき)。読めないものは飛ばす。</summary>
    public void Load(string folder)
    {
        string index = Path.Combine(folder, IndexFileName);
        if (!File.Exists(index))
        {
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(index));
            foreach (JsonElement slot in doc.RootElement.GetProperty("slots").EnumerateArray())
            {
                int n = slot.GetProperty("number").GetInt32();
                if (n is < 1 or > SlotCount)
                {
                    continue;
                }

                _names[n - 1] = slot.TryGetProperty("name", out JsonElement name) ? name.GetString() : null;
                string file = DataFile(folder, n);
                if (slot.TryGetProperty("source", out JsonElement source) && File.Exists(file) && new FileInfo(file).Length <= PersistLimit)
                {
                    DateTimeOffset time = slot.TryGetProperty("time", out JsonElement t) && t.TryGetDateTimeOffset(out DateTimeOffset v) ? v : default;
                    long offset = slot.TryGetProperty("offset", out JsonElement o) ? o.GetInt64() : 0;
                    ClipboardEntry entry = ClipboardEntry.FromBytes(File.ReadAllBytes(file), source.GetString() ?? string.Empty, offset, time);
                    entry.AddReference();
                    _slots[n - 1] = entry;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException)
        {
            // 読めない保存は無視する。
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            _slots[i]?.ReleaseReference();
            _slots[i] = null;
        }

        foreach (ClipboardEntry entry in _history)
        {
            entry.ReleaseReference();
        }

        _history.Clear();
    }

    private void TrimHistory()
    {
        while (_history.Count > _historyLimit)
        {
            _history[^1].ReleaseReference();
            _history.RemoveAt(_history.Count - 1);
        }
    }

    private static string DataFile(string folder, int number) =>
        Path.Combine(folder, $"user-clipboard-{number.ToString(CultureInfo.InvariantCulture)}.bin");

    private static int Index(int number) =>
        number is >= 1 and <= SlotCount ? number - 1 : throw new ArgumentOutOfRangeException(nameof(number));
}
