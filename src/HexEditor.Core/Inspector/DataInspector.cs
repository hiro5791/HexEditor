using HexEditor.Core.Engine;

namespace HexEditor.Core.Inspector;

/// <summary>
/// 1 行の結果。<see cref="Opposite"/> は「反対のエンディアンでも表示」で加えた行 (INSP-02 の仕様 7。名前は「int32 (BE)」)。
/// <see cref="Endian"/> はこの行の解釈に使ったエンディアン。
/// </summary>
public sealed record InspectorRowResult(string TypeId, bool Opposite, Endianness Endian, InspectorValue Value);

/// <summary>表示するグループ 1 つ分の結果。</summary>
public sealed record InspectorGroupResult(InspectorGroup Group, IReadOnlyList<InspectorRowResult> Rows);

/// <summary>
/// 起点のデータを、行の構成に従ってすべての行で解釈する (INSP-01)。起点から最大 128 バイト (文字列の行があれば 4 KB) だけを、ブロックキャッシュから
/// 待たずに読む (キャッシュにないバイトは「…」。仕様 3・4)。ファイルサイズに依存しない。
/// </summary>
public static class DataInspector
{
    public static IReadOnlyList<InspectorGroupResult> Evaluate(DocumentSnapshot snapshot, long origin, InspectorLayout layout,
        Endianness endian, InspectorOptions options)
    {
        int count = (int)Math.Clamp(snapshot.Length - origin, 0, layout.ReadLength);
        byte[] data = new byte[count];
        var states = new ByteState[count];
        if (count > 0)
        {
            count = snapshot.ReadForDisplay(origin, data, states);
        }

        return Evaluate(data.AsSpan(0, count), states.AsSpan(0, count), layout, endian, options);
    }

    public static IReadOnlyList<InspectorGroupResult> Evaluate(ReadOnlySpan<byte> data, ReadOnlySpan<ByteState> states, InspectorLayout layout,
        Endianness endian, InspectorOptions options)
    {
        var groups = new List<InspectorGroupResult>();
        Endianness opposite = endian == Endianness.Little ? Endianness.Big : Endianness.Little;
        foreach ((InspectorGroup group, IReadOnlyList<InspectorRowConfig> rows) in layout.VisibleGroups())
        {
            var results = new List<InspectorRowResult>(rows.Count);
            foreach (InspectorRowConfig row in rows)
            {
                InspectorType type = InspectorTypes.Get(row.TypeId);
                results.Add(new InspectorRowResult(row.TypeId, false, endian, InspectorDecoder.Decode(type, data, states, endian, options)));
                if (row.Opposite && type.HasEndian)
                {
                    results.Add(new InspectorRowResult(row.TypeId, true, opposite, InspectorDecoder.Decode(type, data, states, opposite, options)));
                }
            }

            groups.Add(new InspectorGroupResult(group, results));
        }

        return groups;
    }
}
