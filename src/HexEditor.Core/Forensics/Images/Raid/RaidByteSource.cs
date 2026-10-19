using System.Text.Json;
using System.Text.Json.Serialization;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images.Raid;

/// <summary>RAID の種類 (FOR-26 の仕様 1)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RaidLevel>))]
public enum RaidLevel
{
    Jbod,
    Raid0,
    Raid1,
    Raid5,
}

/// <summary>RAID 5 のパリティの配置 (Linux md の left/right-symmetric/asymmetric と同じ)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Raid5Layout>))]
public enum Raid5Layout
{
    LeftSymmetric,
    LeftAsymmetric,
    RightSymmetric,
    RightAsymmetric,
}

/// <summary>RAID のメンバー (順番は一覧の順)。<see cref="Source"/> はファイルのパスまたはディスクのパス。</summary>
public sealed record RaidMember
{
    public string Source { get; init; } = string.Empty;

    /// <summary>このメンバーのデータの開始オフセット (バイト。既定 0)。</summary>
    public long DataOffset { get; init; }

    /// <summary>欠けているメンバー (RAID 5 で最大 1 台)。</summary>
    public bool Missing { get; init; }
}

/// <summary>RAID の構成 (<c>.raid.json</c>。FOR-26 の仕様 3)。</summary>
public sealed record RaidConfig
{
    public const long DefaultStripeSize = 64 * 1024;
    public const long MinStripeSize = 512;
    public const long MaxStripeSize = 16L * 1024 * 1024;

    public int Version { get; init; } = 1;

    public RaidLevel Level { get; init; } = RaidLevel.Raid5;

    public List<RaidMember> Members { get; init; } = [];

    public long StripeSize { get; init; } = DefaultStripeSize;

    public Raid5Layout Layout { get; init; } = Raid5Layout.LeftSymmetric;

    /// <summary>RAID 1 で使うメンバーの番号。</summary>
    public int UseMember { get; init; }

    public int SectorSize { get; init; } = 512;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true, MaxDepth = 16 };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>JSON から読む。形・値が正しくなければ <see cref="InvalidDataException"/>。</summary>
    public static RaidConfig Parse(string json)
    {
        RaidConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<RaidConfig>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The RAID configuration is not valid JSON: " + ex.Message, ex);
        }

        if (config is null || config.Members is null)
        {
            throw new InvalidDataException("The RAID configuration is empty.");
        }

        if (config.Validate() is { } problem)
        {
            throw new InvalidDataException(problem.ToString());
        }

        return config;
    }

    /// <summary>構成の問題 (なければ null)。</summary>
    public RaidProblem? Validate()
    {
        int count = Members.Count;
        int missing = Members.Count(m => m.Missing);
        if (count > 64 || SectorSize is < 512 or > 65536 || Members.Any(m => m.DataOffset < 0 || m.DataOffset % 512 != 0))
        {
            return RaidProblem.InvalidParameters;
        }

        if (Level is RaidLevel.Raid0 or RaidLevel.Raid5 && (StripeSize is < MinStripeSize or > MaxStripeSize || (StripeSize & (StripeSize - 1)) != 0))
        {
            return RaidProblem.InvalidStripeSize;
        }

        return Level switch
        {
            RaidLevel.Jbod or RaidLevel.Raid0 when count < (Level == RaidLevel.Jbod ? 1 : 2) => RaidProblem.TooFewMembers,
            RaidLevel.Jbod or RaidLevel.Raid0 when missing > 0 => RaidProblem.MissingNotAllowed,
            RaidLevel.Raid1 when count < 1 || UseMember < 0 || UseMember >= count => RaidProblem.TooFewMembers,
            RaidLevel.Raid1 when Members[UseMember].Missing => RaidProblem.MissingNotAllowed,
            RaidLevel.Raid5 when count < 3 => RaidProblem.TooFewMembers,
            RaidLevel.Raid5 when missing > 1 => RaidProblem.TooManyMissing,
            _ => null,
        };
    }
}

/// <summary>RAID の構成の問題 (FOR-26 の「エラー」)。</summary>
public enum RaidProblem
{
    InvalidParameters,
    InvalidStripeSize,
    TooFewMembers,
    MissingNotAllowed,

    /// <summary>RAID 5 で 2 台以上欠けている (開かない)。</summary>
    TooManyMissing,
}

public sealed class RaidConfigException(RaidProblem problem) : InvalidOperationException($"The RAID configuration cannot be opened: {problem}.")
{
    public RaidProblem Problem { get; } = problem;
}

/// <summary>
/// RAID のメンバーを組み合わせた、読み取り専用の論理ディスク (FOR-26 の仕様 2・4)。要求された範囲だけをメンバーから読む。
/// RAID 5 で欠けたメンバーのブロックは、残りのメンバーの同じ位置の XOR で求める。メンバーの大きさが違う場合は最小の大きさに合わせる
/// (<see cref="SizesDiffer"/>)。
/// </summary>
public sealed class RaidByteSource : ByteSourceBase, View.IViewDefaultsSource
{
    private readonly RaidConfig _config;
    private readonly IByteSource?[] _members;
    private readonly long _memberSize;
    private readonly long[] _jbodStarts;
    private readonly bool _ownsMembers;

    /// <param name="members">構成の順のメンバー (欠けているメンバーは null)。</param>
    public RaidByteSource(RaidConfig config, IReadOnlyList<IByteSource?> members, string displayName = "RAID", bool ownsMembers = false)
    {
        if (config.Validate() is { } problem)
        {
            throw new RaidConfigException(problem);
        }

        if (members.Count != config.Members.Count)
        {
            throw new ArgumentException("The number of member sources does not match the configuration.", nameof(members));
        }

        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] is null && !config.Members[i].Missing)
            {
                throw new RaidConfigException(RaidProblem.MissingNotAllowed);
            }
        }

        _config = config;
        _members = [.. members];
        _ownsMembers = ownsMembers;
        DisplayName = displayName;
        long[] usable = [.. members.Select((m, i) => m is null ? -1 : Math.Max(0, m.Length - config.Members[i].DataOffset))];
        long min = usable.Where(u => u >= 0).DefaultIfEmpty(0).Min();
        SizesDiffer = usable.Where(u => u >= 0).Distinct().Count() > 1;
        long stripe = config.StripeSize;
        int n = members.Count;
        switch (config.Level)
        {
            case RaidLevel.Jbod:
                _jbodStarts = new long[n];
                long at = 0;
                for (int i = 0; i < n; i++)
                {
                    _jbodStarts[i] = at;
                    at += usable[i];
                }

                Length = at;
                SizesDiffer = false;
                break;
            case RaidLevel.Raid0:
                _memberSize = min / stripe * stripe;
                Length = _memberSize * n;
                _jbodStarts = [];
                break;
            case RaidLevel.Raid1:
                _memberSize = usable[config.UseMember];
                Length = _memberSize;
                SizesDiffer = false;
                _jbodStarts = [];
                break;
            default:
                _memberSize = min / stripe * stripe;
                Length = _memberSize * (n - 1);
                _jbodStarts = [];
                break;
        }

        Identity = "raid:" + Guid.NewGuid().ToString("N");
    }

    public RaidConfig Config => _config;

    /// <summary>メンバーの大きさが違い、最小の大きさに合わせた (警告)。</summary>
    public bool SizesDiffer { get; }

    public System.Text.Json.Nodes.JsonObject? ViewDefaults => new() { ["separator"] = "sector" };

    public override string DisplayName { get; }

    public override string Identity { get; }

    public override long Length { get; }

    public override int LogicalSectorSize => _config.SectorSize;

    public override SourceCapabilities Capabilities => SourceCapabilities.None;

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        List<UnreadableRange>? bad = null;
        int done = 0;
        while (done < count)
        {
            long pos = offset + done;
            int n = _config.Level switch
            {
                RaidLevel.Jbod => ReadJbod(pos, buffer[done..count], ref bad),
                RaidLevel.Raid1 => ReadMember(_config.UseMember, pos, buffer[done..count], pos, ref bad),
                RaidLevel.Raid0 => ReadRaid0(pos, buffer[done..count], ref bad),
                _ => ReadRaid5(pos, buffer[done..count], ref bad),
            };
            done += n;
        }

        return new ReadResult(count, bad);
    }

    private int ReadJbod(long pos, Span<byte> buffer, ref List<UnreadableRange>? bad)
    {
        int index = Array.BinarySearch(_jbodStarts, pos);
        if (index < 0)
        {
            index = ~index - 1;
        }

        while (index < _jbodStarts.Length - 1 && pos >= _jbodStarts[index + 1])
        {
            index++;
        }

        long inMember = pos - _jbodStarts[index];
        long memberLength = (index + 1 < _jbodStarts.Length ? _jbodStarts[index + 1] : Length) - _jbodStarts[index];
        int n = (int)Math.Min(buffer.Length, memberLength - inMember);
        return ReadMember(index, inMember, buffer[..n], pos, ref bad);
    }

    private int ReadRaid0(long pos, Span<byte> buffer, ref List<UnreadableRange>? bad)
    {
        long stripe = _config.StripeSize;
        int n = _members.Length;
        long chunk = pos / stripe;
        long within = pos % stripe;
        int member = (int)(chunk % n);
        long row = chunk / n;
        int len = (int)Math.Min(buffer.Length, stripe - within);
        return ReadMember(member, row * stripe + within, buffer[..len], pos, ref bad);
    }

    /// <summary>RAID 5 の論理位置を、データのメンバー・パリティのメンバー・メンバーの中の位置に変える (Linux md の raid5_compute_sector と同じ)。</summary>
    public static (int DataMember, int ParityMember, long MemberOffset) Map(long pos, int members, long stripe, Raid5Layout layout)
    {
        int dataDisks = members - 1;
        long chunk = pos / stripe;
        long within = pos % stripe;
        long row = chunk / dataDisks;
        int d = (int)(chunk % dataDisks);
        int rowMod = (int)(row % members);
        int parity;
        int data;
        switch (layout)
        {
            case Raid5Layout.LeftAsymmetric:
                parity = dataDisks - rowMod;
                data = d >= parity ? d + 1 : d;
                break;
            case Raid5Layout.RightAsymmetric:
                parity = rowMod;
                data = d >= parity ? d + 1 : d;
                break;
            case Raid5Layout.RightSymmetric:
                parity = rowMod;
                data = (parity + 1 + d) % members;
                break;
            default:
                parity = dataDisks - rowMod;
                data = (parity + 1 + d) % members;
                break;
        }

        return (data, parity, row * stripe + within);
    }

    private int ReadRaid5(long pos, Span<byte> buffer, ref List<UnreadableRange>? bad)
    {
        long stripe = _config.StripeSize;
        (int data, _, long memberOffset) = Map(pos, _members.Length, stripe, _config.Layout);
        int len = (int)Math.Min(buffer.Length, stripe - pos % stripe);
        Span<byte> part = buffer[..len];
        if (_members[data] is not null)
        {
            return ReadMember(data, memberOffset, part, pos, ref bad);
        }

        // 欠けたメンバー: 残りのメンバー (データとパリティ) の同じ位置の XOR。
        part.Clear();
        byte[] other = new byte[len];
        for (int m = 0; m < _members.Length; m++)
        {
            if (m == data)
            {
                continue;
            }

            ReadMember(m, memberOffset, other, pos, ref bad);
            for (int i = 0; i < len; i++)
            {
                part[i] ^= other[i];
            }
        }

        return len;
    }

    private int ReadMember(int member, long memberOffset, Span<byte> buffer, long logical, ref List<UnreadableRange>? bad)
    {
        IByteSource? source = _members[member];
        if (source is null)
        {
            buffer.Clear();
            (bad ??= []).Add(new UnreadableRange(logical, buffer.Length, UnreadableReason.Unallocated));
            return buffer.Length;
        }

        ReadResult r = source.Read(_config.Members[member].DataOffset + memberOffset, buffer);
        long delta = logical - (_config.Members[member].DataOffset + memberOffset);
        if (!r.IsComplete)
        {
            (bad ??= []).AddRange(r.Unreadable.Select(u => u with { Offset = u.Offset + delta }));
        }

        if (r.BytesReturned < buffer.Length)
        {
            buffer[r.BytesReturned..].Clear();
            (bad ??= []).Add(new UnreadableRange(logical + r.BytesReturned, buffer.Length - r.BytesReturned, UnreadableReason.IoError));
        }

        return buffer.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsMembers)
        {
            foreach (IByteSource? m in _members)
            {
                m?.Dispose();
            }
        }
    }
}
