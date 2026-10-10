namespace HexEditor.Core.Processes;

/// <summary>メモリマップの「領域」の 1 行 (ENG-33 の仕様 1)。<see cref="Offset"/> はドキュメント上の位置。</summary>
public sealed record MemoryMapRow(long Offset, long Start, long Size, RegionState State, uint Protect, RegionType Type, string? Name)
{
    public long End => Start + Size;

    /// <summary>保護属性の短い表記 (R、RW、RX、RWX、WC、NA、+G)。コミットされていない領域は空。</summary>
    public string ProtectText => State == RegionState.Commit ? PageProtection.ShortText(Protect) : string.Empty;

    /// <summary>読める領域 (「次のメモリ領域」の移動先・選択できる範囲)。</summary>
    public bool IsReadable => State == RegionState.Commit && PageProtection.IsReadable(Protect);
}

/// <summary>メモリマップの「領域」の並べ替えの列 (ENG-33 の仕様 2)。</summary>
public enum MemoryMapSort
{
    Start,
    Size,
    State,
    Protect,
    Type,
    Name,
}

/// <summary>
/// メモリマップのパネルの一覧の組み立て (ENG-33 の仕様 1・2・4): 領域の行、並べ替え、絞り込み (名前・保護属性・状態)、
/// 「空き」の領域を隠す切り替え、カーソルのある領域の行。UI に依存しない。
/// </summary>
public static class MemoryMapModel
{
    /// <summary>
    /// プロセスの領域の一覧 (アドレスの昇順) から行を作る。モジュールに含まれる領域にはモジュール名、マップしたファイルにはファイル名を付ける。
    /// <paramref name="rangeStart"/>〜+<paramref name="length"/> の外の領域は含めず、端は切り詰める (モジュール・範囲を開いた場合。ENG-32 の仕様 6)。
    /// </summary>
    public static List<MemoryMapRow> Build(IReadOnlyList<MemoryRegion> regions, IReadOnlyList<ProcessModule> modules, long rangeStart, long length)
    {
        long end = rangeStart + length;
        var rows = new List<MemoryMapRow>(regions.Count);
        foreach (MemoryRegion region in regions)
        {
            long from = Math.Max(region.BaseAddress, rangeStart);
            long to = Math.Min(region.End, end);
            if (to <= from)
            {
                continue;
            }

            string? name = modules.FirstOrDefault(m => region.BaseAddress >= m.BaseAddress && region.BaseAddress < m.End)?.Name
                ?? (region.MappedName is { } mapped ? Path.GetFileName(mapped) : null);
            rows.Add(new MemoryMapRow(from - rangeStart, from, to - from, region.State, region.Protect, region.Type, name));
        }

        return rows;
    }

    /// <summary>スナップショット (ENG-35) の領域から行を作る (記録した領域だけ。どれもコミット済み)。</summary>
    public static List<MemoryMapRow> Build(IReadOnlyList<SnapshotRegion> regions, long baseAddress) =>
        [.. regions.OrderBy(r => r.BaseAddress)
            .Select(r => new MemoryMapRow(r.BaseAddress - baseAddress, r.BaseAddress, r.Size, RegionState.Commit, r.Protect, r.Type, r.ModuleName))];

    /// <summary>
    /// 絞り込みと並べ替え (ENG-33 の仕様 2)。<paramref name="filter"/> は名前・保護属性 (<c>RX</c> など)・状態 (<c>commit</c> など、
    /// <paramref name="stateName"/> で表示名も) の部分一致 (大文字小文字を区別しない)。<paramref name="showFree"/> が偽なら「空き」を隠す。
    /// </summary>
    public static List<MemoryMapRow> Filter(IEnumerable<MemoryMapRow> rows, string? filter, bool showFree, MemoryMapSort sort = MemoryMapSort.Start,
        bool descending = false, Func<RegionState, string>? stateName = null)
    {
        IEnumerable<MemoryMapRow> result = rows;
        if (!showFree)
        {
            result = result.Where(r => r.State != RegionState.Free);
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            string f = filter.Trim();
            result = result.Where(r =>
                (r.Name?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (r.ProtectText.Length > 0 && r.ProtectText.Equals(f, StringComparison.OrdinalIgnoreCase))
                || r.State.ToString().Contains(f, StringComparison.OrdinalIgnoreCase)
                || (stateName?.Invoke(r.State).Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || r.Type.ToString().Contains(f, StringComparison.OrdinalIgnoreCase));
        }

        Func<MemoryMapRow, object> key = sort switch
        {
            MemoryMapSort.Size => r => r.Size,
            MemoryMapSort.State => r => (int)r.State,
            MemoryMapSort.Protect => r => r.ProtectText,
            MemoryMapSort.Type => r => (int)r.Type,
            MemoryMapSort.Name => r => r.Name ?? string.Empty,
            _ => r => r.Start,
        };
        IOrderedEnumerable<MemoryMapRow> ordered = descending
            ? result.OrderByDescending(key, Comparer<object>.Create(Compare))
            : result.OrderBy(key, Comparer<object>.Create(Compare));
        return [.. ordered.ThenBy(r => r.Start)];
    }

    private static int Compare(object? a, object? b) => (a, b) switch
    {
        (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
        (IComparable x, _) => x.CompareTo(b),
        _ => 0,
    };

    /// <summary>ドキュメント上の位置を含む行の番号 (カーソルのある領域の強調。ENG-33 の仕様 4)。なければ -1。</summary>
    public static int IndexOfOffset(IReadOnlyList<MemoryMapRow> rows, long offset)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (offset >= rows[i].Offset && offset < rows[i].Offset + rows[i].Size)
            {
                return i;
            }
        }

        return -1;
    }
}
