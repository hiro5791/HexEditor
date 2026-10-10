namespace HexEditor.Core.Statistics;

/// <summary>
/// 分位数の近似 (t-digest、マージ方式。ANA-11 の仕様 3)。要素数が多すぎて全要素を並べ替えられない場合に、
/// 中央値・四分位数をメモリ一定で求める。
/// </summary>
public sealed class TDigest
{
    private readonly double _compression;
    private readonly double[] _buffer;
    private int _buffered;
    private List<(double Mean, double Weight)> _centroids = [];
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;

    public TDigest(double compression = 200, int bufferSize = 8192)
    {
        _compression = compression;
        _buffer = new double[bufferSize];
    }

    public long Count { get; private set; }

    public void Add(double value)
    {
        if (double.IsNaN(value))
        {
            return;
        }

        _min = Math.Min(_min, value);
        _max = Math.Max(_max, value);
        _buffer[_buffered++] = value;
        Count++;
        if (_buffered == _buffer.Length)
        {
            Compress();
        }
    }

    /// <summary>分位 <paramref name="q"/> (0〜1) の近似値。要素がなければ NaN。</summary>
    public double Quantile(double q)
    {
        Compress();
        if (_centroids.Count == 0)
        {
            return double.NaN;
        }

        if (_centroids.Count == 1)
        {
            return _centroids[0].Mean;
        }

        double total = Count;
        double target = q * total;
        double cumulative = 0;
        double previousCenter = 0;
        double previousMean = _min;
        for (int i = 0; i < _centroids.Count; i++)
        {
            (double mean, double weight) = _centroids[i];
            double center = cumulative + (weight / 2);
            if (target <= center)
            {
                if (i == 0)
                {
                    // 最初のセントロイドの中心より前は、最小値から補間する。
                    return center <= 0 ? mean : _min + ((mean - _min) * (target / center));
                }

                double t = (target - previousCenter) / (center - previousCenter);
                return previousMean + (t * (mean - previousMean));
            }

            cumulative += weight;
            previousCenter = center;
            previousMean = mean;
        }

        // 最後のセントロイドの中心より後ろは、最大値まで補間する。
        double rest = total - previousCenter;
        return rest <= 0 ? _max : previousMean + ((_max - previousMean) * ((target - previousCenter) / rest));
    }

    private void Compress()
    {
        if (_buffered == 0)
        {
            return;
        }

        var all = new List<(double Mean, double Weight)>(_centroids.Count + _buffered);
        all.AddRange(_centroids);
        for (int i = 0; i < _buffered; i++)
        {
            all.Add((_buffer[i], 1));
        }

        _buffered = 0;
        all.Sort((a, b) => a.Mean.CompareTo(b.Mean));
        double total = 0;
        foreach ((_, double w) in all)
        {
            total += w;
        }

        var result = new List<(double Mean, double Weight)>();
        (double curMean, double curWeight) = all[0];
        double soFar = 0;
        for (int i = 1; i < all.Count; i++)
        {
            (double mean, double weight) = all[i];
            double proposed = curWeight + weight;
            double q0 = soFar / total;
            double q2 = (soFar + proposed) / total;
            if (Scale(q2) - Scale(q0) <= 1)
            {
                curMean += (mean - curMean) * weight / proposed;
                curWeight = proposed;
            }
            else
            {
                result.Add((curMean, curWeight));
                soFar += curWeight;
                (curMean, curWeight) = (mean, weight);
            }
        }

        result.Add((curMean, curWeight));
        _centroids = result;
    }

    /// <summary>スケール関数 k1 (両端ほどセントロイドを小さくする)。</summary>
    private double Scale(double q) => _compression / (2 * Math.PI) * Math.Asin(Math.Clamp((2 * q) - 1, -1, 1));
}
