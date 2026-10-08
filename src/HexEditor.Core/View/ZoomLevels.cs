namespace HexEditor.Core.View;

/// <summary>
/// ズームの倍率の段階 (UI-08 の仕様 1)。Hex 表示のズームと画面全体のズームで共通。倍率は百分率の整数で持つ。
/// </summary>
public static class ZoomLevels
{
    public const int Default = 100;

    /// <summary>倍率の段階。拡大・縮小は 1 回で 1 段階動く。</summary>
    public static IReadOnlyList<int> Steps { get; } = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400];

    public static int Min => Steps[0];

    public static int Max => Steps[^1];

    /// <summary>範囲外・段階にない値 (設定ファイルを手で書いた場合など) を、最も近い段階にする。</summary>
    public static int Normalize(int percent)
    {
        int best = Default;
        int distance = int.MaxValue;
        foreach (int step in Steps)
        {
            int d = Math.Abs(step - percent);
            if (d < distance)
            {
                best = step;
                distance = d;
            }
        }

        return best;
    }

    /// <summary>
    /// <paramref name="current"/> から <paramref name="notches"/> 段階 (正は拡大、負は縮小) 動かした倍率。端では止まる。
    /// 段階にない値からは、その向きの最初の段階へ動く。
    /// </summary>
    public static int Step(int current, int notches)
    {
        int value = current;
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            value = notches > 0
                ? Steps.FirstOrDefault(s => s > value, Max)
                : Steps.LastOrDefault(s => s < value, Min);
        }

        return Math.Clamp(value, Min, Max);
    }

    /// <summary>拡大 (1 段階)。</summary>
    public static int ZoomIn(int current) => Step(current, 1);

    /// <summary>縮小 (1 段階)。</summary>
    public static int ZoomOut(int current) => Step(current, -1);

    /// <summary>倍率 (1.0 が 100%)。</summary>
    public static double Factor(int percent) => percent / 100.0;
}

/// <summary>ズームの設定のキー (UI-08 の仕様 2・3)。</summary>
public static class ZoomSettings
{
    /// <summary>Hex 表示のズームの適用範囲: <see cref="ScopeAll"/> (全タブ共通) / <see cref="ScopeTab"/> (タブごと)。</summary>
    public const string HexScopeKey = "view.zoom.hexScope";

    /// <summary>全タブ共通の Hex 表示の倍率 (百分率)。</summary>
    public const string HexKey = "view.zoom.hex";

    /// <summary>画面全体のズームの倍率 (百分率)。</summary>
    public const string UiKey = "view.zoom.ui";

    public const string ScopeAll = "all";
    public const string ScopeTab = "tab";
}
