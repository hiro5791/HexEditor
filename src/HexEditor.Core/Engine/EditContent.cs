using HexEditor.Core.Sources;

namespace HexEditor.Core.Engine;

/// <summary><see cref="EditContent"/> の表し方。</summary>
public enum EditContentKind
{
    /// <summary>パターン (1〜4,096 バイト) の繰り返し。生成ピース 1 つ (ENG-03)。</summary>
    Pattern,

    /// <summary>カウンタ方式の乱数 (SplitMix64)。生成ピース 1 つ (ENG-03)。</summary>
    Random,

    /// <summary>バイト列そのもの (追加バッファに入れる)。</summary>
    Bytes,

    /// <summary>ドキュメントの元データの範囲 (同じファイルの内容の挿入。EDIT-30 の仕様 2)。</summary>
    Original,

    /// <summary>データソース (一時ファイルなど) の範囲の参照。</summary>
    Source,
}

/// <summary>
/// 挿入・上書きする内容 (EDIT-14、EDIT-15、EDIT-29、EDIT-30)。実データを作らずに生成ピース・参照で表すものと、作り終えた
/// 一時ファイルを指すものがある。<see cref="Document.InsertContent"/> / <see cref="Document.OverwriteContent"/> に渡すと、
/// 持っているデータソースはドキュメントに移る。渡さずに捨てる場合は <see cref="Dispose"/> で一時ファイルを消す。
/// </summary>
public sealed class EditContent : IDisposable
{
    private IByteSource? _source;

    private EditContent(EditContentKind kind, long length, byte[]? data = null, long position = 0, ulong seed = 0,
        IByteSource? source = null, bool ownsSource = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Kind = kind;
        Length = length;
        Data = data;
        Position = position;
        Seed = seed;
        _source = source;
        OwnsSource = ownsSource;
    }

    public EditContentKind Kind { get; }

    /// <summary>内容の長さ (バイト)。</summary>
    public long Length { get; }

    /// <summary>パターン、またはバイト列。</summary>
    public byte[]? Data { get; }

    /// <summary>パターンの位相、乱数列の開始位置、元データ・データソースの開始位置。</summary>
    public long Position { get; }

    /// <summary>乱数のシード。</summary>
    public ulong Seed { get; }

    /// <summary>データソースを閉じる責任があるか (作った一時ファイル)。</summary>
    public bool OwnsSource { get; }

    /// <summary>参照するデータソース (<see cref="EditContentKind.Source"/>)。</summary>
    public IByteSource? Source => _source;

    /// <summary>実データの一時ファイルを持つか (キャンセル・失敗のテストで、後始末を確かめる)。</summary>
    public bool HasTemporaryFile => OwnsSource && _source is not null;

    /// <summary><paramref name="pattern"/> を位相 <paramref name="phase"/> から <paramref name="length"/> バイト繰り返す。</summary>
    public static EditContent Pattern(ReadOnlySpan<byte> pattern, long length, long phase = 0)
    {
        if (pattern.Length is < 1 or > GeneratedData.MaxPatternLength)
        {
            throw new ArgumentOutOfRangeException(nameof(pattern), "パターンの長さは 1〜4,096 バイトです。");
        }

        return new EditContent(EditContentKind.Pattern, length, pattern.ToArray(), ((phase % pattern.Length) + pattern.Length) % pattern.Length);
    }

    /// <summary>1 バイトの値の繰り返し。</summary>
    public static EditContent Fill(byte value, long length) => Pattern([value], length);

    /// <summary>乱数列 (ENG-03) の <paramref name="position"/> から <paramref name="length"/> バイト。</summary>
    public static EditContent Random(ulong seed, long length, long position = 0) =>
        new(EditContentKind.Random, length, position: position, seed: seed);

    /// <summary>バイト列そのもの。</summary>
    public static EditContent Bytes(byte[] data) => new(EditContentKind.Bytes, data.Length, data);

    /// <summary>ドキュメントの元データの [<paramref name="offset"/>, + <paramref name="length"/>)。</summary>
    public static EditContent Original(long offset, long length) => new(EditContentKind.Original, length, position: offset);

    /// <summary>
    /// データソースの [<paramref name="offset"/>, + <paramref name="length"/>)。<paramref name="owns"/> なら、ドキュメントが閉じるとき
    /// (またはこの内容を捨てるとき) にデータソースを閉じる。
    /// </summary>
    public static EditContent FromSource(IByteSource source, long offset, long length, bool owns)
    {
        if (offset < 0 || length < 0 || offset + length > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return new EditContent(EditContentKind.Source, length, position: offset, source: source, ownsSource: owns);
    }

    /// <summary>
    /// [<paramref name="offset"/>, + <paramref name="length"/>) の部分 (マルチ選択の「要素をまたいで続ける」塗りつぶし。EDIT-29 の仕様 4)。
    /// データソースを持つ内容の部分は同じデータソースを指す (持つ責任も同じ。ドキュメントに渡すと、ドキュメントが 1 回だけ閉じる)。
    /// </summary>
    public EditContent Slice(long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        return Kind switch
        {
            EditContentKind.Pattern => Pattern(Data!, length, Position + offset),
            EditContentKind.Random => Random(Seed, length, Position + offset),
            EditContentKind.Bytes => Bytes(Data.AsSpan((int)offset, (int)length).ToArray()),
            EditContentKind.Original => Original(Position + offset, length),
            _ => new EditContent(EditContentKind.Source, length, position: Position + offset, source: _source, ownsSource: OwnsSource),
        };
    }

    /// <summary>先頭 <paramref name="count"/> バイトを読む (プレビュー用)。</summary>
    public byte[] Preview(int count)
    {
        int n = (int)Math.Min(count, Length);
        byte[] bytes = new byte[n];
        switch (Kind)
        {
            case EditContentKind.Pattern:
                GeneratedData.FillPattern(Data!, Position, bytes);
                break;
            case EditContentKind.Random:
                GeneratedData.FillRandom(Seed, Position, bytes);
                break;
            case EditContentKind.Bytes:
                Data.AsSpan(0, n).CopyTo(bytes);
                break;
            case EditContentKind.Source:
                _source?.Read(Position, bytes);
                break;
        }

        return bytes;
    }

    /// <summary>データソースをドキュメントに渡す (以後この内容は持たない)。</summary>
    internal IByteSource TakeSource()
    {
        IByteSource source = _source ?? throw new ObjectDisposedException(nameof(EditContent));
        if (OwnsSource)
        {
            _source = null;
        }

        return source;
    }

    /// <summary>ドキュメントに渡さなかった一時ファイルを消す。</summary>
    public void Dispose()
    {
        if (OwnsSource && _source is { } source)
        {
            _source = null;
            source.Dispose();
        }
    }
}
