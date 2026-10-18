using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Editing.Codecs;

/// <summary>派生ドキュメントの変換の種類 (EDIT-46 の仕様 1)。</summary>
public enum DerivedKind
{
    /// <summary>展開した (書き戻しでは圧縮する)。</summary>
    Decompressed,

    /// <summary>復号した (書き戻しでは暗号化する)。</summary>
    Decrypted,
}

/// <summary>
/// 派生ドキュメントの変換の設定 (EDIT-46 の仕様 1)。暗号の鍵は持たない (書き戻しで改めて入力させる。EDIT-40 の仕様 7)。IV・ナンスはメモリの中だけに
/// 持ち、設定・復旧用データには書かない。
/// </summary>
public sealed record DerivedTransform
{
    public DerivedKind Kind { get; init; }

    public CompressionFormat Format { get; init; }

    /// <summary>書き戻しの圧縮レベル (形式の既定)。</summary>
    public int Level { get; init; }

    public CipherAlgorithm Algorithm { get; init; }

    public CipherMode Mode { get; init; }

    public CipherPadding Padding { get; init; }

    public byte[] Iv { get; init; } = [];

    public byte[] Aad { get; init; } = [];

    /// <summary>復号したときに、タグが範囲の末尾 16 バイトだった (書き戻しでは末尾に付ける)。</summary>
    public bool TagAtEnd { get; init; } = true;
}

/// <summary>
/// 派生ドキュメントと元のドキュメントのつながり (EDIT-46)。元のドキュメントの範囲を追い (前での挿入・削除はずらす)、範囲が別の編集で変わったかを
/// 判定する (仕様 5)。書き戻しは <see cref="BuildWriteBack"/> で内容を作り、<see cref="Apply"/> で元のドキュメントへの 1 つの編集グループにする (仕様 3・4)。
/// </summary>
public sealed class DerivedLink : IDisposable
{
    /// <summary>入れ子の上限 (仕様 8)。</summary>
    public const int MaxDepth = 8;

    private DocumentSnapshot _expected;
    private long _expectedOffset;
    private bool _changed;
    private bool _maybeChanged;
    private bool _writingBack;

    public DerivedLink(Document parent, long offset, long length, DerivedTransform transform, int depth = 1)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, MaxDepth);
        Parent = parent;
        Offset = offset;
        Length = length;
        Transform = transform;
        Depth = depth;
        _expected = parent.Current;
        _expectedOffset = offset;
        parent.Changed += OnParentChanged;
    }

    public Document Parent { get; }

    /// <summary>元のドキュメントの範囲 (書き戻すと新しい長さに更新する。仕様 4)。</summary>
    public long Offset { get; private set; }

    public long Length { get; private set; }

    public DerivedTransform Transform { get; }

    /// <summary>入れ子の深さ (元のファイルから開いたものが 1)。</summary>
    public int Depth { get; }

    /// <summary>元のドキュメントが閉じられた (書き戻せない。仕様 7)。</summary>
    public bool IsDetached => Parent.IsDisposed || _detached;

    private bool _detached;

    /// <summary>元のドキュメントを閉じた (InfoBar で知らせ、以後は通常の新規ドキュメント)。</summary>
    public void Detach()
    {
        _detached = true;
        Parent.Changed -= OnParentChanged;
    }

    /// <summary>派生ドキュメントを開いた (または最後に書き戻した) 後に、元の範囲が別の編集で変わったか (仕様 5)。</summary>
    public bool RangeChanged()
    {
        if (_changed)
        {
            return true;
        }

        if (!_maybeChanged)
        {
            return false;
        }

        // 元に戻す・やり直す・読み直しの後は、内容を比べる。
        DocumentSnapshot current = Parent.Current;
        if (Offset + Length > current.Length || !SameBytes(_expected, _expectedOffset, current, Offset, Length))
        {
            _changed = true;
            return true;
        }

        _maybeChanged = false;
        return false;
    }

    private static bool SameBytes(DocumentSnapshot a, long aOffset, DocumentSnapshot b, long bOffset, long length)
    {
        if (aOffset + length > a.Length || bOffset + length > b.Length)
        {
            return false;
        }

        byte[] x = new byte[(int)Math.Min(length, RangeReader.ChunkSize)], y = new byte[x.Length];
        for (long done = 0; done < length;)
        {
            int n = (int)Math.Min(x.Length, length - done);
            RangeReader.ReadExact(a, aOffset + done, x.AsSpan(0, n));
            RangeReader.ReadExact(b, bOffset + done, y.AsSpan(0, n));
            if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n)))
            {
                return false;
            }

            done += n;
        }

        return true;
    }

    private void OnParentChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (_writingBack || e.Kind is DocumentChangeKind.Saved or DocumentChangeKind.ViewTransformChanged)
        {
            return;
        }

        if (e.IsWholeDocument)
        {
            _maybeChanged = true;
            return;
        }

        long end = Offset + Length;
        if (e.Offset + e.RemovedLength <= Offset && !(e.RemovedLength == 0 && e.InsertedLength == 0))
        {
            // 範囲より前の挿入・削除は範囲をずらす (範囲の先頭への挿入を含む)。
            Offset += e.InsertedLength - e.RemovedLength;
            _expectedOffset = Offset;
            _expected = Parent.Current;
            return;
        }

        if (e.Offset >= end)
        {
            return;
        }

        _changed = true;
    }

    /// <summary>
    /// 書き戻す内容を作る (仕様 3): 派生ドキュメントの内容 <paramref name="derived"/> に逆の変換をかける。暗号化では <paramref name="key"/> を使い、
    /// <paramref name="newIv"/> があればその IV・ナンスを使う (GCM・ChaCha20-Poly1305 の既定は新しいナンス。仕様 6)。
    /// </summary>
    public EditContent BuildWriteBack(DocumentSnapshot derived, ContentSink sink, byte[]? key, byte[]? newIv, LongRunningOperation? operation = null)
    {
        if (Transform.Kind == DerivedKind.Decompressed)
        {
            var options = new CompressionOptions(Transform.Format, Transform.Level);
            return CompressionEngine.Compress(derived, 0, derived.Length, options, sink, operation);
        }

        using var p = new CipherParameters
        {
            Algorithm = Transform.Algorithm,
            Mode = Transform.Mode,
            Padding = Transform.Padding,
            Key = key is null ? [] : (byte[])key.Clone(),
            Iv = (byte[])(newIv ?? Transform.Iv).Clone(),
            Aad = (byte[])Transform.Aad.Clone(),
            AppendTag = true,
        };
        return CipherEngine.Run(derived, 0, derived.Length, p, encrypt: true, sink, operation).Content;
    }

    /// <summary>
    /// 作った内容で元の範囲を置き換える (1 つの編集グループ。仕様 4)。長さが変わり、元のドキュメントが長さを変えられなければ
    /// <see cref="FixedLengthException"/> (書き戻さない)。書き戻した後、記録した範囲を新しい長さにする。
    /// </summary>
    public void Apply(EditContent content, string description)
    {
        if (IsDetached)
        {
            content.Dispose();
            throw new InvalidOperationException("元のドキュメントが閉じられています。");
        }

        long newLength = content.Length;
        _writingBack = true;
        try
        {
            TransformApplier.Apply(Parent, [new RangeReplacement(new TargetRange(Offset, Length), content)], description);
        }
        finally
        {
            _writingBack = false;
        }

        Length = newLength;
        _changed = false;
        _maybeChanged = false;
        _expected = Parent.Current;
        _expectedOffset = Offset;
    }

    public void Dispose() => Parent.Changed -= OnParentChanged;
}
