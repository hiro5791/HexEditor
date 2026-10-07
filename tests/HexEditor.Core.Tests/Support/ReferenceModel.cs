namespace HexEditor.Core.Tests.Support;

/// <summary>
/// ピースツリーと比べる単純な基準モデル (テスト方針 6.3)。各バイトの値と、元データ上の位置 (元データ由来でなければ -1) を持つ。
/// </summary>
public sealed class ReferenceModel
{
    private readonly List<byte> _bytes;
    private readonly List<long> _origin;

    public ReferenceModel(byte[] original)
    {
        _bytes = [.. original];
        _origin = [.. Enumerable.Range(0, original.Length).Select(i => (long)i)];
    }

    private ReferenceModel(List<byte> bytes, List<long> origin)
    {
        _bytes = bytes;
        _origin = origin;
    }

    public int Length => _bytes.Count;

    public ReferenceModel Clone() => new([.. _bytes], [.. _origin]);

    public byte[] ToArray() => [.. _bytes];

    public void Insert(int offset, ReadOnlySpan<byte> data)
    {
        _bytes.InsertRange(offset, data.ToArray());
        _origin.InsertRange(offset, Enumerable.Repeat(-1L, data.Length));
    }

    public void Delete(int offset, int length)
    {
        _bytes.RemoveRange(offset, length);
        _origin.RemoveRange(offset, length);
    }

    /// <summary>上書き。末尾を越える分は追加する。</summary>
    public void Overwrite(int offset, ReadOnlySpan<byte> data)
    {
        int replaced = Math.Min(data.Length, Length - offset);
        Delete(offset, replaced);
        Insert(offset, data);
    }

    /// <summary>範囲の複製 (コピー & 貼り付け)。複製した部分は元の由来を引き継ぐ。</summary>
    public void InsertCopy(int destination, int source, int length)
    {
        byte[] bytes = _bytes.GetRange(source, length).ToArray();
        long[] origin = _origin.GetRange(source, length).ToArray();
        _bytes.InsertRange(destination, bytes);
        _origin.InsertRange(destination, origin);
    }

    /// <summary>変更されたバイト (元データ以外、または元データ上の位置とずれているもの) の位置。</summary>
    public IEnumerable<int> ModifiedPositions()
    {
        for (int i = 0; i < _origin.Count; i++)
        {
            if (_origin[i] != i)
            {
                yield return i;
            }
        }
    }

    public static byte[] Repeat(ReadOnlySpan<byte> pattern, long length)
    {
        byte[] result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            result[i] = pattern[i % pattern.Length];
        }

        return result;
    }
}
