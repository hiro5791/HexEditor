using System.Buffers.Binary;

namespace HexEditor.Core.Templates.Visualization;

/// <summary>音声のサンプルの形式 (TPL-16 の仕様 1: <c>pcm_u8</c>、<c>pcm_s16le</c>、<c>pcm_s16be</c>、<c>pcm_s24le</c>、<c>pcm_f32le</c>)。</summary>
public enum PcmFormat
{
    U8,
    S16Le,
    S16Be,
    S24Le,
    F32Le,
}

/// <summary>PCM のデコード、WAV の書き出し、波形の集計 (TPL-16)。</summary>
public static class PcmAudio
{
    public static bool TryParse(string name, out PcmFormat format)
    {
        (bool ok, format) = name.Trim().ToLowerInvariant() switch
        {
            "pcm_u8" => (true, PcmFormat.U8),
            "pcm_s16le" => (true, PcmFormat.S16Le),
            "pcm_s16be" => (true, PcmFormat.S16Be),
            "pcm_s24le" => (true, PcmFormat.S24Le),
            "pcm_f32le" => (true, PcmFormat.F32Le),
            _ => (false, PcmFormat.U8),
        };
        return ok;
    }

    public static string Name(PcmFormat format) => format switch
    {
        PcmFormat.U8 => "pcm_u8",
        PcmFormat.S16Le => "pcm_s16le",
        PcmFormat.S16Be => "pcm_s16be",
        PcmFormat.S24Le => "pcm_s24le",
        _ => "pcm_f32le",
    };

    public static int BytesPerSample(PcmFormat format) => format switch
    {
        PcmFormat.U8 => 1,
        PcmFormat.S16Le or PcmFormat.S16Be => 2,
        PcmFormat.S24Le => 3,
        _ => 4,
    };

    /// <summary>サンプル列 (−1〜1、チャンネルは交互) にする。端数のバイトは使わない。</summary>
    public static float[] Decode(ReadOnlySpan<byte> data, PcmFormat format)
    {
        int size = BytesPerSample(format);
        float[] samples = new float[data.Length / size];
        for (int i = 0; i < samples.Length; i++)
        {
            ReadOnlySpan<byte> s = data.Slice(i * size, size);
            samples[i] = format switch
            {
                PcmFormat.U8 => (s[0] - 128) / 128f,
                PcmFormat.S16Le => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                PcmFormat.S16Be => BinaryPrimitives.ReadInt16BigEndian(s) / 32768f,
                PcmFormat.S24Le => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                _ => Clamp(BinaryPrimitives.ReadSingleLittleEndian(s)),
            };
        }

        return samples;
    }

    private static float Clamp(float v) => float.IsNaN(v) ? 0 : Math.Clamp(v, -1f, 1f);

    /// <summary>
    /// サンプルを 16 bit 符号付き (再生・比較用) にする。<see cref="PcmFormat.S16Le"/> と <see cref="PcmFormat.S16Be"/> は元の値と同じになる。
    /// </summary>
    public static short[] ToInt16(ReadOnlySpan<float> samples)
    {
        short[] result = new short[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            result[i] = (short)Math.Clamp(MathF.Round(samples[i] * 32768f), short.MinValue, short.MaxValue);
        }

        return result;
    }

    /// <summary>WAV (RIFF) ファイルの内容。16 bit のビッグエンディアンはリトルエンディアンにし、浮動小数点は形式 3 で書く。</summary>
    public static byte[] Wav(PcmFormat format, int sampleRate, int channels, ReadOnlySpan<byte> data)
    {
        int size = BytesPerSample(format);
        int length = data.Length / size * size;
        byte[] wav = new byte[44 + length];
        Span<byte> w = wav;
        "RIFF"u8.CopyTo(w);
        BinaryPrimitives.WriteInt32LittleEndian(w[4..], 36 + length);
        "WAVE"u8.CopyTo(w[8..]);
        "fmt "u8.CopyTo(w[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(w[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(w[20..], (short)(format == PcmFormat.F32Le ? 3 : 1));
        BinaryPrimitives.WriteInt16LittleEndian(w[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(w[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(w[28..], sampleRate * channels * size);
        BinaryPrimitives.WriteInt16LittleEndian(w[32..], (short)(channels * size));
        BinaryPrimitives.WriteInt16LittleEndian(w[34..], (short)(size * 8));
        "data"u8.CopyTo(w[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(w[40..], length);
        data[..length].CopyTo(w[44..]);
        if (format == PcmFormat.S16Be)
        {
            for (int i = 44; i + 1 < wav.Length; i += 2)
            {
                (wav[i], wav[i + 1]) = (wav[i + 1], wav[i]);
            }
        }

        return wav;
    }

    /// <summary>波形: フレームを <paramref name="buckets"/> 個の区間に分け、区間ごとの最小・最大 (全チャンネル)。</summary>
    public static (float Min, float Max)[] Waveform(ReadOnlySpan<float> samples, int channels, int buckets)
    {
        channels = Math.Max(1, channels);
        long frames = samples.Length / channels;
        if (frames == 0 || buckets <= 0)
        {
            return [];
        }

        int n = (int)Math.Min(buckets, frames);
        var result = new (float Min, float Max)[n];
        for (int b = 0; b < n; b++)
        {
            long from = frames * b / n;
            long to = Math.Max(from + 1, frames * (b + 1) / n);
            float min = float.MaxValue;
            float max = float.MinValue;
            for (long f = from; f < to; f++)
            {
                for (int c = 0; c < channels; c++)
                {
                    float v = samples[(int)(f * channels + c)];
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                }
            }

            result[b] = (min, max);
        }

        return result;
    }
}
