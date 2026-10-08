using System.Globalization;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Panels;

/// <summary>パネルを置く場所 (UI-05 の仕様 1、3)。</summary>
public enum PanelDock
{
    Left,
    Right,
    Bottom,

    /// <summary>メインウィンドウから切り離した浮動パネル (子ウィンドウ)。</summary>
    Floating,
}

/// <summary>画面上の長方形 (浮動パネルの位置。物理ピクセル)。</summary>
public readonly record struct PanelBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Intersects(PanelBounds other) => X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

/// <summary>パネル 1 つの配置。</summary>
public sealed class PanelPlacement
{
    public required string Id { get; init; }

    public PanelDock Dock { get; set; }

    public bool Visible { get; set; }

    /// <summary>同じ場所の中での並び (タブの順)。</summary>
    public int Order { get; set; }

    /// <summary>浮動にする前の場所 (見出しのダブルクリックで戻す先。UI-05 の仕様 5)。</summary>
    public PanelDock DockedAt { get; set; }

    public PanelBounds? FloatingBounds { get; set; }
}

/// <summary>
/// パネルの配置 (UI-05): 各パネルの場所・表示状態・並び、左・右・下の領域の大きさ、各領域で選んでいるタブ。
/// ウィンドウごとに持ち、セッション (UI-31) に保存する。新しいウィンドウは最後にアクティブだったウィンドウの配置を引き継ぐ (仕様 7)。
/// </summary>
public sealed class PanelLayout
{
    /// <summary>パネルの幅・高さの最小値 (UI-01 の仕様 5)。</summary>
    public const double MinSize = 160;

    public const double DefaultSideWidth = 300;
    public const double DefaultBottomHeight = 220;

    private readonly Dictionary<string, PanelPlacement> _panels = new(StringComparer.Ordinal);

    public double LeftWidth { get; set; } = DefaultSideWidth;

    public double RightWidth { get; set; } = DefaultSideWidth;

    public double BottomHeight { get; set; } = DefaultBottomHeight;

    /// <summary>表示 &gt; パネルの表示切り替え で隠した領域 (中のパネルの表示状態は保つ。UI-01 の仕様 6)。</summary>
    public HashSet<PanelDock> HiddenDocks { get; } = [];

    /// <summary>領域に表示するパネルがあり、隠していないか。</summary>
    public bool IsDockShown(PanelDock dock) => !HiddenDocks.Contains(dock) && VisibleIn(dock).Count > 0;

    /// <summary>各領域で選んでいるパネル。</summary>
    public Dictionary<PanelDock, string> ActiveTab { get; } = [];

    public IEnumerable<PanelPlacement> Panels => _panels.Values;

    /// <summary>パネルの配置。知らないパネルは既定の場所・非表示で作る。</summary>
    public PanelPlacement Get(string id, PanelDock defaultDock)
    {
        if (!_panels.TryGetValue(id, out PanelPlacement? p))
        {
            p = new PanelPlacement { Id = id, Dock = defaultDock, DockedAt = defaultDock, Order = NextOrder(defaultDock) };
            _panels[id] = p;
        }

        return p;
    }

    public PanelPlacement? Find(string id) => _panels.GetValueOrDefault(id);

    /// <summary>場所の中の表示中のパネル (タブの順)。</summary>
    public IReadOnlyList<PanelPlacement> VisibleIn(PanelDock dock) =>
        _panels.Values.Where(p => p.Visible && p.Dock == dock).OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

    public void Show(string id, PanelDock defaultDock)
    {
        PanelPlacement p = Get(id, defaultDock);
        p.Visible = true;
        ActiveTab[p.Dock] = id;
        HiddenDocks.Remove(p.Dock);
    }

    public void Hide(string id)
    {
        if (_panels.TryGetValue(id, out PanelPlacement? p))
        {
            p.Visible = false;
            FixActive(p.Dock);
        }
    }

    /// <summary>別の場所に移す (UI-05 の仕様 3、4)。移した先でそのパネルを選ぶ。</summary>
    public void Move(string id, PanelDock dock, PanelDock defaultDock)
    {
        PanelPlacement p = Get(id, defaultDock);
        PanelDock from = p.Dock;
        if (dock == PanelDock.Floating && from != PanelDock.Floating)
        {
            p.DockedAt = from;
        }

        p.Dock = dock;
        p.Visible = true;
        p.Order = NextOrder(dock);
        ActiveTab[dock] = id;
        HiddenDocks.Remove(dock);
        FixActive(from);
    }

    /// <summary>浮動パネルを元の場所に戻す (UI-05 の仕様 5)。</summary>
    public void Redock(string id)
    {
        if (_panels.TryGetValue(id, out PanelPlacement? p) && p.Dock == PanelDock.Floating)
        {
            Move(id, p.DockedAt == PanelDock.Floating ? PanelDock.Right : p.DockedAt, p.DockedAt);
        }
    }

    /// <summary>既定の配置に戻す (UI-05 の仕様 8)。表示状態は保ち、場所と大きさを既定にする。</summary>
    public void ResetToDefault(IReadOnlyDictionary<string, PanelDock> defaults)
    {
        foreach (PanelPlacement p in _panels.Values)
        {
            PanelDock dock = defaults.TryGetValue(p.Id, out PanelDock d) ? d : PanelDock.Right;
            p.Dock = dock;
            p.DockedAt = dock;
            p.FloatingBounds = null;
        }

        int order = 0;
        foreach (PanelPlacement p in _panels.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            p.Order = order++;
        }

        LeftWidth = RightWidth = DefaultSideWidth;
        BottomHeight = DefaultBottomHeight;
        HiddenDocks.Clear();
        ActiveTab.Clear();
        foreach (PanelDock dock in new[] { PanelDock.Left, PanelDock.Right, PanelDock.Bottom })
        {
            FixActive(dock);
        }
    }

    /// <summary>選んでいるタブを、その場所にある表示中のパネルに直す。</summary>
    private void FixActive(PanelDock dock)
    {
        var visible = VisibleIn(dock);
        if (visible.Count == 0)
        {
            ActiveTab.Remove(dock);
        }
        else if (!ActiveTab.TryGetValue(dock, out string? active) || visible.All(p => p.Id != active))
        {
            ActiveTab[dock] = visible[0].Id;
        }
    }

    private int NextOrder(PanelDock dock) => _panels.Values.Where(p => p.Dock == dock).Select(p => p.Order + 1).DefaultIfEmpty(0).Max();

    /// <summary>
    /// 浮動パネルの保存位置が画面外なら、メインウィンドウの右上に置く (UI-05 の「エラー」)。
    /// </summary>
    public static PanelBounds EnsureOnScreen(PanelBounds bounds, IReadOnlyList<PanelBounds> workAreas, PanelBounds mainWindow)
    {
        if (workAreas.Any(a => a.Intersects(bounds) && bounds.Y >= a.Y && bounds.Y < a.Bottom - 16))
        {
            return bounds;
        }

        return new PanelBounds(mainWindow.Right - bounds.Width - 16, mainWindow.Y + 64, bounds.Width, bounds.Height);
    }

    /// <summary>大きさを最小値と、ウィンドウに入る大きさに収める。</summary>
    public static double Clamp(double size, double available) => Math.Max(MinSize, Math.Min(size, Math.Max(MinSize, available)));

    // ---- 保存 ----

    public JsonObject ToJson()
    {
        var panels = new JsonObject();
        foreach (PanelPlacement p in _panels.Values.OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var item = new JsonObject
            {
                ["dock"] = DockName(p.Dock),
                ["visible"] = p.Visible,
                ["order"] = p.Order,
            };
            if (p.Dock == PanelDock.Floating)
            {
                item["dockedAt"] = DockName(p.DockedAt);
            }

            if (p.FloatingBounds is { } b)
            {
                item["bounds"] = new JsonArray(b.X, b.Y, b.Width, b.Height);
            }

            panels[p.Id] = item;
        }

        var active = new JsonObject();
        foreach ((PanelDock dock, string id) in ActiveTab.OrderBy(p => p.Key))
        {
            active[DockName(dock)] = id;
        }

        return new JsonObject
        {
            ["leftWidth"] = LeftWidth,
            ["rightWidth"] = RightWidth,
            ["bottomHeight"] = BottomHeight,
            ["panels"] = panels,
            ["active"] = active,
            ["hiddenDocks"] = new JsonArray([.. HiddenDocks.Order().Select(d => (JsonNode?)DockName(d))]),
        };
    }

    /// <summary>保存した配置を読む。読めない値は既定にする。</summary>
    public static PanelLayout FromJson(JsonNode? node)
    {
        var layout = new PanelLayout();
        if (node is not JsonObject root)
        {
            return layout;
        }

        layout.LeftWidth = Size(root["leftWidth"], DefaultSideWidth);
        layout.RightWidth = Size(root["rightWidth"], DefaultSideWidth);
        layout.BottomHeight = Size(root["bottomHeight"], DefaultBottomHeight);
        if (root["panels"] is JsonObject panels)
        {
            foreach ((string id, JsonNode? value) in panels)
            {
                if (value is not JsonObject item || !TryDock(item["dock"], out PanelDock dock))
                {
                    continue;
                }

                var p = new PanelPlacement
                {
                    Id = id,
                    Dock = dock,
                    DockedAt = TryDock(item["dockedAt"], out PanelDock at) ? at : dock,
                    Visible = item["visible"] is JsonValue v && v.TryGetValue(out bool visible) && visible,
                    Order = item["order"] is JsonValue o && o.TryGetValue(out int order) ? order : 0,
                };
                if (item["bounds"] is JsonArray { Count: 4 } b && b.All(n => n is JsonValue))
                {
                    p.FloatingBounds = new PanelBounds(b[0]!.GetValue<int>(), b[1]!.GetValue<int>(), b[2]!.GetValue<int>(), b[3]!.GetValue<int>());
                }

                layout._panels[id] = p;
            }
        }

        if (root["active"] is JsonObject active)
        {
            foreach ((string dockName, JsonNode? id) in active)
            {
                if (Enum.TryParse(dockName, ignoreCase: true, out PanelDock dock) && id is JsonValue v && v.TryGetValue(out string? s))
                {
                    layout.ActiveTab[dock] = s;
                }
            }
        }

        if (root["hiddenDocks"] is JsonArray hidden)
        {
            foreach (JsonNode? d in hidden)
            {
                if (TryDock(d, out PanelDock dock))
                {
                    layout.HiddenDocks.Add(dock);
                }
            }
        }

        return layout;
    }

    public PanelLayout Clone() => FromJson(ToJson());

    public static string DockName(PanelDock dock) => dock.ToString().ToLower(CultureInfo.InvariantCulture);

    private static bool TryDock(JsonNode? node, out PanelDock dock)
    {
        dock = PanelDock.Right;
        return node is JsonValue v && v.TryGetValue(out string? s) && Enum.TryParse(s, ignoreCase: true, out dock);
    }

    private static double Size(JsonNode? node, double fallback) =>
        Settings.SettingDefinition.TryGetNumber(node, out double d) && double.IsFinite(d) ? Math.Max(MinSize, d) : fallback;
}
