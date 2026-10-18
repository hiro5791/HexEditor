using System.IO.Compression;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>
/// .ksy の機能ごとの小さな確認 (TPL-33)。TC-TPL-33-01 (CI だけの kaitai_struct_formats との照合) で見つかった違いを、手で書いた小さな
/// .ksy とデータで、ダウンロードなしに毎回確かめる。期待値は Kaitai Struct の公式の実行時ライブラリと同じ読み方。
/// </summary>
public sealed class KaitaiEngineTests
{
    private static TemplateResult RunKsyText(string ksy, byte[] data, out TemplateProgram program)
    {
        program = TemplateLoader.LoadText(ksy, "test.ksy", Path.GetTempPath());
        Assert.False(program.HasErrors, string.Join("; ", program.Diagnostics) + "\n" + program.ConvertedHxt);
        TemplateResult r = Run(program, TemplateDataSource.FromBytes(data));
        r.EvaluateAll();
        Assert.True(r.State == TemplateRunState.Completed, string.Join("; ", r.Diagnostics) + "\n" + program.ConvertedHxt);
        return r;
    }

    private static TemplateResult RunKsyText(string ksy, byte[] data) => RunKsyText(ksy, data, out _);

    [Fact]
    public void LittleEndianBitFieldsCrossByteBoundaries()
    {
        // dos_datetime の time (b5 b6 b5、bit-endian: le) と zip の gp_flags。12:00:00 は 0x6000 (00 60)
        const string ksy = """
            meta:
              id: bits_le
              endian: le
              bit-endian: le
            seq:
              - id: time
                type: time
              - id: after
                type: u1
            types:
              time:
                seq:
                  - id: second_div_2
                    type: b5
                  - id: minute
                    type: b6
                  - id: hour
                    type: b5
            """;
        TemplateResult r = RunKsyText(ksy, Hex("00 60 AB"), out TemplateProgram p);
        Assert.True(Long(r, "time.hour") == 12, p.ConvertedHxt);
        Assert.Equal(0L, Long(r, "time.minute"));
        Assert.Equal(0L, Long(r, "time.second_div_2"));
        Assert.Equal(2L, Node(r, "time").Size);
        Assert.Equal(2L, Node(r, "after").Offset);
        Assert.Equal(0xABL, Long(r, "after"));
    }

    [Fact]
    public void BitFieldsFollowedByAByteFieldAlignToTheNextByte()
    {
        // zip の gp_flags と同じ並び (b1 b2 b1 b1 b1 b1 b4 b1 b1 b1 b2 = 16 ビット)。b4 (ビット 7〜10) はバイトをまたぐ
        const string ksy = """
            meta:
              id: gp
              endian: le
              bit-endian: le
            seq:
              - id: file_encrypted
                type: b1
              - id: comp_options_raw
                type: b2
              - id: has_data_descriptor
                type: b1
              - id: reserved_1
                type: b1
              - id: comp_patched_data
                type: b1
              - id: strong_encrypt
                type: b1
              - id: reserved_2
                type: b4
              - id: lang_encoding
                type: b1
              - id: reserved_3
                type: b1
              - id: mask_header_values
                type: b1
              - id: reserved_4
                type: b2
              - id: tail
                type: b3
              - id: next
                type: u1
            """;
        // ビット 0〜15 = 0x0F80: reserved_2 (ビット 7〜10) = 0xF、lang_encoding (ビット 11) = 1。ビット 16〜18 (tail) = 5
        TemplateResult r = RunKsyText(ksy, Hex("80 0F 05 7E"));
        Assert.Equal(0L, Long(r, "file_encrypted"));
        Assert.Equal(0L, Long(r, "comp_options_raw"));
        Assert.Equal(0L, Long(r, "strong_encrypt"));
        Assert.Equal(0xFL, Long(r, "reserved_2"));
        Assert.Equal(1L, Long(r, "lang_encoding"));
        Assert.Equal(0L, Long(r, "reserved_3"));
        Assert.Equal(0L, Long(r, "reserved_4"));
        Assert.Equal(5L, Long(r, "tail"));
        Assert.Equal(3L, Node(r, "next").Offset);
        Assert.Equal(0x7EL, Long(r, "next"));
    }

    [Fact]
    public void ProcessWithSizeEosUsesTheRestOfTheStream()
    {
        // png の zTXt と同じ書き方 (process: zlib と size-eos: true)。大きさのある型の中なので、その型の終わりまでを展開する
        const string ksy = """
            meta:
              id: ztxt
              endian: le
            seq:
              - id: len_chunk
                type: u1
              - id: chunk
                type: chunk
                size: len_chunk
              - id: after
                type: u1
            types:
              chunk:
                seq:
                  - id: method
                    type: u1
                  - id: text
                    process: zlib
                    size-eos: true
            """;
        byte[] plain = "Hello, Kaitai!"u8.ToArray();
        byte[] packed = Zlib(plain);
        byte[] data = [(byte)(packed.Length + 1), 0, .. packed, 0x99];
        TemplateResult r = RunKsyText(ksy, data);
        // 展開したバイト列はセクション (TPL-15) に置く
        TemplateNode text = Find(r.Root, "text") ?? throw new Xunit.Sdk.XunitException("text がない");
        Assert.Equal(plain.Length, text.Size);
        Assert.Equal(plain, text.Children.Select(c => (byte)Convert.ToInt64(c.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        Assert.Equal(0x99L, Long(r, "after"));
        Assert.Equal(packed.Length + 2L, Node(r, "after").Offset);
    }

    private static TemplateNode? Find(TemplateNode node, string name)
    {
        foreach (TemplateNode child in node.Children)
        {
            if (child.Name == name)
            {
                return child;
            }

            if (child.Kind is not TemplateNodeKind.Array && Find(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static byte[] Zlib(byte[] plain)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(plain);
        }

        return ms.ToArray();
    }

    [Fact]
    public void SeqCanReferToInstancesDeclaredAfterIt()
    {
        // gif の logical_screen_descriptor / local_image_descriptor と同じ: seq の if・size が、後に書いた instances (value) を使う。
        // 子の型からの _parent.… も同じ
        const string ksy = """
            meta:
              id: fwd
              endian: le
            seq:
              - id: options
                type: u1
              - id: table
                size: table_size * 3
                if: has_table
              - id: child
                type: child
            instances:
              has_table:
                value: (options & 0x80) != 0
              table_size:
                value: 2 << (options & 7)
            types:
              child:
                seq:
                  - id: extra
                    type: u1
                    if: _parent.has_table
                  - id: tail
                    type: u1
            """;
        TemplateResult r = RunKsyText(ksy, Hex("80 01 02 03 04 05 06 AA BB"));
        Assert.Equal(6L, Node(r, "table").Size);
        Assert.Equal(0xAAL, Long(r, "child.extra"));
        Assert.Equal(0xBBL, Long(r, "child.tail"));
        Assert.Equal(true, Value(r, "has_table"));

        TemplateResult none = RunKsyText(ksy, Hex("00 CC"));
        Assert.Null(none.FindByPath("table"));
        Assert.Null(none.FindByPath("child.extra"));
        Assert.Equal(0xCCL, Long(none, "child.tail"));
    }

    [Fact]
    public void InstancesWithoutPosReadAtTheEndOfTheSeq()
    {
        // elf の program_header の flags_obj と同じ: pos のない instances は、Kaitai Struct ではストリームの今の位置 (seq を読み終えた
        // あとならその末尾) から読む
        const string ksy = """
            meta:
              id: nopos
              endian: le
            seq:
              - id: header
                type: header
            types:
              header:
                seq:
                  - id: kind
                    type: u2
                  - id: flags
                    type: u2
                instances:
                  flags_obj:
                    type: flag_bits(flags)
                  trailer:
                    type: u1
              flag_bits:
                params:
                  - id: value
                    type: u4
                instances:
                  read:
                    value: (value & 4) != 0
            """;
        TemplateResult r = RunKsyText(ksy, Hex("01 00 04 00 7F"));
        Assert.Equal(4L, Node(r, "header.flags_obj").Offset);
        Assert.Equal(true, Value(r, "header.flags_obj.read"));
        Assert.Equal(4L, Node(r, "header.trailer").Offset);
        Assert.Equal(0x7FL, Long(r, "header.trailer"));
    }
}
