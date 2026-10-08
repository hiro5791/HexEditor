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
}
