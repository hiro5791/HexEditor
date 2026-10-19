using HexEditor.Core.FileSystems;
using HexEditor.Core.FileSystems.Volumes;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images.Raid;

/// <summary>
/// LDM のミラー・RAID-5 のボリューム (FOR-07) を、RAID の再構成 (FOR-26) の論理ディスクとして組み立てる。構成要素は、各ディスクの
/// 論理ボリュームの部分 (<see cref="LogicalVolumeRaidRequest.Members"/> の位置と長さ) だけを先頭 0 として渡す。
/// </summary>
public static class LogicalVolumeRaid
{
    /// <summary>RAID の設定 (メンバーの <see cref="RaidMember.Source"/> は構成要素の ID。見つからない構成要素は欠けているものとする)。</summary>
    public static RaidConfig ConfigOf(LogicalVolumeRaidRequest request, IReadOnlyDictionary<string, IVolumeReader> readers)
    {
        bool mirror = string.Equals(request.Layout, "Mirror", StringComparison.OrdinalIgnoreCase);
        List<RaidMember> members = [.. request.Members.Select(m => new RaidMember { Source = m.MemberId, Missing = !readers.ContainsKey(m.MemberId) })];
        int use = Math.Max(0, members.FindIndex(m => !m.Missing));
        return new RaidConfig
        {
            Level = mirror ? RaidLevel.Raid1 : RaidLevel.Raid5,
            Members = members,
            StripeSize = request.StripeSize > 0 ? request.StripeSize : RaidConfig.DefaultStripeSize,
            Layout = Enum.TryParse(request.Layout, ignoreCase: true, out Raid5Layout layout) ? layout : Raid5Layout.LeftAsymmetric,
            UseMember = use,
        };
    }

    /// <summary>論理ディスクを作る。設定に問題があれば <see cref="RaidConfigException"/>。</summary>
    public static RaidByteSource Open(LogicalVolumeRaidRequest request, IReadOnlyDictionary<string, IVolumeReader> readers)
    {
        RaidConfig config = ConfigOf(request, readers);
        IByteSource?[] sources = [.. request.Members.Select(m => readers.TryGetValue(m.MemberId, out IVolumeReader? r)
            ? new FileSystemContentSource(r.Slice(m.MemberOffset, Math.Min(m.Length, Math.Max(0, r.Length - m.MemberOffset))), m.MemberId,
                $"lvraid:{request.VolumeName}:{m.MemberId}:{m.MemberOffset}")
            : (IByteSource?)null)];
        return new RaidByteSource(config, sources, request.VolumeName);
    }
}
