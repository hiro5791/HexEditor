using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace HexEditor.App.Services.Audio;

/// <summary>
/// 音声の出力 (07 の TPL-16 の <c>audio</c> の再生)。テスト用のビルドで設定したときは、サンプル列を記録するだけの仮想の出力に差し替える
/// (TC-TPL-16-02。CI に音声の出力先がなくても確かめられるように)。
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>再生を始める。<paramref name="wav"/> は WAV ファイルの内容、<paramref name="samples"/> は 16 bit のサンプル (チャンネルは交互)。</summary>
    void Play(byte[] wav, short[] samples, int sampleRate, int channels, TimeSpan from);

    void Stop();

    bool IsPlaying { get; }

    TimeSpan Position { get; }

    /// <summary>最後まで再生した (どのスレッドからでも呼ばれる)。</summary>
    event EventHandler? Ended;
}

/// <summary>出力の作成 (テスト用のビルドでは仮想の出力にできる)。</summary>
public static class AudioOutputs
{
#if HEX_TEST_HOOKS
    /// <summary>テストの仮想の出力 (null なら実際の出力)。テスト用の命令で設定する。</summary>
    public static FakeAudioOutput? Fake { get; set; }

    public static IAudioOutput Create() => Fake is { } fake ? fake : new MediaPlayerAudioOutput();
#else
    public static IAudioOutput Create() => new MediaPlayerAudioOutput();
#endif
}

/// <summary>Windows のメディア再生 (<see cref="MediaPlayer"/>) で WAV を再生する。</summary>
internal sealed class MediaPlayerAudioOutput : IAudioOutput
{
    private MediaPlayer? _player;
    private InMemoryRandomAccessStream? _stream;

    public event EventHandler? Ended;

    public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;

    public void Play(byte[] wav, short[] samples, int sampleRate, int channels, TimeSpan from)
    {
        Stop();
        _stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(_stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(wav);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }

        _stream.Seek(0);
        _player = new MediaPlayer { Source = MediaSource.CreateFromStream(_stream, "audio/wav"), AudioCategory = MediaPlayerAudioCategory.Media };
        _player.MediaEnded += (_, _) => Ended?.Invoke(this, EventArgs.Empty);
        _player.PlaybackSession.Position = from;
        _player.Play();
    }

    public void Stop()
    {
        if (_player is not null)
        {
            _player.Pause();
            _player.Dispose();
            _player = null;
        }

        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose() => Stop();
}

#if HEX_TEST_HOOKS
/// <summary>
/// テストの仮想の出力: 渡されたサンプル列を記録する。再生位置は時計ではなく、テスト用の命令 (<see cref="Advance"/>) で進める
/// (時間で待つテストにしない)。停止すると位置は止まる。
/// </summary>
public sealed class FakeAudioOutput : IAudioOutput
{
    private readonly Lock _lock = new();
    private TimeSpan _position;
    private TimeSpan _length;

    public event EventHandler? Ended;

    /// <summary>受け取ったサンプル (再生の開始位置から。チャンネルは交互)。</summary>
    public short[] Received { get; private set; } = [];

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public int PlayCount { get; private set; }

    public bool IsPlaying { get; private set; }

    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                return _position;
            }
        }
    }

    public void Play(byte[] wav, short[] samples, int sampleRate, int channels, TimeSpan from)
    {
        lock (_lock)
        {
            long skip = Math.Clamp((long)(from.TotalSeconds * sampleRate) * channels, 0, samples.Length);
            Received = samples[(int)skip..];
            SampleRate = sampleRate;
            Channels = channels;
            _position = from;
            _length = TimeSpan.FromSeconds((double)samples.Length / Math.Max(1, channels) / Math.Max(1, sampleRate));
            IsPlaying = true;
            PlayCount++;
        }
    }

    /// <summary>再生中なら位置を進める。最後に達したら止まる。</summary>
    public void Advance(TimeSpan delta)
    {
        bool ended = false;
        lock (_lock)
        {
            if (!IsPlaying)
            {
                return;
            }

            _position += delta;
            if (_position >= _length)
            {
                _position = _length;
                IsPlaying = false;
                ended = true;
            }
        }

        if (ended)
        {
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            IsPlaying = false;
        }
    }

    public void Dispose() => Stop();
}
#endif
