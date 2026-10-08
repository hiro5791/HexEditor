namespace HexEditor.Core.View;

/// <summary>画面上の矩形 (物理ピクセル)。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary><paramref name="outer"/> の中にすべて入っているか。<paramref name="slack"/> だけはみ出してもよい。</summary>
    public bool IsInside(PixelRect outer, int slack = 0) =>
        X >= outer.X - slack && Y >= outer.Y - slack && Right <= outer.Right + slack && Bottom <= outer.Bottom + slack;
}

/// <summary>
/// 保存したウィンドウの位置を戻すときの規則 (UI-01 の「エラー」): 位置が今のモニターの範囲外なら、主モニターの中央に既定の大きさ
/// (1280 × 800 px。作業領域に入らない場合は作業領域の 90%) で出す。
/// </summary>
public static class WindowPlacement
{
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 800;

    /// <summary>主モニターの作業領域 <paramref name="workArea"/> の中央に置く既定の位置と大きさ。<paramref name="scale"/> は表示倍率。</summary>
    public static PixelRect Centered(PixelRect workArea, double scale)
    {
        int width = (int)Math.Round(DefaultWidth * scale);
        int height = (int)Math.Round(DefaultHeight * scale);
        if (width > workArea.Width || height > workArea.Height)
        {
            width = (int)(workArea.Width * 0.9);
            height = (int)(workArea.Height * 0.9);
        }

        return new PixelRect(workArea.X + (workArea.Width - width) / 2, workArea.Y + (workArea.Height - height) / 2, width, height);
    }

    /// <summary>
    /// 保存した位置 <paramref name="saved"/> を戻すか。どのモニターの作業領域でもタイトルバーをつかめなければ (タイトルバーの上端が
    /// 作業領域の中になく、横に 100 px 以上重ならない)、主モニターの中央の既定の位置を返す。ウィンドウの一部が画面の端からはみ出して
    /// いるだけなら、そのまま戻す (ウィンドウの見えない枠と最大化の分の 16 px のはみ出しは許す)。
    /// </summary>
    public static PixelRect Restore(PixelRect saved, IReadOnlyList<PixelRect> workAreas, PixelRect primaryWorkArea, double scale)
    {
        int slack = (int)Math.Ceiling(16 * scale);
        int titleBar = (int)Math.Ceiling(32 * scale);
        int grip = Math.Min(saved.Width, (int)Math.Ceiling(100 * scale));
        bool Reachable(PixelRect a) =>
            Math.Min(saved.Right, a.Right) - Math.Max(saved.X, a.X) >= grip
            && saved.Y >= a.Y - slack && saved.Y + titleBar <= a.Bottom;
        return workAreas.Any(Reachable) ? saved : Centered(primaryWorkArea, scale);
    }

    /// <summary>
    /// モニターの記録も使って戻す (UI-31 の仕様 1): 保存したときのモニター <paramref name="savedMonitor"/> (モニターの名前) が今は
    /// ない場合 (外したモニター) は、位置が他のモニターにかかっていても主モニターの中央の既定の位置にする。モニターがある場合と、
    /// 記録がない・名前が分からない場合は、<see cref="Restore(PixelRect, IReadOnlyList{PixelRect}, PixelRect, double)"/> と同じ。
    /// </summary>
    public static PixelRect Restore(
        PixelRect saved, IReadOnlyList<(string? Name, PixelRect WorkArea)> monitors, string? savedMonitor, PixelRect primaryWorkArea, double scale)
    {
        bool known = monitors.Any(m => m.Name is not null);
        if (savedMonitor is { Length: > 0 } && known
            && !monitors.Any(m => string.Equals(m.Name, savedMonitor, StringComparison.OrdinalIgnoreCase)))
        {
            return Centered(primaryWorkArea, scale);
        }

        return Restore(saved, [.. monitors.Select(m => m.WorkArea)], primaryWorkArea, scale);
    }

    /// <summary>
    /// セッションに書く「元の大きさ」(UI-31 の仕様 1): 通常の表示なら今の位置と大きさ。最大化・全画面・最小化の間は、その前の通常の表示の
    /// 位置と大きさ <paramref name="normal"/> (記録がなければ今の値)。最大化・全画面のまま書くと、戻したときに通常の大きさが失われるため。
    /// </summary>
    public static PixelRect NormalBounds(PixelRect current, PixelRect? normal, bool isNormalState) =>
        isNormalState || normal is not { Width: > 0, Height: > 0 } n ? current : n;
}
