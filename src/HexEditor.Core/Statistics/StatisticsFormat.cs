using System.Globalization;

namespace HexEditor.Core.Statistics;

/// <summary>記述統計の項目 (ANA-11 の仕様 2 の表の行)。</summary>
public enum StatisticItem
{
    Count,
    Sum,
    Minimum,
    Maximum,
    Range,
    Mean,
    PopulationVariance,
    SampleVariance,
    PopulationStdDev,
    SampleStdDev,
    Median,
    Q1,
    Q3,
    Mode,
    Skewness,
    Kurtosis,
    ChiSquare,
    SerialCorrelation,
    Pi,
}

/// <summary>値を求められない理由 (「—」のツールチップ。ANA-11 の「エラー」)。</summary>
public enum StatisticsUnavailable
{
    None,

    /// <summary>要素が 1 つもない。</summary>
    NoElements,

    /// <summary>要素が足りない (分散は 2 個、歪度は 3 個、尖度は 4 個から)。</summary>
    TooFewElements,

    /// <summary>すべて同じ値 (分散が 0)。</summary>
    AllSame,

    /// <summary>要素数が多すぎるため求めていない (最頻値)。</summary>
    TooManyElements,

    /// <summary>この型では求めない (カイ二乗値・π の推定は u8 のみ)。</summary>
    NotApplicable,
}

/// <summary>統計の値の表示 (ANA-11 の仕様 4)。数値は地域設定に従う。16 進の値は地域設定に依存しない。</summary>
public static class StatisticsFormat
{
    /// <summary>小数は有効数字 10 桁。</summary>
    public static string Decimal(double value, CultureInfo culture) =>
        double.IsNaN(value) ? "—" : value == Math.Floor(value) && Math.Abs(value) < 1e15 ? value.ToString("N0", culture) : value.ToString("G10", culture);

    /// <summary>整数 (128 bit) を桁区切り付きで。</summary>
    public static string Integer(Int128 value, CultureInfo culture)
    {
        if (value >= long.MinValue && value <= long.MaxValue)
        {
            return ((long)value).ToString("N0", culture);
        }

        // Int128 は N0 を持たないので、3 桁ごとに区切る。
        string digits = Int128.Abs(value).ToString(CultureInfo.InvariantCulture);
        string separator = culture.NumberFormat.NumberGroupSeparator;
        var parts = new List<string>();
        for (int end = digits.Length; end > 0; end -= 3)
        {
            parts.Insert(0, digits[Math.Max(0, end - 3)..end]);
        }

        return (value < 0 ? culture.NumberFormat.NegativeSign : string.Empty) + string.Join(separator, parts);
    }

    /// <summary>16 進 (地域設定に依存しない)。負の値は 2 の補数ではなく符号付きで。</summary>
    public static string Hex(Int128 value) =>
        (value < 0 ? "-0x" : "0x") + Int128.Abs(value).ToString("X", CultureInfo.InvariantCulture);

    /// <summary>平均: 整数型で割り切れるなら正確な整数、それ以外は有効数字 10 桁。</summary>
    public static string Mean(DescriptiveStats d, CultureInfo culture)
    {
        if (d.Count == 0)
        {
            return "—";
        }

        if (d.IntegerSum is Int128 sum && sum % d.Count == 0)
        {
            return Integer(sum / d.Count, culture);
        }

        return Decimal(d.Mean, culture);
    }

    /// <summary>中央値・四分位数: 近似なら値の前に ≈ を付ける。</summary>
    public static string Quantile(DescriptiveStats d, double value, CultureInfo culture) =>
        double.IsNaN(value) ? "—" : (d.QuantilesApproximate ? "≈" : string.Empty) + Decimal(value, culture);

    /// <summary>項目を求められない理由 (求められれば None)。</summary>
    public static StatisticsUnavailable WhyUnavailable(DescriptiveStats d, StatisticItem item)
    {
        bool byteOnly = item is StatisticItem.ChiSquare or StatisticItem.Pi;
        if (byteOnly && d.Type != ElementType.U8)
        {
            return StatisticsUnavailable.NotApplicable;
        }

        if (d.Count == 0 && item != StatisticItem.Count && item != StatisticItem.Sum)
        {
            return StatisticsUnavailable.NoElements;
        }

        switch (item)
        {
            case StatisticItem.SampleVariance or StatisticItem.SampleStdDev or StatisticItem.SerialCorrelation when d.Count < 2:
            case StatisticItem.Skewness when d.Count < 3:
            case StatisticItem.Kurtosis when d.Count < 4:
                return StatisticsUnavailable.TooFewElements;
            case StatisticItem.Skewness or StatisticItem.Kurtosis or StatisticItem.SerialCorrelation when d.PopulationVariance == 0:
                return StatisticsUnavailable.AllSame;
            case StatisticItem.Mode when d.ModeTooMany:
                return StatisticsUnavailable.TooManyElements;
            case StatisticItem.SerialCorrelation when double.IsNaN(d.SerialCorrelation):
                return StatisticsUnavailable.TooFewElements;
            case StatisticItem.Pi when d.Pi is null:
                return StatisticsUnavailable.TooFewElements;
            default:
                return StatisticsUnavailable.None;
        }
    }

    /// <summary>大きさの短い表記 (例: 64 MB、100 GB)。1,024 の累乗で、整数なら小数を付けない。</summary>
    public static string Size(long bytes, CultureInfo culture)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB", "EB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return (value == Math.Floor(value) ? value.ToString("N0", culture) : value.ToString("N1", culture)) + " " + units[unit];
    }
}
