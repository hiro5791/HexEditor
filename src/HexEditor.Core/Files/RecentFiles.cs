using System.Text.Json;

namespace HexEditor.Core.Files;

/// <summary>最近使ったファイルの項目の種類 (ENG-16 の仕様 1)。プロセスメモリは記録しない。</summary>
public enum RecentItemKind
{
    File,
    Disk,
    DiskImage,
    Sftp,
}

/// <summary>開くときのオプション (ENG-16 の仕様 2): 範囲 (ENG-13)、セクタサイズ (ENG-31)、形式 (ENG-38)。</summary>
public sealed record RecentOpenOptions
{
    public long? RangeStart { get; init; }

    public long? RangeLength { get; init; }

    public int? SectorSize { get; init; }

    public string? Format { get; init; }
}

/// <summary>最近使ったファイルの 1 項目 (ENG-16 の仕様 2)。</summary>
public sealed record RecentItem
{
    public RecentItemKind Kind { get; init; } = RecentItemKind.File;

    /// <summary>パス (デバイスの識別子、<c>sftp://</c> の URI)。ポータブル版では exe からの相対パスを解決した絶対パス。</summary>
    public required string Path { get; init; }

    /// <summary>表示名 (ファイル名、<c>PhysicalDrive1 (Samsung SSD 990, 2 TB)</c> など)。</summary>
    public required string DisplayName { get; init; }

    public DateTime LastOpenedUtc { get; init; }

    public bool Pinned { get; init; }

    /// <summary>ピン留めした順 (小さいほど先)。</summary>
    public int PinOrder { get; init; }

    public RecentOpenOptions? Options { get; init; }

    /// <summary>ネットワーク上のパスか (存在を確かめない。UI-32 の仕様 6)。</summary>
    public bool IsNetworkPath => RecentFileList.IsNetworkPath(Path);
}

/// <summary>「一覧を消去」の前の状態 (InfoBar の「元に戻す」で戻す。UI-32 の仕様 5)。</summary>
public sealed record RecentClearUndo(IReadOnlyList<RecentItem> Items);

/// <summary>
/// 最近使ったファイルの一覧 (ENG-16、UI-32)。並びはピン留めした項目 (ピン留めした順)、その後に最後に開いた日時の新しい順。
/// ピン留めしていない項目は <see cref="MaxItems"/> 件まで (ピン留めは数えない)。<see cref="MaxItems"/> が 0 なら記録しない。
/// ジャンプリスト (UI-35) は <see cref="Items"/> と <see cref="Changed"/> を使う。スレッドセーフ。
/// </summary>
public sealed class RecentFileList
{
    public const int DefaultMaxItems = 25;
    public const int MaxItemsLimit = 100;

    /// <summary>サブメニュー・スタートページに出す最近の件数 (ピン留めは別。UI-32 の仕様 3、UI-38 の仕様 1)。</summary>
    public const int MenuItems = 10;

    private readonly object _lock = new();
    private List<RecentItem> _items = [];
    private int _maxItems = DefaultMaxItems;

    /// <summary>一覧が変わった (記録・ピン留め・削除・消去・読み込み)。どのスレッドからも呼ばれる。</summary>
    public event EventHandler? Changed;

    /// <summary>記録する件数 (0〜100)。0 にすると記録をやめ、ピン留めを含めて一覧を消す (ENG-16 の仕様 7)。</summary>
    public int MaxItems
    {
        get
        {
            lock (_lock)
            {
                return _maxItems;
            }
        }

        set
        {
            int clamped = Math.Clamp(value, 0, MaxItemsLimit);
            lock (_lock)
            {
                if (clamped == _maxItems)
                {
                    return;
                }

                _maxItems = clamped;
                _items = clamped == 0 ? [] : Normalize(_items, clamped);
            }

            OnChanged();
        }
    }

    /// <summary>並べた一覧 (ピン留め → 新しい順)。</summary>
    public IReadOnlyList<RecentItem> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _items];
            }
        }
    }

    /// <summary>ピン留めした項目。</summary>
    public IReadOnlyList<RecentItem> Pinned => [.. Items.Where(i => i.Pinned)];

    /// <summary>ピン留めしていない項目 (新しい順)。</summary>
    public IReadOnlyList<RecentItem> Recent => [.. Items.Where(i => !i.Pinned)];

    /// <summary>
    /// 開いた・閉じたことを記録する: 一覧の先頭 (ピン留めの後) に移す (UI-32 の仕様 1)。ピン留めした項目はその位置のまま日時だけ更新する。
    /// <see cref="MaxItems"/> が 0 なら何もしない。
    /// </summary>
    public void Record(string path, string displayName, DateTime utc, RecentItemKind kind = RecentItemKind.File, RecentOpenOptions? options = null)
    {
        lock (_lock)
        {
            if (_maxItems == 0)
            {
                return;
            }

            int index = IndexOf(path);
            RecentItem? existing = index >= 0 ? _items[index] : null;
            if (index >= 0)
            {
                _items.RemoveAt(index);
            }

            _items.Add(new RecentItem
            {
                Kind = kind,
                Path = existing?.Path ?? path,
                DisplayName = displayName,
                LastOpenedUtc = utc,
                Pinned = existing?.Pinned ?? false,
                PinOrder = existing?.PinOrder ?? 0,
                Options = options ?? existing?.Options,
            });
            _items = Normalize(_items, _maxItems);
        }

        OnChanged();
    }

    /// <summary>項目を探す (パスの大文字・小文字は区別しない)。</summary>
    public RecentItem? Find(string path)
    {
        lock (_lock)
        {
            int index = IndexOf(path);
            return index >= 0 ? _items[index] : null;
        }
    }

    /// <summary>ピン留めする (ピン留めした順の最後)。項目がなければ false。</summary>
    public bool Pin(string path) => Update(path, item => item.Pinned ? item : item with
    {
        Pinned = true,
        PinOrder = _items.Where(i => i.Pinned).Select(i => i.PinOrder).DefaultIfEmpty(-1).Max() + 1,
    });

    /// <summary>ピン留めを外す。</summary>
    public bool Unpin(string path) => Update(path, item => item with { Pinned = false, PinOrder = 0 });

    /// <summary>一覧から削除する。</summary>
    public bool Remove(string path)
    {
        lock (_lock)
        {
            int index = IndexOf(path);
            if (index < 0)
            {
                return false;
            }

            _items.RemoveAt(index);
        }

        OnChanged();
        return true;
    }

    /// <summary>「一覧を消去」: ピン留めしていない項目だけを消す (UI-32 の仕様 5)。戻り値で元に戻せる。</summary>
    public RecentClearUndo ClearUnpinned()
    {
        RecentClearUndo undo;
        lock (_lock)
        {
            undo = new RecentClearUndo([.. _items]);
            _items = [.. _items.Where(i => i.Pinned)];
        }

        OnChanged();
        return undo;
    }

    /// <summary>消去を元に戻す (消去の後に記録した項目は残す)。</summary>
    public void Undo(RecentClearUndo undo)
    {
        lock (_lock)
        {
            var merged = new List<RecentItem>(undo.Items);
            foreach (RecentItem item in _items)
            {
                int at = merged.FindIndex(i => SamePath(i.Path, item.Path));
                if (at >= 0)
                {
                    merged[at] = item;
                }
                else
                {
                    merged.Add(item);
                }
            }

            _items = _maxItems == 0 ? [] : Normalize(merged, _maxItems);
        }

        OnChanged();
    }

    /// <summary>サブメニューに出す項目: ピン留めした項目と、最近の <see cref="MenuItems"/> 件 (UI-32 の仕様 3)。</summary>
    public IReadOnlyList<RecentItem> MenuEntries()
    {
        IReadOnlyList<RecentItem> items = Items;
        return [.. items.Where(i => i.Pinned), .. items.Where(i => !i.Pinned).Take(MenuItems)];
    }

    /// <summary>ネットワーク上のパス (UNC、ネットワークドライブ)。存在を確かめない (応答しないことがあるため。ENG-16、UI-32 の仕様 6)。</summary>
    public static bool IsNetworkPath(string path)
    {
        if (IsNetworkPathSyntax(path))
        {
            return true;
        }

        try
        {
            string? root = System.IO.Path.GetPathRoot(path);
            return root is { Length: >= 2 } && root[1] == ':' && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>書き方だけで分かるネットワークのパス (UNC、URI)。ドライブの種類は調べない。</summary>
    public static bool IsNetworkPathSyntax(string path) =>
        (path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
            && !path.StartsWith(@"\\.\", StringComparison.Ordinal))
        || path.Contains("://", StringComparison.Ordinal);

    // ---- 保存 (recent.json。ENG-16 の仕様 6) ----

    /// <summary>JSON から読む。<paramref name="paths"/> は相対パスの解決 (ポータブル版。UI-32 の仕様 8)。</summary>
    public void Load(string json, RecentPathMapper? paths = null)
    {
        RecentFileData data = JsonSerializer.Deserialize<RecentFileData>(json, JsonFile.Options) ?? new RecentFileData();
        lock (_lock)
        {
            IEnumerable<RecentItem> items = data.Items.Where(i => i.Path.Length > 0)
                .Select(i => paths is null ? i : i with { Path = paths.ToFull(i.Path) });
            _items = _maxItems == 0 ? [] : Normalize([.. items], _maxItems);
        }

        OnChanged();
    }

    /// <summary>JSON にする。<paramref name="paths"/> が exe と同じドライブのパスを相対パスにする (ポータブル版)。</summary>
    public string ToJson(RecentPathMapper? paths = null)
    {
        IReadOnlyList<RecentItem> items = Items;
        var data = new RecentFileData
        {
            Items = [.. items.Select(i => paths is null ? i : i with { Path = paths.ToStored(i.Path) })],
        };
        return JsonFile.Serialize(data);
    }

    private bool Update(string path, Func<RecentItem, RecentItem> change)
    {
        lock (_lock)
        {
            int index = IndexOf(path);
            if (index < 0)
            {
                return false;
            }

            _items[index] = change(_items[index]);
            _items = Normalize(_items, _maxItems);
        }

        OnChanged();
        return true;
    }

    private int IndexOf(string path) => _items.FindIndex(i => SamePath(i.Path, path));

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>並べ替えて、ピン留めしていない項目を件数で切る。同じパスは新しいものだけ残す。</summary>
    private static List<RecentItem> Normalize(IEnumerable<RecentItem> items, int maxItems)
    {
        var unique = items.GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(i => i.Pinned).ThenByDescending(i => i.LastOpenedUtc).First())
            .ToList();
        return
        [
            .. unique.Where(i => i.Pinned).OrderBy(i => i.PinOrder).ThenByDescending(i => i.LastOpenedUtc),
            .. unique.Where(i => !i.Pinned).OrderByDescending(i => i.LastOpenedUtc).Take(maxItems),
        ];
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>recent.json の形式。</summary>
    private sealed record RecentFileData
    {
        public int Version { get; init; } = 1;

        public List<RecentItem> Items { get; init; } = [];
    }
}

/// <summary>
/// ポータブル版のパスの記録 (UI-32 の仕様 8): exe と同じドライブにあるファイルを exe のフォルダからの相対パスで記録し、USB メモリの
/// ドライブ文字が変わっても開けるようにする。<see cref="BaseFolder"/> が null なら絶対パスのまま。
/// </summary>
public sealed class RecentPathMapper(string? baseFolder)
{
    public string? BaseFolder { get; } = baseFolder;

    /// <summary>記録する形 (同じドライブなら相対パス)。</summary>
    public string ToStored(string path)
    {
        if (BaseFolder is null || !System.IO.Path.IsPathFullyQualified(path) || RecentFileList.IsNetworkPathSyntax(path))
        {
            return path;
        }

        string? root = System.IO.Path.GetPathRoot(path);
        string? baseRoot = System.IO.Path.GetPathRoot(BaseFolder);
        return root is not null && string.Equals(root, baseRoot, StringComparison.OrdinalIgnoreCase)
            ? System.IO.Path.GetRelativePath(BaseFolder, path)
            : path;
    }

    /// <summary>記録した形から絶対パスに戻す。</summary>
    public string ToFull(string stored) =>
        BaseFolder is not null && !System.IO.Path.IsPathRooted(stored) && !stored.Contains("://", StringComparison.Ordinal)
            ? System.IO.Path.GetFullPath(System.IO.Path.Combine(BaseFolder, stored))
            : stored;
}

/// <summary>recent.json の読み書き (設定フォルダ)。書けない場合は一覧を更新せず、理由を返す (利用者には出さない。ENG-16 の「エラー」)。</summary>
public sealed class RecentFileStore(string folder, RecentPathMapper? paths = null)
{
    public const string FileName = "recent.json";

    public string PathName => System.IO.Path.Combine(folder, FileName);

    public RecentPathMapper? Paths { get; } = paths;

    /// <summary>読む。ファイルがない・壊れている場合は空の一覧のまま (壊れたファイルは残す)。</summary>
    public void Load(RecentFileList list)
    {
        try
        {
            if (File.Exists(PathName))
            {
                list.Load(File.ReadAllText(PathName), Paths);
            }
        }
        catch (JsonException)
        {
            JsonFile.KeepBroken(PathName, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>書く。書けなければ例外の内容を返す (呼び出し側がログに記録する)。</summary>
    public string? Save(RecentFileList list)
    {
        try
        {
            JsonFile.WriteText(PathName, list.ToJson(Paths));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
