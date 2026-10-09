using HexEditor.Core.Engine;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Hashing;

/// <summary>「一致するアルゴリズムを探す」で一致した組み合わせ (アルゴリズムとパラメータ、一致の種類)。</summary>
public sealed record AlgorithmMatch(HashAlgorithmChoice Choice, HashMatch Match, byte[] Value)
{
    public string DisplayName => Choice.DisplayName;
}

/// <summary>「一致するアルゴリズムを探す」の結果。<paramref name="Tried"/> は試した組み合わせの数 (「n 種類を試しました」)。</summary>
public sealed record AlgorithmSearchResult(IReadOnlyList<AlgorithmMatch> Matches, int Tried, long BytesRead);

/// <summary>
/// 値だけ分かっていてアルゴリズムが分からない場合に、一致するアルゴリズムを探す (ANA-21 の仕様 5)。期待値のビット数と出力のビット数が
/// 同じすべての組み合わせ (全 CRC プリセット、カスタム CRC、全チェックサムの全オプションの組み合わせ、その他のアルゴリズムの既定の
/// パラメータ。出力長を選べるものは期待値の長さ) を、対象を 1 回だけ読んで同時に計算する (<see cref="HashEngine.Compute"/>)。
/// </summary>
public static class AlgorithmFinder
{
    private static readonly HashComplement[] Complements = [HashComplement.None, HashComplement.Ones, HashComplement.Twos];

    /// <summary>
    /// 出力が <paramref name="bits"/> ビットになる、試す組み合わせの一覧 (一覧の順。<see cref="HashCatalog.All"/> から作るため、
    /// カスタム CRC や後から加わったアルゴリズムも含む)。この環境で使えないアルゴリズムは含めない。
    /// </summary>
    public static IReadOnlyList<HashAlgorithmChoice> Candidates(int bits)
    {
        var result = new List<HashAlgorithmChoice>();
        if (bits <= 0)
        {
            return result;
        }

        foreach (HashAlgorithmInfo algorithm in HashCatalog.All)
        {
            if (!algorithm.IsAvailable)
            {
                continue;
            }

            foreach (HashParameters p in ParameterCombinations(algorithm, bits))
            {
                if (algorithm.Validate(p) == HashParameterError.None && algorithm.BitsFor(p) == bits)
                {
                    result.Add(new HashAlgorithmChoice(algorithm, p));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 探す。<paramref name="target"/> の対象範囲・除外範囲を使う (アルゴリズムと計算方法は無視し、範囲は連結して 1 つの値を求める)。
    /// null ならドキュメント全体。<paramref name="operation"/> があれば進捗を報告し、キャンセルされれば
    /// <see cref="OperationCanceledException"/> を投げる。
    /// </summary>
    public static AlgorithmSearchResult Find(DocumentSnapshot snapshot, ExpectedHash expected, HashRequest? target = null,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        IReadOnlyList<HashAlgorithmChoice> candidates = Candidates(expected.Value.Length * 8);
        if (candidates.Count == 0)
        {
            return new AlgorithmSearchResult([], 0, 0);
        }

        HashRequest request = (target ?? new HashRequest { Algorithms = [] }) with
        {
            Algorithms = candidates,
            RangeMode = HashRangeMode.Concatenate,
        };
        HashComputation computation = HashEngine.Compute(snapshot, request, operation, cancellationToken);
        var matches = new List<AlgorithmMatch>();
        foreach (HashResultRow row in computation.Rows)
        {
            HashMatch match = expected.Compare(row.Value, row.Algorithm.IsNumeric && row.Value.Length <= 8);
            if (match != HashMatch.None)
            {
                matches.Add(new AlgorithmMatch(row.Choice, match, row.Value));
            }
        }

        return new AlgorithmSearchResult(matches, candidates.Count, computation.BytesRead);
    }

    /// <summary>試すパラメータの組み合わせ (補数 × エンディアン × 符号。出力長を選べるものは期待値の長さ)。</summary>
    private static IEnumerable<HashParameters> ParameterCombinations(HashAlgorithmInfo algorithm, int bits)
    {
        HashParameterKinds kinds = algorithm.Parameters;
        HashComplement[] complements = kinds.HasFlag(HashParameterKinds.Complement) ? Complements : [HashComplement.None];
        bool?[] endians = kinds.HasFlag(HashParameterKinds.Endian) ? [false, true] : [null];
        bool[] signs = kinds.HasFlag(HashParameterKinds.Signed) ? [false, true] : [false];
        int outputBits = kinds.HasFlag(HashParameterKinds.OutputLength) && bits != algorithm.Bits ? bits : 0;
        foreach (HashComplement complement in complements)
        {
            foreach (bool? endian in endians)
            {
                foreach (bool signed in signs)
                {
                    yield return new HashParameters { Complement = complement, BigEndian = endian, Signed = signed, OutputBits = outputBits };
                }
            }
        }
    }
}
