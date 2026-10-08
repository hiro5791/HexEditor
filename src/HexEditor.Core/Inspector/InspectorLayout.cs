using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Inspector;

/// <summary>行の設定 1 つ: 型、表示するか、反対のエンディアンの行も加えるか (INSP-02 の仕様 7)。</summary>
public sealed record InspectorRowConfig(string TypeId, bool Visible, bool Opposite = false);

/// <summary>組み込みのプリセット (INSP-19 の仕様 4)。</summary>
public enum InspectorPreset
{
    /// <summary>「基本」(既定。INSP-01 の仕様 6 の行)。</summary>
    Basic,
    All,
    DateTime,

    /// <summary>「組み込み向け」(整数、2 進。固定小数点と half はフェーズ 2)。</summary>
    Embedded,
}

/// <summary>
/// 行の構成 (INSP-19): グループの順序、グループの中の行の順序、行ごとの表示・非表示とオプション。アプリ全体で共通
/// (仕様 6)。変更は新しいインスタンスを返す。
/// </summary>
public sealed class InspectorLayout : IEquatable<InspectorLayout>
{
    private InspectorLayout(IReadOnlyList<InspectorGroup> groups, IReadOnlyList<InspectorRowConfig> rows)
    {
        Groups = groups;
        Rows = rows;
    }

    /// <summary>グループの順序。</summary>
    public IReadOnlyList<InspectorGroup> Groups { get; }

    /// <summary>すべての行 (非表示の行を含む)。同じグループの中の順序が表示の順序。</summary>
    public IReadOnlyList<InspectorRowConfig> Rows { get; }

    /// <summary>既定のグループの順序 (行のある型のグループだけ)。</summary>
    private static IReadOnlyList<InspectorGroup> DefaultGroups { get; } =
        [.. Enum.GetValues<InspectorGroup>().Where(g => InspectorTypes.All.Any(t => t.Group == g))];

    public static InspectorLayout Default => FromPreset(InspectorPreset.Basic);

    public static InspectorLayout FromPreset(InspectorPreset preset)
    {
        bool Shown(InspectorType t) => preset switch
        {
            InspectorPreset.All => true,
            InspectorPreset.DateTime => t.Group == InspectorGroup.DateTime,
            InspectorPreset.Embedded => t.Group == InspectorGroup.Integer || InspectorTypes.IsBinary(t.Id),
            _ => InspectorTypes.Basic.Contains(t.Id),
        };
        return new InspectorLayout(DefaultGroups, [.. InspectorTypes.All.Select(t => new InspectorRowConfig(t.Id, Shown(t)))]);
    }

    public InspectorRowConfig? Row(string typeId) => Rows.FirstOrDefault(r => r.TypeId == typeId);

    /// <summary>グループの中の行 (表示の順序。非表示の行を含む)。</summary>
    public IReadOnlyList<InspectorRowConfig> RowsIn(InspectorGroup group) =>
        [.. Rows.Where(r => InspectorTypes.Get(r.TypeId).Group == group)];

    /// <summary>表示する行をグループの順に並べたもの。</summary>
    public IEnumerable<(InspectorGroup Group, IReadOnlyList<InspectorRowConfig> Rows)> VisibleGroups()
    {
        foreach (InspectorGroup group in Groups)
        {
            var rows = RowsIn(group).Where(r => r.Visible).ToList();
            if (rows.Count > 0)
            {
                yield return (group, rows);
            }
        }
    }

    public InspectorLayout WithVisible(string typeId, bool visible) => Replace(typeId, r => r with { Visible = visible });

    public InspectorLayout WithOpposite(string typeId, bool opposite) => Replace(typeId, r => r with { Opposite = opposite });

    /// <summary>行をグループの中で <paramref name="delta"/> だけ動かす (負なら上へ)。端を越える分は無視する。</summary>
    public InspectorLayout MoveRow(string typeId, int delta)
    {
        InspectorGroup group = InspectorTypes.Get(typeId).Group;
        var inGroup = RowsIn(group).ToList();
        int index = inGroup.FindIndex(r => r.TypeId == typeId);
        if (index < 0)
        {
            return this;
        }

        int target = Math.Clamp(index + delta, 0, inGroup.Count - 1);
        if (target == index)
        {
            return this;
        }

        InspectorRowConfig row = inGroup[index];
        inGroup.RemoveAt(index);
        inGroup.Insert(target, row);

        // グループの行を元の位置 (グループごとのまとまり) に書き戻す。
        var rows = new List<InspectorRowConfig>(Rows.Count);
        int next = 0;
        foreach (InspectorRowConfig r in Rows)
        {
            rows.Add(InspectorTypes.Get(r.TypeId).Group == group ? inGroup[next++] : r);
        }

        return new InspectorLayout(Groups, rows);
    }

    /// <summary>グループを <paramref name="delta"/> だけ動かす (負なら上へ)。</summary>
    public InspectorLayout MoveGroup(InspectorGroup group, int delta)
    {
        var groups = Groups.ToList();
        int index = groups.IndexOf(group);
        if (index < 0)
        {
            return this;
        }

        int target = Math.Clamp(index + delta, 0, groups.Count - 1);
        groups.RemoveAt(index);
        groups.Insert(target, group);
        return new InspectorLayout(groups, Rows);
    }

    private InspectorLayout Replace(string typeId, Func<InspectorRowConfig, InspectorRowConfig> change) =>
        new(Groups, [.. Rows.Select(r => r.TypeId == typeId ? change(r) : r)]);

    // ---- 保存 (設定ファイルの文字列) ----

    public string Serialize()
    {
        var root = new JsonObject
        {
            ["groups"] = new JsonArray([.. Groups.Select(g => (JsonNode?)JsonValue.Create(g.ToString()))]),
            ["rows"] = new JsonArray([.. Rows.Select(r =>
            {
                var o = new JsonObject { ["id"] = r.TypeId, ["visible"] = r.Visible };
                if (r.Opposite)
                {
                    o["opposite"] = true;
                }

                return (JsonNode?)o;
            })]),
        };
        return root.ToJsonString();
    }

    /// <summary>
    /// 設定の文字列を読む。知らない型は捨て、記録にない型 (新しい版で増えたもの) は既定の表示で末尾に加える。
    /// 読めなければ既定の構成。
    /// </summary>
    public static InspectorLayout Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Default;
        }

        try
        {
            JsonObject root = JsonNode.Parse(text)?.AsObject() ?? throw new JsonException();
            var groups = new List<InspectorGroup>();
            foreach (JsonNode? node in root["groups"]?.AsArray() ?? [])
            {
                if (Enum.TryParse(node?.GetValue<string>(), ignoreCase: true, out InspectorGroup g) && DefaultGroups.Contains(g) && !groups.Contains(g))
                {
                    groups.Add(g);
                }
            }

            groups.AddRange(DefaultGroups.Where(g => !groups.Contains(g)));
            var rows = new List<InspectorRowConfig>();
            foreach (JsonNode? node in root["rows"]?.AsArray() ?? [])
            {
                string? id = node?["id"]?.GetValue<string>();
                if (id is not null && InspectorTypes.Find(id) is not null && rows.All(r => r.TypeId != id))
                {
                    rows.Add(new InspectorRowConfig(id, node!["visible"]?.GetValue<bool>() ?? true, node["opposite"]?.GetValue<bool>() ?? false));
                }
            }

            foreach (InspectorType t in InspectorTypes.All.Where(t => rows.All(r => r.TypeId != t.Id)))
            {
                rows.Add(new InspectorRowConfig(t.Id, InspectorTypes.Basic.Contains(t.Id)));
            }

            return new InspectorLayout(groups, rows);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Default;
        }
    }

    public bool Equals(InspectorLayout? other) => other is not null && Groups.SequenceEqual(other.Groups) && Rows.SequenceEqual(other.Rows);

    public override bool Equals(object? obj) => Equals(obj as InspectorLayout);

    public override int GetHashCode() => Serialize().GetHashCode(StringComparison.Ordinal);
}
