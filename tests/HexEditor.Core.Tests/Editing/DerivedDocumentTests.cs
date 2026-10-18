using System.Buffers.Binary;
using System.IO.Compression;
using HexEditor.Core.Editing.Codecs;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;
using CipherMode = HexEditor.Core.Editing.Codecs.CipherMode;

namespace HexEditor.Core.Tests.Editing;

/// <summary>EDIT-46 派生ドキュメント (範囲の追跡、変更の検知、書き戻しの内容と 1 つの編集グループ)。</summary>
public sealed class DerivedDocumentTests
{
    /// <summary>PNG の IDAT チャンクのデータ部 (zlib) の開始位置と長さ。</summary>
    public static (long Start, long Length) Idat(byte[] png)
    {
        long at = 8;
        while (at + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan((int)at));
            string type = System.Text.Encoding.ASCII.GetString(png, (int)at + 4, 4);
            if (type == "IDAT")
            {
                return (at + 8, length);
            }

            at += 12 + length;
        }

        throw new InvalidDataException("IDAT がありません。");
    }

    [Fact]
    public void Idat_is_decompressed_edited_and_written_back_as_one_group()
    {
        byte[] png = TestDataCatalog.Png16();
        (long start, long length) = Idat(png);
        using var parent = new Document(new MemoryByteSource(png), Options());

        // 展開して派生ドキュメントを作る。
        DecompressionResult r = CompressionEngine.Decompress(parent.Current, start, length, CompressionFormat.Zlib, new CodecOutput(ContentSink.For(parent)));
        using var derived = new Document(MemoryByteSource.CreateEmpty("d"), Options());
        derived.InsertContent(0, r.Content!);
        derived.ClearHistory();
        using var link = new DerivedLink(parent, start, r.ConsumedInput, new DerivedTransform { Kind = DerivedKind.Decompressed, Format = CompressionFormat.Zlib, Level = 6 });

        // 1 バイト変えて書き戻す。
        derived.Overwrite(0x10, [0xFF]);
        Assert.False(link.RangeChanged());
        EditContent content = link.BuildWriteBack(derived.Current, ContentSink.For(parent), null, null);
        link.Apply(content, "書き戻し");

        // 元の範囲が再圧縮したデータに置き換わり、展開すると派生ドキュメントと一致する。
        byte[] after = ReadAll(parent.Current);
        using var z = new ZLibStream(new MemoryStream(after, (int)start, (int)link.Length), CompressionMode.Decompress);
        var ms = new MemoryStream();
        z.CopyTo(ms);
        Assert.Equal(ReadAll(derived.Current), ms.ToArray());

        // 1 回の Undo で元に戻る。
        parent.Undo();
        Assert.Equal(png, ReadAll(parent.Current));
    }

    [Fact]
    public void Edits_before_the_range_shift_it_and_edits_inside_mark_it_changed()
    {
        using var parent = new Document(new MemoryByteSource(new byte[100]), Options());
        using var link = new DerivedLink(parent, 40, 20, new DerivedTransform());
        parent.Insert(10, [1, 2, 3]);
        Assert.Equal(43, link.Offset);
        Assert.False(link.RangeChanged());
        parent.Overwrite(90, [7]);
        Assert.False(link.RangeChanged());
        parent.Overwrite(50, [9]);
        Assert.True(link.RangeChanged());
    }

    [Fact]
    public void Undo_of_an_unrelated_edit_keeps_the_range_unchanged()
    {
        using var parent = new Document(new MemoryByteSource(new byte[100]), Options());
        using var link = new DerivedLink(parent, 40, 20, new DerivedTransform());
        parent.Overwrite(90, [7]);
        parent.Undo();
        Assert.False(link.RangeChanged());
        parent.Overwrite(45, [7]);
        parent.Undo();
        parent.Redo();
        Assert.True(link.RangeChanged());
    }

    [Fact]
    public void Encrypted_write_back_uses_the_new_key_and_nonce()
    {
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] nonce = new byte[12];
        byte[] plain = "secret data"u8.ToArray();
        using var source = new Document(new MemoryByteSource(plain), Options());
        using var p = new CipherParameters { Algorithm = CipherAlgorithm.Aes256, Mode = CipherMode.Gcm, Key = (byte[])key.Clone(), Iv = (byte[])nonce.Clone() };
        byte[] cipher = CompressionTests.ReadContent(CipherEngine.Run(source.Current, 0, plain.Length, p, true, ContentSink.For(source)).Content);
        using var parent = new Document(new MemoryByteSource(cipher), Options());
        using var derived = new Document(new MemoryByteSource(plain), Options());
        using var link = new DerivedLink(parent, 0, cipher.Length,
            new DerivedTransform { Kind = DerivedKind.Decrypted, Algorithm = CipherAlgorithm.Aes256, Mode = CipherMode.Gcm, Iv = nonce });
        byte[] fresh = CipherCatalog.RandomIv(CipherAlgorithm.Aes256, CipherMode.Gcm);
        link.Apply(link.BuildWriteBack(derived.Current, ContentSink.For(parent), key, fresh), "書き戻し");
        byte[] written = ReadAll(parent.Current);
        Assert.Equal(cipher.Length, written.Length);
        Assert.NotEqual(cipher, written);

        // 新しいナンスで復号すると元の平文。
        using var check = new Document(new MemoryByteSource(written), Options());
        using var q = new CipherParameters { Algorithm = CipherAlgorithm.Aes256, Mode = CipherMode.Gcm, Key = key, Iv = fresh };
        Assert.Equal(plain, CompressionTests.ReadContent(CipherEngine.Run(check.Current, 0, written.Length, q, false, ContentSink.For(check)).Content));
    }

    [Fact]
    public void Nesting_is_limited_to_eight_levels() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DerivedLink(new Document(new MemoryByteSource([1]), Options()), 0, 1, new DerivedTransform(), 9));
}
