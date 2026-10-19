using System.Text;
using static HexEditor.TestData.FileSystemImages.ImageBytes;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>
/// ISO 9660 のイメージを直接書く生成ツール (TD-FOR-ISO-JRR)。基本ボリューム記述子・El Torito のブートレコード・Joliet の補助ボリューム記述子・終端、
/// パス表 (L と M)、ディレクトリ (基本と Joliet の 2 つの木)、Rock Ridge (SUSP の SP・CE・ER・NM・PX・TF) を書く。基本の名前は大文字の 8.3
/// (使えない文字は <c>_</c>) に <c>;1</c> を付け、Joliet では <c>:</c> を <c>_</c> にする (xorriso と同じ置き換え)。<see cref="StartSector"/> を
/// 指定すると、その位置から始まるセッションを書く (複数セッションのテスト)。アプリの解析の実装は使わない。
/// </summary>
public sealed class IsoImageBuilder
{
    public const int Sector = 2048;

    public sealed class Node
    {
        public required string Name { get; init; }
        public Node? Parent { get; init; }
        public bool IsDirectory { get; init; }
        public byte[] Content { get; init; } = [];
        public DateTime Modified { get; init; }
        public int Mode { get; init; }
        public List<Node> Children { get; } = [];
        public long Extent { get; set; }
        public long Length { get; set; }
        public long JolietExtent { get; set; }
        public long JolietLength { get; set; }
        public int DirectoryNumber { get; set; }
        public int JolietNumber { get; set; }

        public string Path => Parent is null ? "\\" : (Parent.Parent is null ? "\\" : Parent.Path + "\\") + Name;
    }

    private readonly string _volumeId;
    private readonly DateTime _created;

    public IsoImageBuilder(string volumeId, DateTime created, long startSector = 0)
    {
        _volumeId = volumeId;
        _created = created;
        StartSector = startSector;
        Root = new Node { Name = string.Empty, IsDirectory = true, Modified = created, Mode = 0x41ED };
    }

    public Node Root { get; }

    public long StartSector { get; }

    /// <summary>JST (+9:00) で記録する (ディレクトリレコードの時差の値 36)。</summary>
    public sbyte ZoneQuarterHours { get; init; } = 36;

    public bool Joliet { get; init; } = true;

    public bool RockRidge { get; init; } = true;

    /// <summary>El Torito のブートイメージ (エミュレーションなし、4 セクタ (512 バイト単位))。null ならブートレコードを書かない。</summary>
    public Node? BootImage { get; set; }

    public long BootCatalogSector { get; private set; }

    public long VolumeSpaceSize { get; private set; }

    public Node AddFile(Node dir, string name, byte[] content, DateTime modified)
    {
        var n = new Node { Name = name, Parent = dir, Content = content, Modified = modified, Mode = 0x81A4 };
        dir.Children.Add(n);
        return n;
    }

    public Node AddDirectory(Node dir, string name, DateTime modified)
    {
        var n = new Node { Name = name, Parent = dir, IsDirectory = true, Modified = modified, Mode = 0x41ED };
        dir.Children.Add(n);
        return n;
    }

    /// <summary>基本の名前 (ISO 9660 レベル 1: 大文字の 8.3 に <c>;1</c>)。</summary>
    public static string BasicName(Node n)
    {
        static string Clean(string s, int max)
        {
            var b = new StringBuilder();
            foreach (char c in s.ToUpperInvariant())
            {
                b.Append(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' ? c : '_');
            }

            return b.Length > max ? b.ToString(0, max) : b.ToString();
        }

        int dot = n.Name.LastIndexOf('.');
        if (n.IsDirectory || dot < 0)
        {
            return Clean(n.Name, 8) + (n.IsDirectory ? string.Empty : ".;1");
        }

        return Clean(n.Name[..dot], 8) + "." + Clean(n.Name[(dot + 1)..], 3) + ";1";
    }

    /// <summary>Joliet の名前 (<c>:</c> などの使えない文字を <c>_</c> にし、ファイルは <c>;1</c> を付ける)。</summary>
    public static string JolietName(Node n)
    {
        var b = new StringBuilder();
        foreach (char c in n.Name)
        {
            b.Append(c is '*' or '/' or ':' or ';' or '?' or '\\' ? '_' : c);
        }

        return b + (n.IsDirectory ? string.Empty : ";1");
    }

    private IEnumerable<Node> All(Node dir)
    {
        foreach (Node c in dir.Children)
        {
            yield return c;
            if (c.IsDirectory)
            {
                foreach (Node d in All(c))
                {
                    yield return d;
                }
            }
        }
    }

    private List<Node> Directories()
    {
        // パス表の順 (幅優先)。
        var list = new List<Node> { Root };
        for (int i = 0; i < list.Count; i++)
        {
            list.AddRange(list[i].Children.Where(c => c.IsDirectory).OrderBy(c => BasicName(c), StringComparer.Ordinal));
        }

        return list;
    }

    // ---- 書き込み ----

    public SparseImage Build(SparseImage? into = null, long totalSectors = 512)
    {
        long s0 = StartSector;
        long pvd = s0 + 16;
        long next = pvd + 1;
        long bootRecord = BootImage is not null ? next++ : -1;
        long svd = Joliet ? next++ : -1;
        long terminator = next++;
        BootCatalogSector = BootImage is not null ? next++ : -1;
        List<Node> dirs = Directories();
        for (int i = 0; i < dirs.Count; i++)
        {
            dirs[i].DirectoryNumber = i + 1;
        }

        long lPath = next, mPath = next + 1;
        next += 2;
        long jlPath = Joliet ? next : -1, jmPath = Joliet ? next + 1 : -1;
        if (Joliet)
        {
            next += 2;
        }

        // ディレクトリの大きさは中身のレコードの合計 (セクタ単位)。
        foreach (Node d in dirs)
        {
            d.Length = SectorsOf(DirRecords(d, joliet: false, extentsKnown: false, RockRidge ? 0 : -1)) * Sector;
            d.Extent = next;
            next += d.Length / Sector;
        }

        if (Joliet)
        {
            foreach (Node d in dirs)
            {
                d.JolietLength = SectorsOf(DirRecords(d, joliet: true, extentsKnown: false)) * Sector;
                d.JolietExtent = next;
                next += d.JolietLength / Sector;
            }
        }

        long ceSector = RockRidge ? next++ : -1;
        foreach (Node f in All(Root).Where(n => !n.IsDirectory))
        {
            f.Length = f.Content.Length;
            f.Extent = f.Content.Length == 0 ? 0 : next;
            f.JolietExtent = f.Extent;
            f.JolietLength = f.Length;
            next += (f.Content.Length + Sector - 1) / Sector;
        }

        VolumeSpaceSize = Math.Max(next, s0 + totalSectors);
        SparseImage image = into ?? new SparseImage(VolumeSpaceSize * Sector);

        // ボリューム記述子。
        image.Write(pvd * Sector, VolumeDescriptor(1, dirs[0], lPath, mPath, PathTable(dirs, false, false).Length));
        if (BootImage is not null)
        {
            byte[] br = new byte[Sector];
            br[0] = 0;
            Ascii(br, 1, "CD001");
            br[6] = 1;
            Ascii(br, 7, "EL TORITO SPECIFICATION");
            U32(br, 0x47, BootCatalogSector);
            image.Write(bootRecord * Sector, br);
            image.Write(BootCatalogSector * Sector, BootCatalog());
        }

        if (Joliet)
        {
            image.Write(svd * Sector, VolumeDescriptor(2, dirs[0], jlPath, jmPath, PathTable(dirs, true, false).Length));
        }

        byte[] term = new byte[Sector];
        term[0] = 255;
        Ascii(term, 1, "CD001");
        term[6] = 1;
        image.Write(terminator * Sector, term);

        image.Write(lPath * Sector, PathTable(dirs, false, false));
        image.Write(mPath * Sector, PathTable(dirs, false, true));
        if (Joliet)
        {
            image.Write(jlPath * Sector, PathTable(dirs, true, false));
            image.Write(jmPath * Sector, PathTable(dirs, true, true));
        }

        foreach (Node d in dirs)
        {
            image.Write(d.Extent * Sector, Pack(DirRecords(d, false, true, ceSector), d.Length));
            if (Joliet)
            {
                image.Write(d.JolietExtent * Sector, Pack(DirRecords(d, true, true), d.JolietLength));
            }
        }

        if (RockRidge)
        {
            // CE で続きにした ER (拡張の名前)。
            image.Write(ceSector * Sector, ErEntry());
        }

        foreach (Node f in All(Root).Where(n => !n.IsDirectory && n.Content.Length > 0))
        {
            image.Write(f.Extent * Sector, f.Content);
        }

        return image;
    }

    private static long SectorsOf(List<byte[]> records)
    {
        long sectors = 1;
        int used = 0;
        foreach (byte[] r in records)
        {
            if (used + r.Length > Sector)
            {
                sectors++;
                used = 0;
            }

            used += r.Length;
        }

        return sectors;
    }

    private static byte[] Pack(List<byte[]> records, long length)
    {
        byte[] data = new byte[length];
        int sector = 0, used = 0;
        foreach (byte[] r in records)
        {
            if (used + r.Length > Sector)
            {
                sector++;
                used = 0;
            }

            r.CopyTo(data, sector * Sector + used);
            used += r.Length;
        }

        return data;
    }

    private byte[] ErEntry()
    {
        byte[] id = Encoding.ASCII.GetBytes("RRIP_1991A");
        byte[] desc = Encoding.ASCII.GetBytes("THE ROCK RIDGE INTERCHANGE PROTOCOL PROVIDES SUPPORT FOR POSIX FILE SYSTEM SEMANTICS");
        byte[] src = Encoding.ASCII.GetBytes("PLEASE CONTACT DISC PUBLISHER FOR SPECIFICATION SOURCE.");
        byte[] er = new byte[8 + id.Length + desc.Length + src.Length];
        Ascii(er, 0, "ER");
        er[2] = (byte)er.Length;
        er[3] = 1;
        er[4] = (byte)id.Length;
        er[5] = (byte)desc.Length;
        er[6] = (byte)src.Length;
        er[7] = 1;
        id.CopyTo(er, 8);
        desc.CopyTo(er, 8 + id.Length);
        src.CopyTo(er, 8 + id.Length + desc.Length);
        byte[] sector = new byte[Sector];
        er.CopyTo(sector, 0);
        return sector;
    }

    private List<byte[]> DirRecords(Node dir, bool joliet, bool extentsKnown, long ceSector = -1)
    {
        var list = new List<byte[]>
        {
            Record(dir, [0], joliet, dir == Root && !joliet && RockRidge ? RootSusp(dir, ceSector) : Susp(dir, null, joliet)),
            Record(dir.Parent ?? dir, [1], joliet, Susp(dir.Parent ?? dir, null, joliet)),
        };
        IEnumerable<Node> children = joliet
            ? dir.Children.OrderBy(c => JolietName(c), StringComparer.Ordinal)
            : dir.Children.OrderBy(c => BasicName(c), StringComparer.Ordinal);
        foreach (Node c in children)
        {
            byte[] name = joliet ? Encoding.BigEndianUnicode.GetBytes(JolietName(c)) : Encoding.ASCII.GetBytes(BasicName(c));
            list.Add(Record(c, name, joliet, Susp(c, c.Name, joliet)));
        }

        _ = extentsKnown;
        return list;
    }

    private byte[] RootSusp(Node root, long ceSector)
    {
        var b = new List<byte>();
        b.AddRange([(byte)'S', (byte)'P', 7, 1, 0xBE, 0xEF, 0]);
        b.AddRange(Susp(root, null, false));
        if (ceSector >= 0)
        {
            byte[] ce = new byte[28];
            Ascii(ce, 0, "CE");
            ce[2] = 28;
            ce[3] = 1;
            Both32(ce, 4, ceSector);
            Both32(ce, 12, 0);
            Both32(ce, 20, ErEntry().AsSpan().TrimEnd((byte)0).Length);
            b.AddRange(ce);
        }

        return [.. b];
    }

    private byte[] Susp(Node n, string? name, bool joliet)
    {
        if (joliet || !RockRidge)
        {
            return [];
        }

        var b = new List<byte>();
        byte[] px = new byte[36];
        Ascii(px, 0, "PX");
        px[2] = 36;
        px[3] = 1;
        Both32(px, 4, n.Mode);
        Both32(px, 12, n.IsDirectory ? 2 : 1);
        Both32(px, 20, 0);
        Both32(px, 28, 0);
        b.AddRange(px);
        byte[] tf = new byte[5 + 3 * 7];
        Ascii(tf, 0, "TF");
        tf[2] = (byte)tf.Length;
        tf[3] = 1;
        tf[4] = 0x02 | 0x04 | 0x08;
        for (int i = 0; i < 3; i++)
        {
            Date(n.Modified).CopyTo(tf, 5 + i * 7);
        }

        b.AddRange(tf);
        if (name is not null)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(name);
            byte[] nm = new byte[5 + utf8.Length];
            Ascii(nm, 0, "NM");
            nm[2] = (byte)nm.Length;
            nm[3] = 1;
            utf8.CopyTo(nm, 5);
            b.AddRange(nm);
        }

        return [.. b];
    }

    private byte[] Date(DateTime utc)
    {
        DateTime local = utc.AddMinutes(ZoneQuarterHours * 15);
        return [(byte)(local.Year - 1900), (byte)local.Month, (byte)local.Day, (byte)local.Hour, (byte)local.Minute, (byte)local.Second, (byte)ZoneQuarterHours];
    }

    private byte[] Record(Node n, byte[] name, bool joliet, byte[] systemUse)
    {
        int len = 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0) + systemUse.Length;
        len += len % 2;
        byte[] r = new byte[len];
        r[0] = (byte)len;
        Both32(r, 2, joliet ? n.JolietExtent : n.Extent);
        Both32(r, 10, joliet ? n.JolietLength : n.Length);
        Date(n.Modified).CopyTo(r, 18);
        r[25] = (byte)(n.IsDirectory ? 2 : 0);
        Both16(r, 28, 1);
        r[32] = (byte)name.Length;
        name.CopyTo(r, 33);
        systemUse.CopyTo(r, 33 + name.Length + (name.Length % 2 == 0 ? 1 : 0));
        return r;
    }

    private byte[] PathTable(List<Node> dirs, bool joliet, bool bigEndian)
    {
        var b = new List<byte>();
        foreach (Node d in dirs)
        {
            byte[] id = d == Root ? [0] : joliet ? Encoding.BigEndianUnicode.GetBytes(JolietName(d)) : Encoding.ASCII.GetBytes(BasicName(d));
            byte[] e = new byte[8 + id.Length + id.Length % 2];
            e[0] = (byte)id.Length;
            long extent = joliet ? d.JolietExtent : d.Extent;
            int parent = d.Parent?.DirectoryNumber ?? 1;
            if (bigEndian)
            {
                B32(e, 2, extent);
                B16(e, 6, parent);
            }
            else
            {
                U32(e, 2, extent);
                U16(e, 6, parent);
            }

            id.CopyTo(e, 8);
            b.AddRange(e);
        }

        return [.. b];
    }

    private byte[] VolumeDescriptor(int type, Node root, long lPath, long mPath, int pathTableSize)
    {
        bool joliet = type == 2;
        byte[] d = new byte[Sector];
        d[0] = (byte)type;
        Ascii(d, 1, "CD001");
        d[6] = 1;
        Text(d, 8, 32, "LINUX", joliet);
        Text(d, 40, 32, _volumeId, joliet);
        Both32(d, 80, VolumeSpaceSize);
        if (joliet)
        {
            d[88] = 0x25;
            d[89] = 0x2F;
            d[90] = 0x45;
        }

        Both16(d, 120, 1);
        Both16(d, 124, 1);
        Both16(d, 128, Sector);
        Both32(d, 132, pathTableSize);
        U32(d, 140, lPath);
        B32(d, 148, mPath);
        Record(root, [0], joliet, []).AsSpan(0, 34).CopyTo(d.AsSpan(156));
        d[156] = 34;
        Text(d, 190, 128, string.Empty, joliet);
        Text(d, 318, 128, string.Empty, joliet);
        Text(d, 446, 128, string.Empty, joliet);
        Text(d, 574, 128, "HEXEDITOR TEST DATA", joliet);
        Text(d, 702, 37, string.Empty, joliet);
        Text(d, 739, 37, string.Empty, joliet);
        Text(d, 776, 37, string.Empty, joliet);
        DecDate(_created).CopyTo(d, 813);
        DecDate(_created).CopyTo(d, 830);
        Ascii(d, 847, "0000000000000000");
        Ascii(d, 864, "0000000000000000");
        d[881] = 1;
        return d;
    }

    private static void Text(byte[] d, int at, int length, string s, bool joliet)
    {
        if (joliet)
        {
            for (int i = 0; i + 1 < length; i += 2)
            {
                char c = i / 2 < s.Length ? s[i / 2] : ' ';
                d[at + i] = (byte)(c >> 8);
                d[at + i + 1] = (byte)c;
            }

            return;
        }

        for (int i = 0; i < length; i++)
        {
            d[at + i] = (byte)(i < s.Length ? s[i] : ' ');
        }
    }

    private static byte[] DecDate(DateTime utc)
    {
        byte[] b = new byte[17];
        Ascii(b, 0, utc.ToString("yyyyMMddHHmmss00", System.Globalization.CultureInfo.InvariantCulture));
        b[16] = 0;
        return b;
    }

    private byte[] BootCatalog()
    {
        byte[] c = new byte[Sector];
        c[0] = 1;
        c[1] = 0;
        Ascii(c, 4, "HexEditor");
        c[30] = 0x55;
        c[31] = 0xAA;
        int sum = 0;
        for (int i = 0; i < 32; i += 2)
        {
            sum += c[i] | c[i + 1] << 8;
        }

        U16(c, 28, (0x10000 - (sum & 0xFFFF)) & 0xFFFF);
        c[32] = 0x88;
        c[33] = 0;
        U16(c, 34, 0);
        c[36] = 0;
        U16(c, 38, 4);
        U32(c, 40, BootImage!.Extent);
        return c;
    }
}
