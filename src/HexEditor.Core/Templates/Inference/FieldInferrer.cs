using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace HexEditor.Core.Templates.Inference;

/// <summary>
/// フィールドの型の推定 (TPL-39)。標本のレコード (<see cref="RecordSample"/>) の列ごとの値の並びに規則 (仕様 2 の表) を当て、
/// 衝突の決め方 (仕様 3)、エンディアン (仕様 4)、フィールドの区切り方 (仕様 5) に従ってレコードを平らなフィールドの並びにする。
/// 規則の計算は「フィールドの候補の数 × N」に比例し、多くの規則は条件に外れた時点で打ち切る。
/// </summary>
public sealed class FieldInferrer
{
    /// <summary>不明 (乱数に近い) とする列のエントロピーの下限 (ビット)。</summary>
    public const double RandomEntropy = 7.0;

    private static readonly long MinTicks = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long MaxTicks = new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long CenturyTicks = TimeSpan.FromDays(36_524).Ticks;

    private readonly RecordSample _sample;
    private readonly int _k;
    private readonly int _n;
    private readonly long _documentLength;
    private readonly InferenceEndian _endian;

    // 列ごとの統計
    private readonly bool[] _constant;
    private readonly double[] _entropy;
    private readonly int[] _printable;

    private List<OffsetCandidate>? _offsetsLittle;
    private List<OffsetCandidate>? _offsetsBig;

    public FieldInferrer(RecordSample sample, long documentLength, InferenceEndian endian)
    {
        _sample = sample;
        _k = sample.RecordLength;
        _n = sample.Count;
        _documentLength = documentLength;
        _endian = endian;
        _constant = new bool[_k];
        _entropy = new double[_k];
        _printable = new int[_k];
        int[] counts = new int[256];
        for (int c = 0; c < _k; c++)
        {
            Array.Clear(counts);
            byte first = sample.At(0, c);
            bool constant = true;
            int printable = 0;
            for (int i = 0; i < _n; i++)
            {
                byte b = sample.At(i, c);
                counts[b]++;
                constant &= b == first;
                if (b is >= 0x20 and <= 0x7E)
                {
                    printable++;
                }
            }

            _constant[c] = constant;
            _printable[c] = printable;
            double h = 0;
            int distinct = 0;
            foreach (int count in counts)
            {
                if (count > 0)
                {
                    distinct++;
                    double p = (double)count / _n;
                    h -= p * Math.Log2(p);
                }
            }

            _entropy[c] = h + ((distinct - 1) / (2.0 * _n * Math.Log(2)));
        }
    }

    /// <summary>ビッグエンディアンとして値を読んだ回数 (TC-TPL-39-08 で「ビッグエンディアンの規則の計算が呼ばれない」ことを確かめる)。</summary>
    public int BigEndianEvaluations { get; private set; }

    /// <summary>レコード全体のエンディアン (仕様 4 の多数決。<see cref="InferLayout"/> の後で決まる)。</summary>
    public bool BigEndian { get; private set; }

    public int RecordLength => _k;

    public int SampleCount => _n;

    // ───────────── 区切り方 (仕様 5) ─────────────

    /// <summary>レコード全体を推定する (名前も付ける)。</summary>
    public IReadOnlyList<InferredField> InferLayout(CancellationToken cancellationToken = default)
    {
        var raw = new List<Draft>();
        int c = 0;
        while (c < _k)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FieldAt(c) is { } field)
            {
                raw.Add(field);
                c += field.Size;
                continue;
            }

            int unit = UnitSize(c);
            raw.Add(Fallback(c, unit));
            c += unit;
        }

        // レコード全体のエンディアン: エンディアンで結果が変わるフィールドの多数決 (同数ならリトル)
        BigEndian = _endian switch
        {
            InferenceEndian.Big => true,
            InferenceEndian.Little => false,
            _ => raw.Count(d => d.Vote == true) > raw.Count(d => d.Vote == false),
        };

        // 不明はまとめ、整数はレコードのエンディアンで読む
        var merged = new List<Draft>();
        foreach (Draft d in raw)
        {
            Draft resolved = d.Deferred ? Resolve(d) : d;
            if (merged.Count > 0 && merged[^1].Rule.Kind == InferredKind.Unknown && resolved.Rule.Kind == InferredKind.Unknown)
            {
                Draft previous = merged[^1];
                merged[^1] = UnknownDraft(previous.Offset, previous.Size + resolved.Size);
                continue;
            }

            merged.Add(resolved);
        }

        return Name(merged);
    }

    /// <summary>
    /// 指定した範囲を 1 つのフィールドとして推定し直す (表の「前のフィールドと結合」「分割...」。TPL-40 の仕様 3)。
    /// 自然な配置と、列挙・フラグを半分に分ける規則 (仕様 3 の 4) は使わない (利用者が範囲を決めたため)。
    /// </summary>
    public InferredField InferSpan(int offset, int size)
    {
        if (offset < 0 || size <= 0 || offset + size > _k)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

        List<InferredRule> step1 = Step1Options(offset, size);
        List<InferredRule> numeric = size is 1 or 2 or 4 or 8 ? NumericHits(offset, size, forSpan: true) : [];
        InferredRule? first = step1.FirstOrDefault();
        InferredRule? number = PickStep(numeric, InferenceConfidence.High) ?? PickStep(numeric, InferenceConfidence.Medium) ?? PickEnumFlags(numeric);
        InferredRule? pick = first is not null && !(first.Kind is InferredKind.Padding or InferredKind.Constant && number is not null) ? first : number ?? first;
        Draft fallback = Fallback(offset, size);
        Draft draft = pick is null
            ? fallback.Deferred ? Resolve(fallback) : fallback
            : new Draft(offset, size, pick, Candidates(pick, step1.Concat(numeric)), NearMiss(offset, size, pick.Kind, pick.BigEndian ?? BigEndian), null, false);
        return Name([draft], []).Single();
    }

    private InferredRule? PickStep(List<InferredRule> hits, InferenceConfidence confidence) =>
        Best(hits.Where(h => h.Confidence == confidence && h.Kind is InferredKind.Counter or InferredKind.Time or InferredKind.Offset
            or InferredKind.Length or InferredKind.Float));

    private static InferredRule? PickEnumFlags(List<InferredRule> hits) =>
        Best(hits.Where(h => h.Kind == InferredKind.Flags)) ?? Best(hits.Where(h => h.Kind == InferredKind.Enum));

    /// <summary>列 c から始まるフィールド (規則に当たらなければ null)。</summary>
    private Draft? FieldAt(int c)
    {
        List<InferredRule> step1 = Step1Options(c, null);
        InferredRule? first = step1.FirstOrDefault();

        // 数値の規則 (自然な配置)
        var hitsByWidth = new Dictionary<int, List<InferredRule>>();
        foreach (int w in new[] { 8, 4, 2, 1 })
        {
            if (c % w == 0 && c + w <= _k && !ZeroRunCrosses(c, w))
            {
                hitsByWidth[w] = NumericHits(c, w, forSpan: false);
            }
        }

        (InferredRule Rule, int Width)? numeric = null;
        foreach (InferenceConfidence confidence in new[] { InferenceConfidence.High, InferenceConfidence.Medium })
        {
            foreach (int w in new[] { 8, 4, 2 })
            {
                if (numeric is null && hitsByWidth.TryGetValue(w, out List<InferredRule>? hits) && PickStep(hits, confidence) is { } r)
                {
                    numeric = (r, w);
                }
            }
        }

        foreach (int w in new[] { 4, 2, 1 })
        {
            if (numeric is null && hitsByWidth.TryGetValue(w, out List<InferredRule>? hits) && PickEnumFlags(hits) is { } r)
            {
                numeric = (r, w);
            }
        }

        // 埋め草・定数・文字列が同じ列から始まる数値の候補の中で終わるときは、数値を先にする (数値の上位の 00 のバイトを埋め草にしない。
        // 上位が 0 の 64 bit の値を 2 文字の UTF-16 の文字列にしない)
        if (first is not null && !(first.Kind != InferredKind.Magic && numeric is { } n && n.Width > first.Type.Size))
        {
            return new Draft(c, first.Type.Size, first, Candidates(first, step1), NearMiss(c, first.Type.Size, first.Kind, first.BigEndian ?? false), null, false);
        }

        if (numeric is { } chosen)
        {
            List<InferredRule> all = hitsByWidth[chosen.Width];
            return new Draft(c, chosen.Width, chosen.Rule, Candidates(chosen.Rule, all.Concat(step1)), NearMiss(c, chosen.Width, chosen.Rule.Kind, chosen.Rule.BigEndian ?? false),
                Vote(all, chosen.Rule), false);
        }

        return null;
    }

    /// <summary>規則に当たらない列の単位 (自然な配置の 4・2・1 バイト。中で始まって外へ続く候補があれば小さくする)。</summary>
    private int UnitSize(int c)
    {
        foreach (int u in new[] { 4, 2 })
        {
            if (c % u != 0 || c + u > _k)
            {
                continue;
            }

            bool ok = true;
            for (int z = c + 1; z < c + u && ok; z++)
            {
                if (FieldAtCheap(z) is int end && end > c + u)
                {
                    ok = false;
                }
            }

            if (ok)
            {
                return u;
            }
        }

        return 1;
    }

    /// <summary>列 z から始まる候補の終わり (なければ null)。単位の大きさを決めるためだけに使う。</summary>
    private int? FieldAtCheap(int z)
    {
        if (Step1Options(z, null).FirstOrDefault() is { } s)
        {
            return z + s.Type.Size;
        }

        foreach (int w in new[] { 8, 4, 2 })
        {
            if (z % w == 0 && z + w <= _k && !ZeroRunCrosses(z, w) && NumericHits(z, w, forSpan: false).Count > 0)
            {
                return z + w;
            }
        }

        return null;
    }

    /// <summary>候補の中で始まる 00 の連続 (すべてのレコードで 00 の列) が、候補の末尾を越えて続くか (その 00 は埋め草のもの)。</summary>
    private bool ZeroRunCrosses(int c, int w) =>
        c + w < _k && IsZeroColumn(c + w - 1) && IsZeroColumn(c + w) && !Enumerable.Range(c, w).All(IsZeroColumn);

    private bool IsZeroColumn(int c) => _constant[c] && _sample.At(0, c) == 0;

    private bool AllRandom(int c, int size)
    {
        for (int i = c; i < c + size; i++)
        {
            if (_entropy[i] < RandomEntropy)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>規則に当たらない部分: 不明 (すべてのバイトが乱数に近い) か整数 (エンディアンは後で決める)。</summary>
    private Draft Fallback(int c, int size) =>
        AllRandom(c, size) ? UnknownDraft(c, size) : new Draft(c, size, IntegerRule(c, size, false), [], null, null, true);

    private Draft Resolve(Draft d)
    {
        InferredRule rule = IntegerRule(d.Offset, d.Size, BigEndian);
        return d with { Rule = rule, Candidates = [rule], NearMiss = NearMiss(d.Offset, d.Size, InferredKind.Integer, BigEndian), Deferred = false };
    }

    private Draft UnknownDraft(int c, int size)
    {
        double min = double.MaxValue;
        for (int i = c; i < c + size; i++)
        {
            min = Math.Min(min, _entropy[i]);
        }

        var rule = new InferredRule(InferredKind.Unknown, InferenceConfidence.Low, new InferenceType("bytes", size), null,
            new InferenceMessage("TplInfer_Reason_Unknown", Math.Floor(min * 10) / 10));
        return new Draft(c, size, rule, [rule], null, null, false);
    }

    private InferredRule IntegerRule(int c, int size, bool big)
    {
        if (size is not (1 or 2 or 4 or 8))
        {
            return new InferredRule(InferredKind.Integer, InferenceConfidence.Low, new InferenceType("bytes", size), null,
                new InferenceMessage("TplInfer_Reason_Bytes", size));
        }

        ulong[] v = Values(c, size, big);
        int bits = size * 8;
        ulong msb = 1UL << (bits - 1);
        int msbSet = v.Count(x => (x & msb) != 0);
        bool signed = false;
        if (msbSet >= 0.1 * _n)
        {
            double limit = Math.Pow(2, bits - 4);
            signed = v.All(x => Math.Abs((double)SignExtend(x, size)) < limit);
        }

        string id = (signed ? "s" : "u") + bits.ToString(CultureInfo.InvariantCulture);
        object min, max;
        if (signed)
        {
            min = v.Min(x => SignExtend(x, size));
            max = v.Max(x => SignExtend(x, size));
        }
        else
        {
            min = v.Min();
            max = v.Max();
        }

        return new InferredRule(InferredKind.Integer, InferenceConfidence.Low, new InferenceType(id, size), size > 1 ? big : null,
            new InferenceMessage("TplInfer_Reason_Integer", min, max)) { Spread = v.Max() - v.Min() };
    }

    /// <summary>エンディアンで結果が変わるフィールドなら、選んだ規則のエンディアン (多数決の票)。</summary>
    private static bool? Vote(List<InferredRule> hits, InferredRule chosen)
    {
        if (chosen.BigEndian is not bool big)
        {
            return null;
        }

        InferredRule? other = Best(hits.Where(h => h.BigEndian == !big && h.Kind == chosen.Kind));
        bool same = other is not null && other.Confidence == chosen.Confidence && other.Spread == chosen.Spread;
        return same ? null : big;
    }

    /// <summary>候補の一覧 (確度の順に最大 3 つ。先頭は採った規則)。</summary>
    private static IReadOnlyList<InferredRule> Candidates(InferredRule chosen, IEnumerable<InferredRule> others)
    {
        var list = new List<InferredRule> { chosen };
        foreach (InferredRule r in others.Where(o => o.Type.Size == chosen.Type.Size).OrderByDescending(o => o.Confidence))
        {
            if (list.Count < 3 && !list.Any(x => x.Kind == r.Kind && x.Type.Id == r.Type.Id))
            {
                list.Add(r);
            }
        }

        return list;
    }

    /// <summary>確度の高い規則 (同じなら値の範囲の小さい方、さらに同じならリトルエンディアン、規則の表の順)。</summary>
    private static InferredRule? Best(IEnumerable<InferredRule> rules) =>
        rules.OrderByDescending(r => r.Confidence).ThenBy(r => r.Spread).ThenBy(r => r.BigEndian == true).ThenBy(r => r.Kind).FirstOrDefault();

    // ───────────── 名前 (仕様 7) ─────────────

    private static IReadOnlyList<InferredField> Name(List<Draft> drafts, HashSet<string>? used = null)
    {
        used ??= new HashSet<string>(StringComparer.Ordinal);
        var result = new List<InferredField>(drafts.Count);
        foreach (Draft d in drafts)
        {
            string? name = BaseName(d.Rule.Kind, d.Offset);
            if (name is not null)
            {
                string unique = name;
                for (int i = 2; !used.Add(unique); i++)
                {
                    unique = name + "_" + i.ToString(CultureInfo.InvariantCulture);
                }

                name = unique;
            }

            result.Add(new InferredField
            {
                Offset = d.Offset,
                Size = d.Size,
                Rule = d.Rule,
                Candidates = d.Candidates.Count > 0 ? d.Candidates : [d.Rule],
                NearMiss = d.NearMiss,
                Name = name,
            });
        }

        return result;
    }

    /// <summary>種類と列から付ける名前 (埋め草は null)。</summary>
    public static string? BaseName(InferredKind kind, int offset)
    {
        string hex = offset.ToString("X2", CultureInfo.InvariantCulture);
        return kind switch
        {
            InferredKind.Padding => null,
            InferredKind.Magic => "magic",
            InferredKind.Counter => "counter_" + hex,
            InferredKind.Time => "time_" + hex,
            InferredKind.Offset => "offset_" + hex,
            InferredKind.Length => "length_" + hex,
            InferredKind.Float => "value_" + hex,
            InferredKind.AsciiString or InferredKind.Utf16String => "name_" + hex,
            InferredKind.Enum => "kind_" + hex,
            InferredKind.Flags => "flags_" + hex,
            InferredKind.Constant => "const_" + hex,
            InferredKind.Integer => "field_" + hex,
            _ => "unknown_" + hex,
        };
    }

    // ───────────── 埋め草・マジック・定数・文字列 ─────────────

    /// <summary>
    /// 列 c から始まる埋め草・マジック・定数・文字列の候補 (長い順、同じ長さならこの順)。<paramref name="exact"/> を指定すると、その長さの候補だけ。
    /// </summary>
    private List<InferredRule> Step1Options(int c, int? exact)
    {
        var options = new List<InferredRule>();
        if (_constant[c])
        {
            int end = c;
            if (_sample.At(0, c) == 0)
            {
                while (end < _k && IsZeroColumn(end) && (exact is null || end < c + exact))
                {
                    end++;
                }

                int n = end - c;
                if (exact is null || n == exact)
                {
                    options.Add(new InferredRule(InferredKind.Padding, n >= 2 ? InferenceConfidence.High : InferenceConfidence.Medium,
                        new InferenceType("padding", n), null, new InferenceMessage("TplInfer_Reason_Padding", _n)));
                }
            }
            else
            {
                while (end < _k && _constant[end] && (exact is null || end < c + exact))
                {
                    end++;
                }

                // 末尾の 00 は含めない (後ろの数値の上位のバイトであることが多い)。範囲を指定したときはそのまま。
                if (exact is null)
                {
                    while (end > c + 1 && IsZeroColumn(end - 1))
                    {
                        end--;
                    }
                }

                int n = end - c;
                if (exact is null || n == exact)
                {
                    byte[] bytes = [.. Enumerable.Range(c, n).Select(i => _sample.At(0, i))];
                    int printable = bytes.Count(b => b is >= 0x20 and <= 0x7E);
                    bool magic = n >= 2 && (c == 0 || printable >= 0.75 * n);
                    string display = printable == n ? "\"" + System.Text.Encoding.ASCII.GetString(bytes) + "\"" : Convert.ToHexString(bytes).Chunk(2).Aggregate(
                        new System.Text.StringBuilder(), (sb, p) => sb.Append(sb.Length > 0 ? " " : string.Empty).Append(p)).ToString();
                    InferenceType type = magic && printable == n ? new InferenceType("char", n) : new InferenceType("bytes", n);
                    options.Add(magic
                        ? new InferredRule(InferredKind.Magic, InferenceConfidence.High, type, null, new InferenceMessage("TplInfer_Reason_Magic", display, _n))
                        : new InferredRule(InferredKind.Constant, InferenceConfidence.Medium, type, null, new InferenceMessage("TplInfer_Reason_Constant", display, _n)));
                }
            }
        }

        if (AsciiString(c, exact) is { } ascii)
        {
            options.Add(ascii);
        }

        InferredRule? utf16 = null;
        foreach (bool big in Endians())
        {
            if (Utf16String(c, exact, big) is { } u && (utf16 is null || Better(u, utf16)))
            {
                utf16 = u;
            }
        }

        if (utf16 is not null)
        {
            options.Add(utf16);
        }

        // 長い順。同じ長さならマジック・埋め草・定数・文字列 (ASCII・UTF-16) の順
        return [.. options.OrderByDescending(o => o.Type.Size).ThenBy(o => o.Kind switch
        {
            InferredKind.Magic => 0,
            InferredKind.Padding => 1,
            InferredKind.Constant => 2,
            InferredKind.AsciiString => 3,
            _ => 4,
        })];
    }

    private static bool Better(InferredRule a, InferredRule b) =>
        a.Confidence > b.Confidence || (a.Confidence == b.Confidence && (a.Type.Size > b.Type.Size || (a.Type.Size == b.Type.Size && a.Spread < b.Spread)));

    /// <summary>文字列の長さの上限 (バイト)。</summary>
    private const int MaxStringScan = 4096;

    /// <summary>文字列 (ASCII): 90% 以上のレコードで、印字可能な文字が 1 文字以上続き、その後ろは 00 だけ (仕様 2 の表)。</summary>
    private InferredRule? AsciiString(int c, int? exact)
    {
        if (_printable[c] < 0.9 * _n)
        {
            return null;
        }

        int limit = Math.Min(_k, c + MaxStringScan);
        int[] text = new int[_n];
        int[] zeros = new int[_n];
        for (int i = 0; i < _n; i++)
        {
            ReadOnlySpan<byte> rec = _sample.Record(i);
            int p = c;
            while (p < limit && rec[p] is >= 0x20 and <= 0x7E)
            {
                p++;
            }

            int z = p;
            while (z < limit && rec[z] == 0)
            {
                z++;
            }

            text[i] = p - c;
            zeros[i] = z - p;
        }

        return StringRule(c, exact, text, zeros, 1, InferredKind.AsciiString, null);
    }

    /// <summary>文字列 (UTF-16)。文字は U+0020〜U+D7FF・U+E000〜U+FFFD、その後ろは 0000 だけ。80% 以上の文字が同じブロック。</summary>
    private InferredRule? Utf16String(int c, int? exact, bool big)
    {
        if (c + 4 > _k || (exact is int e && e % 2 != 0))
        {
            return null;
        }

        int limit = c + (Math.Min(_k - c, MaxStringScan) & ~1);
        int[] text = new int[_n];
        int[] zeros = new int[_n];
        int firstOk = 0;
        for (int i = 0; i < _n; i++)
        {
            ReadOnlySpan<byte> rec = _sample.Record(i);
            int p = c;
            while (p + 2 <= limit && IsUtf16Char(Char16(rec, p, big)))
            {
                p += 2;
            }

            int z = p;
            while (z + 2 <= limit && Char16(rec, z, big) == 0)
            {
                z += 2;
            }

            text[i] = (p - c) / 2;
            zeros[i] = (z - p) / 2;
            if (text[i] > 0)
            {
                firstOk++;
            }

            // 早めに打ち切る: 1 文字目が文字でないレコードが 10% を超えた
            if (i - firstOk + 1 > 0.1 * _n)
            {
                return null;
            }
        }

        if (big)
        {
            BigEndianEvaluations++;
        }

        return StringRule(c, exact, text, zeros, 2, InferredKind.Utf16String, big);
    }

    private InferredRule? StringRule(int c, int? exact, int[] text, int[] zeros, int unit, InferredKind kind, bool? big)
    {
        // n (文字数) の候補: レコードの文字数のうち大きいものから。90% 以上のレコードがその長さの窓を満たす最大の n
        int minChars = kind == InferredKind.AsciiString ? 4 : 2;
        IEnumerable<int> lengths = exact is int e ? [e / unit] : text.Distinct().Where(t => t >= minChars).OrderDescending();
        foreach (int n in lengths)
        {
            if (n < minChars || c + (n * unit) > _k)
            {
                continue;
            }

            int support = 0;
            for (int i = 0; i < _n; i++)
            {
                if (text[i] >= 1 && (text[i] >= n || text[i] + zeros[i] >= n))
                {
                    support++;
                }
            }

            double ratio = (double)support / _n;
            if (ratio < 0.9)
            {
                if (exact is not null)
                {
                    return null;
                }

                continue;
            }

            ulong spread = 0;
            if (kind == InferredKind.Utf16String)
            {
                // 0000 でない文字の 80% 以上が同じブロック
                var blocks = new Dictionary<int, int>();
                int total = 0;
                int lo = int.MaxValue, hi = 0;
                for (int i = 0; i < _n; i++)
                {
                    ReadOnlySpan<byte> rec = _sample.Record(i);
                    for (int j = 0; j < Math.Min(n, text[i]); j++)
                    {
                        int ch = Char16(rec, c + (j * 2), big == true);
                        int block = Block(ch);
                        blocks[block] = blocks.GetValueOrDefault(block) + 1;
                        total++;
                        lo = Math.Min(lo, ch);
                        hi = Math.Max(hi, ch);
                    }
                }

                if (total == 0 || blocks.Values.Max() < 0.8 * total)
                {
                    if (exact is not null)
                    {
                        return null;
                    }

                    continue;
                }

                spread = (ulong)(hi - lo);
            }

            InferenceConfidence confidence = ratio >= 0.98 ? InferenceConfidence.High : InferenceConfidence.Medium;
            var type = new InferenceType(kind == InferredKind.AsciiString ? "char" : "char16", n * unit);
            string key = kind == InferredKind.AsciiString ? "TplInfer_Reason_Ascii" : "TplInfer_Reason_Utf16";
            return new InferredRule(kind, confidence, type, big, new InferenceMessage(key, Percent(ratio), n)) { Spread = spread };
        }

        return null;
    }

    private static int Char16(ReadOnlySpan<byte> rec, int p, bool big) =>
        big ? BinaryPrimitives.ReadUInt16BigEndian(rec[p..]) : BinaryPrimitives.ReadUInt16LittleEndian(rec[p..]);

    private static bool IsUtf16Char(int ch) => ch is (>= 0x20 and <= 0xD7FF) or (>= 0xE000 and <= 0xFFFD);

    /// <summary>Unicode のブロック (主なもの。ほかは 128 文字ごと)。</summary>
    private static int Block(int ch) => ch switch
    {
        < 0x80 => -1,
        < 0x100 => -2,
        < 0x250 => -3,
        >= 0x370 and < 0x400 => -4,
        >= 0x400 and < 0x530 => -5,
        >= 0x3040 and < 0x30A0 => -6,
        >= 0x30A0 and < 0x3100 => -7,
        >= 0x3400 and < 0x4DC0 => -8,
        >= 0x4E00 and < 0xA000 => -9,
        >= 0xAC00 and < 0xD7B0 => -10,
        >= 0xFF00 and < 0xFFF0 => -11,
        _ => ch >> 7,
    };

    // ───────────── 数値の規則 ─────────────

    private IEnumerable<bool> Endians() => _endian switch
    {
        InferenceEndian.Little => [false],
        InferenceEndian.Big => [true],
        _ => [false, true],
    };

    /// <summary>列 c から w バイトの数値の規則に当たったもの (両方のエンディアン。衝突の決め方を当てた後)。</summary>
    private List<InferredRule> NumericHits(int c, int w, bool forSpan)
    {
        var hits = new List<InferredRule>();
        foreach (bool big in w == 1 ? [false] : Endians())
        {
            hits.AddRange(NumericHits(c, w, big, forSpan));
        }

        return hits;
    }

    private List<InferredRule> NumericHits(int c, int w, bool big, bool forSpan)
    {
        ulong[] v = Values(c, w, big);
        bool? endian = w == 1 ? null : big;
        var hits = new List<InferredRule>();
        ulong spread = v.Max() - v.Min();

        InferredRule? counter = w >= 2 ? CounterRule(v, w, endian, out _) : null;
        InferredRule? time = null, offset = null, length = null;
        if (counter is null)
        {
            // 連番に当たる候補は、日時・オフセット・長さにしない (仕様 3 の 1)
            time = TimeRule(v, w, endian, out _);
            offset = OffsetRule(v, w, endian);
            length = LengthRule(c, v, w, big, endian);

            // 長さ (中) は条件が弱いため、独立に変わる 2 つのフィールドを 1 つにまとめないよう、列挙・フラグと同じく半分に分けて調べる
            if (length is { Confidence: InferenceConfidence.Medium } && !forSpan && w >= 2 && IndependentHalves(c, w, v.Distinct().Count()))
            {
                length = null;
            }
        }

        InferredRule? number = FloatRule(v, w, endian, out _);
        if (time is not null && number is not null)
        {
            // 日時と浮動小数点の両方: レコード番号の順に減らない割合が 90% 以上なら日時 (仕様 3 の 3)
            if (NonDecreasing(v) >= 0.9)
            {
                number = null;
            }
            else
            {
                time = null;
            }
        }

        foreach (InferredRule? r in new[] { counter, time, offset, length, number })
        {
            if (r is not null)
            {
                hits.Add(r with { Spread = spread });
            }
        }

        if (w <= 4)
        {
            InferredRule? flags = FlagsRule(v, w, endian);
            InferredRule? kind = flags is null ? EnumRule(v, w, endian) : null;
            InferredRule? enumOrFlags = flags ?? kind;
            if (enumOrFlags is not null && (forSpan || w == 1 || !IndependentHalves(c, w, v.Distinct().Count())))
            {
                hits.Add(enumOrFlags with { Spread = spread });
            }
        }

        return hits;
    }

    /// <summary>標本の値 (列 c から w バイト)。</summary>
    private ulong[] Values(int c, int w, bool big)
    {
        if (big && w > 1)
        {
            BigEndianEvaluations++;
        }

        ulong[] v = new ulong[_n];
        for (int i = 0; i < _n; i++)
        {
            v[i] = Read(_sample.Record(i), c, w, big);
        }

        return v;
    }

    private static ulong Read(ReadOnlySpan<byte> rec, int c, int w, bool big) => w switch
    {
        1 => rec[c],
        2 => big ? BinaryPrimitives.ReadUInt16BigEndian(rec[c..]) : BinaryPrimitives.ReadUInt16LittleEndian(rec[c..]),
        4 => big ? BinaryPrimitives.ReadUInt32BigEndian(rec[c..]) : BinaryPrimitives.ReadUInt32LittleEndian(rec[c..]),
        _ => big ? BinaryPrimitives.ReadUInt64BigEndian(rec[c..]) : BinaryPrimitives.ReadUInt64LittleEndian(rec[c..]),
    };

    private static long SignExtend(ulong v, int w) => w switch
    {
        1 => (sbyte)v,
        2 => (short)v,
        4 => (int)v,
        _ => (long)v,
    };

    private static ulong Mask(int w) => w == 8 ? ulong.MaxValue : (1UL << (w * 8)) - 1;

    private static int Percent(double ratio) => (int)Math.Floor((ratio * 100) + 1e-9);

    /// <summary>レコード番号の順に減らない割合 (標本の隣どうし)。</summary>
    private double NonDecreasing(ulong[] v)
    {
        if (v.Length < 2)
        {
            return 1;
        }

        int ok = 0;
        for (int i = 1; i < v.Length; i++)
        {
            if (v[i] >= v[i - 1])
            {
                ok++;
            }
        }

        return (double)ok / (v.Length - 1);
    }

    /// <summary>連番: 値 = a + d × r (0 &lt; |d| ≤ 65,536、0 ≤ a &lt; 2^24) のレコードが 95% 以上。</summary>
    private InferredRule? CounterRule(ulong[] v, int w, bool? endian, out double ratio)
    {
        ratio = 0;
        ulong mask = Mask(w);
        long[] r = _sample.Indices;
        var tried = new HashSet<(ulong, long)>();
        int allowedMisses = (int)Math.Floor(0.05 * _n);
        (ulong A, long D, int Matches)? best = null;
        for (int i = 0; i + 1 < _n && i < 16; i++)
        {
            long dr = r[i + 1] - r[i];
            long dv = SignExtend((v[i + 1] - v[i]) & mask, w);
            if (dr <= 0 || dv % dr != 0)
            {
                continue;
            }

            long d = dv / dr;
            if (d == 0 || Math.Abs(d) > 65_536)
            {
                continue;
            }

            ulong a = unchecked(v[i] - ((ulong)d * (ulong)r[i])) & mask;
            if (a >= 1UL << 24 || !tried.Add((a, d)))
            {
                continue;
            }

            int matches = 0;
            int misses = 0;
            for (int j = 0; j < _n && misses <= allowedMisses; j++)
            {
                if ((unchecked(a + ((ulong)d * (ulong)r[j])) & mask) == v[j])
                {
                    matches++;
                }
                else
                {
                    misses++;
                }
            }

            if (best is null || matches > best.Value.Matches)
            {
                best = (a, d, matches);
            }
        }

        if (best is not { } b)
        {
            return null;
        }

        ratio = (double)b.Matches / _n;
        if (ratio < 0.95)
        {
            return null;
        }

        InferenceConfidence confidence = b.Matches == _n ? InferenceConfidence.High : InferenceConfidence.Medium;
        return new InferredRule(InferredKind.Counter, confidence, new InferenceType(InferenceType.UnsignedOf(w), w), endian,
            new InferenceMessage("TplInfer_Reason_Counter", b.Matches, _n, (long)b.A, b.D)) { Counter = ((long)b.A, b.D) };
    }

    /// <summary>日時の型として読んだ時刻 (範囲外・不正は null)。</summary>
    private static long? TimeTicks(string type, ulong v)
    {
        try
        {
            switch (type)
            {
                case "time32_t":
                    return DateTime.UnixEpoch.Ticks + ((long)v * TimeSpan.TicksPerSecond);
                case "time64_t":
                    long s = (long)v;
                    return s is > -100_000_000_000 and < 100_000_000_000 ? DateTime.UnixEpoch.Ticks + (s * TimeSpan.TicksPerSecond) : null;
                case "filetime":
                    long ft = (long)v;
                    return ft is >= 0 and <= 2650467743999999999 ? DateTime.FromFileTimeUtc(ft).Ticks : null;
                default:
                    int date = (int)(v >> 16) & 0xFFFF, time = (int)(v & 0xFFFF);
                    int year = 1980 + ((date >> 9) & 0x7F), month = (date >> 5) & 0xF, day = date & 0x1F;
                    int hour = (time >> 11) & 0x1F, minute = (time >> 5) & 0x3F, second = (time & 0x1F) * 2;
                    if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 58)
                    {
                        return null;
                    }

                    return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>日時: 1980-01-01〜2100-01-01 の範囲の割合が 95% 以上、すべて同じではなく、最大と最小の差が 100 年未満。</summary>
    private InferredRule? TimeRule(ulong[] v, int w, bool? endian, out double bestRatio)
    {
        bestRatio = 0;
        string[] types = w switch
        {
            4 => ["dosdatetime", "time32_t"],
            8 => ["filetime", "time64_t"],
            _ => [],
        };
        int allowedMisses = (int)Math.Floor(0.05 * _n);
        foreach (string type in types)
        {
            long[] ticks = new long[_n];
            int ok = 0, misses = 0;
            for (int i = 0; i < _n && misses <= allowedMisses; i++)
            {
                if (TimeTicks(type, v[i]) is long t && t >= MinTicks && t < MaxTicks)
                {
                    ticks[i] = t;
                    ok++;
                }
                else
                {
                    ticks[i] = long.MinValue;
                    misses++;
                }
            }

            double ratio = (double)ok / _n;
            bestRatio = Math.Max(bestRatio, ratio);
            if (ratio < 0.95)
            {
                continue;
            }

            long min = ticks.Where(t => t != long.MinValue).Min(), max = ticks.Max();
            if (min == max || max - min >= CenturyTicks)
            {
                continue;
            }

            int nonDecreasing = 0, pairs = 0;
            for (int i = 1; i < _n; i++)
            {
                if (ticks[i] != long.MinValue && ticks[i - 1] != long.MinValue)
                {
                    pairs++;
                    if (ticks[i] >= ticks[i - 1])
                    {
                        nonDecreasing++;
                    }
                }
            }

            InferenceConfidence confidence = pairs > 0 && nonDecreasing >= 0.9 * pairs ? InferenceConfidence.High : InferenceConfidence.Medium;
            DateTime from = new DateTime(min, DateTimeKind.Utc).Date, to = new DateTime(max, DateTimeKind.Utc).Date.AddDays(1);
            return new InferredRule(InferredKind.Time, confidence, new InferenceType(type, w), endian,
                new InferenceMessage("TplInfer_Reason_Time_" + type, Percent(ratio),
                    from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        return null;
    }

    private bool OffsetWidth(int w) => w is 4 or 8 || (w == 2 && _documentLength < 64 * 1024);

    /// <summary>オフセット: 00 でない値が 50% 以上で、すべてドキュメントの長さ未満、その 90% 以上が互いに異なる。</summary>
    private InferredRule? OffsetRule(ulong[] v, int w, bool? endian)
    {
        if (!OffsetWidth(w))
        {
            return null;
        }

        var nonzero = new List<ulong>(_n);
        foreach (ulong x in v)
        {
            if (x != 0)
            {
                if (x >= (ulong)_documentLength)
                {
                    return null;
                }

                nonzero.Add(x);
            }
        }

        if (nonzero.Count < 0.5 * _n || nonzero.Count == 0)
        {
            return null;
        }

        int distinct = nonzero.Distinct().Count();
        if (distinct < 0.9 * nonzero.Count)
        {
            return null;
        }

        int increasing = 0;
        for (int i = 1; i < nonzero.Count; i++)
        {
            if (nonzero[i] > nonzero[i - 1])
            {
                increasing++;
            }
        }

        string length = "0x" + _documentLength.ToString("X", CultureInfo.InvariantCulture);
        double increasingRatio = nonzero.Count > 1 ? (double)increasing / (nonzero.Count - 1) : 0;
        if (increasingRatio >= 0.9)
        {
            for (int bit = 30; bit >= 2; bit--)
            {
                ulong p = 1UL << bit;
                int multiples = nonzero.Count(x => x % p == 0);
                if (multiples >= 0.9 * nonzero.Count)
                {
                    return new InferredRule(InferredKind.Offset, InferenceConfidence.High, new InferenceType(InferenceType.UnsignedOf(w), w), endian,
                        new InferenceMessage("TplInfer_Reason_OffsetHigh", length, Percent((double)multiples / nonzero.Count), (long)p));
                }
            }
        }

        return new InferredRule(InferredKind.Offset, InferenceConfidence.Medium, new InferenceType(InferenceType.UnsignedOf(w), w), endian,
            new InferenceMessage("TplInfer_Reason_Offset", length, Percent((double)distinct / nonzero.Count)));
    }

    private sealed record OffsetCandidate(int Column, int Width, ulong[] Values);

    /// <summary>長さの規則で調べるオフセットの候補の最大の数。</summary>
    private const int MaxOffsetsForLength = 32;

    /// <summary>オフセットの候補 (連番でないもの。長さの規則で使う)。</summary>
    private List<OffsetCandidate> OffsetCandidates(bool big)
    {
        ref List<OffsetCandidate>? cache = ref big ? ref _offsetsBig : ref _offsetsLittle;
        if (cache is not null)
        {
            return cache;
        }

        var list = new List<OffsetCandidate>();
        foreach (int w in new[] { 8, 4, 2 })
        {
            if (!OffsetWidth(w))
            {
                continue;
            }

            for (int c = 0; c + w <= _k; c += w)
            {
                if (!QuickOffset(c, w, big))
                {
                    continue;
                }

                ulong[] v = Values(c, w, big);
                if (OffsetRule(v, w, big) is not null && CounterRule(v, w, big, out _) is null)
                {
                    list.Add(new OffsetCandidate(c, w, v));
                }
            }
        }

        cache = list;
        return list;
    }

    /// <summary>値を配列にせずに、ドキュメントの長さ以上の値があるかを先に調べる (多くの列はここで外れる)。</summary>
    private bool QuickOffset(int c, int w, bool big)
    {
        int nonzero = 0;
        for (int i = 0; i < _n; i++)
        {
            ulong x = Read(_sample.Record(i), c, w, big);
            if (x >= (ulong)_documentLength)
            {
                return false;
            }

            if (x != 0)
            {
                nonzero++;
            }
        }

        return nonzero >= 0.5 * _n;
    }

    /// <summary>
    /// 長さ: 同じレコードのオフセットの候補 o について、連続した標本で「次のレコードの o − このレコードの o = この値」が 90% 以上 (高)。
    /// または、すべてのレコードで「o + この値 ≤ ドキュメントの長さ」、値がすべて 2^24 未満で 00 でない値が 50% 以上、異なる値が 17 個以上 (中)。
    /// </summary>
    private InferredRule? LengthRule(int c, ulong[] v, int w, bool big, bool? endian)
    {
        List<OffsetCandidate> offsets = OffsetCandidates(big);
        if (offsets.Count == 0)
        {
            return null;
        }

        int consecutive = Math.Min(_sample.ConsecutiveCount, _n);
        long[] r = _sample.Indices;
        InferredRule? medium = null;

        // 近いオフセットの候補から調べる (候補が多いデータで時間が「候補の数 × N」を大きく超えないよう、最大 32 個)
        foreach (OffsetCandidate o in offsets.OrderBy(o => Math.Abs(o.Column - c)).Take(MaxOffsetsForLength))
        {
            if (o.Column < c + w && c < o.Column + o.Width)
            {
                continue;
            }

            string name = BaseName(InferredKind.Offset, o.Column)!;
            int pairs = 0, ok = 0;
            for (int i = 0; i + 1 < consecutive; i++)
            {
                if (r[i + 1] == r[i] + 1)
                {
                    pairs++;
                    if (unchecked(o.Values[i + 1] - o.Values[i]) == v[i])
                    {
                        ok++;
                    }
                }
            }

            if (pairs >= 3 && ok >= 0.9 * pairs)
            {
                return new InferredRule(InferredKind.Length, InferenceConfidence.High, new InferenceType(InferenceType.UnsignedOf(w), w), endian,
                    new InferenceMessage("TplInfer_Reason_LengthHigh", Percent((double)ok / pairs), name));
            }

            if (medium is null)
            {
                bool all = true;
                int nonzero = 0;
                for (int i = 0; i < _n && all; i++)
                {
                    all = v[i] < 1UL << 24 && o.Values[i] + v[i] <= (ulong)_documentLength;
                    if (v[i] != 0)
                    {
                        nonzero++;
                    }
                }

                if (all && nonzero >= 0.5 * _n && v.Distinct().Take(17).Count() > 16)
                {
                    medium = new InferredRule(InferredKind.Length, InferenceConfidence.Medium, new InferenceType(InferenceType.UnsignedOf(w), w), endian,
                        new InferenceMessage("TplInfer_Reason_Length", name));
                }
            }
        }

        return medium;
    }

    /// <summary>浮動小数点: 値の 95% 以上が 0 か、絶対値が 1e-9〜1e12 の正規化数で、異なる値が 25% 以上。</summary>
    private InferredRule? FloatRule(ulong[] v, int w, bool? endian, out double ratio)
    {
        ratio = 0;
        if (w is not (4 or 8))
        {
            return null;
        }

        int ok = 0, misses = 0;
        int allowedMisses = (int)Math.Floor(0.05 * _n);
        for (int i = 0; i < _n; i++)
        {
            double x = w == 4 ? BitConverter.UInt32BitsToSingle((uint)v[i]) : BitConverter.UInt64BitsToDouble(v[i]);
            double a = Math.Abs(x);
            if (x == 0 || (double.IsNormal(x) && (w == 8 || float.IsNormal((float)x)) && a >= 1e-9 && a <= 1e12))
            {
                ok++;
            }
            else if (++misses > allowedMisses)
            {
                ratio = (double)ok / _n;
                return null;
            }
        }

        ratio = (double)ok / _n;
        int distinct = v.Distinct().Count();
        if (ratio < 0.95 || distinct < 0.25 * _n)
        {
            return null;
        }

        return new InferredRule(InferredKind.Float, ratio >= 0.99 ? InferenceConfidence.High : InferenceConfidence.Medium,
            new InferenceType(w == 4 ? "f32" : "f64", w), endian, new InferenceMessage("TplInfer_Reason_Float", Percent(ratio), distinct));
    }

    /// <summary>異なる値 (上限を超えたら null)。</summary>
    private static List<ulong>? DistinctUpTo(ulong[] v, int limit)
    {
        var set = new HashSet<ulong>();
        foreach (ulong x in v)
        {
            if (set.Add(x) && set.Count > limit)
            {
                return null;
            }
        }

        return [.. set.Order()];
    }

    /// <summary>フラグ: 異なる値が 2〜64 個で、1 ビットだけの値を 2 個以上、2 ビット以上の値を 1 個以上観測し、すべてが 1 ビットの値の OR。</summary>
    private static InferredRule? FlagsRule(ulong[] v, int w, bool? endian)
    {
        if (DistinctUpTo(v, 64) is not { Count: >= 2 } distinct)
        {
            return null;
        }

        ulong[] singles = [.. distinct.Where(x => BitOperations.PopCount(x) == 1)];
        ulong union = singles.Aggregate(0UL, (a, b) => a | b);
        if (singles.Length < 2 || !distinct.Any(x => BitOperations.PopCount(x) >= 2) || distinct.Any(x => (x & ~union) != 0))
        {
            return null;
        }

        string bits = string.Join(", ", singles.Select(b => "0x" + b.ToString("X" + (w * 2).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)));
        return new InferredRule(InferredKind.Flags, InferenceConfidence.Medium, new InferenceType("flags", w), endian,
            new InferenceMessage("TplInfer_Reason_Flags", singles.Length, bits)) { Values = singles };
    }

    /// <summary>列挙: 異なる値が 2〜16 個で、異なる値の数が N / 8 以下。</summary>
    private InferredRule? EnumRule(ulong[] v, int w, bool? endian)
    {
        if (DistinctUpTo(v, 16) is not { Count: >= 2 } distinct || distinct.Count > _n / 8.0)
        {
            return null;
        }

        string values = string.Join(", ", distinct.Select(x => x.ToString(CultureInfo.InvariantCulture)));
        return new InferredRule(InferredKind.Enum, InferenceConfidence.Medium, new InferenceType("enum", w), endian,
            new InferenceMessage("TplInfer_Reason_Enum", distinct.Count, values)) { Values = distinct };
    }

    /// <summary>
    /// 列挙・フラグの候補を半分に分けたとき、両方の半分が値の変わる列で、組の異なる値の数が「半分ごとの異なる値の数の積」の 50% 以上か
    /// (独立に変わる 2 つのフィールド。仕様 3 の 4)。
    /// </summary>
    private bool IndependentHalves(int c, int w, int pairDistinct)
    {
        int half = w / 2;
        bool varies1 = Enumerable.Range(c, half).Any(i => !_constant[i]);
        bool varies2 = Enumerable.Range(c + half, half).Any(i => !_constant[i]);
        if (!varies1 || !varies2)
        {
            return false;
        }

        var a = new HashSet<ulong>();
        var b = new HashSet<ulong>();
        for (int i = 0; i < _n; i++)
        {
            a.Add(Read(_sample.Record(i), c, half, false));
            b.Add(Read(_sample.Record(i), c + half, half, false));
        }

        return pairDistinct >= 0.5 * a.Count * (double)b.Count;
    }

    // ───────────── 当たらなかった規則のうち最も近いもの (仕様 8) ─────────────

    private InferenceMessage? NearMiss(int c, int size, InferredKind chosen, bool big)
    {
        var options = new List<(double Ratio, InferenceMessage Message)>();
        big = _endian switch
        {
            InferenceEndian.Little => false,
            InferenceEndian.Big => true,
            _ => big,
        };
        if (size is 2 or 4 or 8)
        {
            ulong[] v = Values(c, size, big);
            if (size is 4 or 8 && chosen != InferredKind.Float)
            {
                FloatRule(v, size, null, out double f);
                options.Add((f, new InferenceMessage("TplInfer_Near_Float", Percent(f))));
            }

            if (size is 4 or 8 && chosen != InferredKind.Time)
            {
                TimeRule(v, size, null, out double t);
                options.Add((t, new InferenceMessage("TplInfer_Near_Time", Percent(t))));
            }

            if (chosen != InferredKind.Counter)
            {
                CounterRule(v, size, null, out double n);
                options.Add((n, new InferenceMessage("TplInfer_Near_Counter", Percent(n))));
            }
        }

        if (chosen is not (InferredKind.AsciiString or InferredKind.Padding or InferredKind.Magic or InferredKind.Constant))
        {
            double printable = (double)_printable[c] / _n;
            options.Add((printable, new InferenceMessage("TplInfer_Near_Ascii", Percent(printable))));
        }

        return options.Count == 0 ? null : options.MaxBy(o => o.Ratio).Message;
    }

    /// <summary>区切り方の途中の候補。<see cref="Vote"/> はエンディアンの多数決の票、<see cref="Deferred"/> はエンディアンを後で決める整数。</summary>
    private sealed record Draft(int Offset, int Size, InferredRule Rule, IReadOnlyList<InferredRule> Candidates, InferenceMessage? NearMiss, bool? Vote, bool Deferred);
}
