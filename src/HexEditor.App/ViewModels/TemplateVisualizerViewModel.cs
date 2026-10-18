using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.App.Services.Audio;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Visualization;
using HexEditor.Platform.Imaging;

namespace HexEditor.App.ViewModels;

/// <summary>
/// テンプレートのビジュアライザのパネル (07 の TPL-16) の状態: 表示しているノード、内容 (<see cref="TemplateVisual"/>)、画像のデコードの結果、
/// 音声の再生。データの読み込みとデコードはバックグラウンドで行う (仕様の「巨大ファイル・長時間処理」)。ウィンドウごとに 1 つ。
/// </summary>
public sealed partial class TemplateVisualizerViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _load;
    private IAudioOutput? _audio;
    private short[] _audioSamples = [];

    /// <summary>内容が変わった (パネルが表示を作り直す)。UI スレッドで呼ぶ。</summary>
    public event EventHandler? ContentChanged;

    /// <summary>再生の状態が変わった。</summary>
    public event EventHandler? PlaybackChanged;

    /// <summary>表示しているノード (なければ null)。</summary>
    public TemplateNode? Node { get; private set; }

    public TemplateVisual? Visual { get; private set; }

    /// <summary>画像のファイル形式のデコードの結果 (<c>image</c> のとき)。</summary>
    public DecodedImage? Decoded { get; private set; }

    /// <summary>デコードの失敗の理由 (<c>image</c> のとき)。</summary>
    public string? DecodeError { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>上部の「種類と大きさ」の表示。</summary>
    [ObservableProperty]
    public partial string HeaderText { get; set; } = string.Empty;

    /// <summary>上限で切ったときの表示 (仕様 3)、またはエラー。</summary>
    [ObservableProperty]
    public partial string MessageText { get; set; } = string.Empty;

    /// <summary>代替テキスト (仕様 6)。</summary>
    [ObservableProperty]
    public partial string AltText { get; set; } = string.Empty;

    /// <summary>保存できる (画像は PNG、音声は WAV、表は CSV。仕様 5)。</summary>
    public bool CanSave => Visual is BitmapVisual or AudioVisual or TableVisual || (Visual is EncodedImageVisual && Decoded is not null);

    /// <summary>ノードを表示する (ビジュアライザを持たないノードは「表示するものがありません」)。</summary>
    public async Task ShowAsync(TemplateNode? node)
    {
        StopAudio();
        _seek = TimeSpan.Zero;
        _audioSamples = [];
        _load?.Cancel();
        _load = new CancellationTokenSource();
        CancellationToken ct = _load.Token;
        Node = node;
        Visual = null;
        Decoded = null;
        DecodeError = null;
        if (node is null || !TemplateVisualizer.HasVisualizer(node))
        {
            HeaderText = string.Empty;
            MessageText = Loc.Get("TplVis_Nothing");
            AltText = MessageText;
            IsLoading = false;
            ContentChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsLoading = true;
        MessageText = string.Empty;
        ContentChanged?.Invoke(this, EventArgs.Empty);
        TemplateVisual visual;
        DecodedImage? decoded = null;
        string? error = null;
        try
        {
            TemplateDisplayContext display = TemplateDisplayContext.Current;
            visual = await Task.Run(() => TemplateVisualizer.Create(node, display, ct), ct);
            if (visual is EncodedImageVisual image)
            {
                (decoded, error) = await ImageCodec.DecodeAsync(image.Data, ct);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        Visual = visual;
        Decoded = decoded;
        DecodeError = error;
        IsLoading = false;
        Describe();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Describe()
    {
        TemplateVisual? v = Visual;
        HeaderText = v is null ? string.Empty : Loc.Format("TplVis_Header", Loc.Get("TplVis_Kind_" + v.Kind), v.Kind == TemplateVisualKind.Image && Decoded is { } d
            ? string.Create(CultureInfo.InvariantCulture, $"{d.Width} × {d.Height}, {(v as EncodedImageVisual)?.FormatName ?? d.Format}")
            : v.Description);
        bool truncated = v?.Truncated == true || Decoded?.Truncated == true;
        MessageText = v switch
        {
            ErrorVisual e => Loc.Format("TplVis_CannotShow", e.Error ?? string.Empty),
            EncodedImageVisual when DecodeError is { } reason => Loc.Format("TplVis_CannotShowImage", reason),
            _ when truncated => Loc.Get("TplVis_Truncated"),
            _ => string.Empty,
        };
        AltText = v switch
        {
            BitmapVisual b => Loc.Format("TplVis_ImageAlt", b.Width, b.Height, b.Format),
            EncodedImageVisual i when Decoded is { } img => Loc.Format("TplVis_ImageAlt", img.Width, img.Height, i.FormatName ?? img.Format),
            AudioVisual a => Loc.Format("TplVis_AudioAlt", a.Duration.TotalSeconds.ToString("0.###", CultureInfo.CurrentCulture),
                a.SampleRate.ToString("N0", CultureInfo.CurrentCulture), a.Channels),
            TableVisual t => Loc.Format("TplVis_TableAlt", t.RowCount, t.Columns.Count),
            ChartVisual c => Loc.Format("TplVis_ChartAlt", c.Values.Length, c.Minimum, c.Maximum),
            ColorVisual c => Loc.Format("TplVis_ColorAlt", c.Hex),
            _ => MessageText.Length > 0 ? MessageText : HeaderText,
        };
        OnPropertyChanged(nameof(CanSave));
    }

    // ───────────── 音声 ─────────────

    public bool IsPlaying => _audio?.IsPlaying == true;

    public TimeSpan PlaybackPosition => _audio?.Position ?? _seek;

    private TimeSpan _seek;

    /// <summary>再生する (シークバーの位置から)。</summary>
    public void Play()
    {
        if (Visual is not AudioVisual a)
        {
            return;
        }

        if (_audioSamples.Length != a.Samples.Length)
        {
            _audioSamples = PcmAudio.ToInt16(a.Samples);
        }

        _audio ??= CreateAudio();
        TimeSpan from = _seek >= a.Duration ? TimeSpan.Zero : _seek;
        _audio.Play(a.ToWav(), _audioSamples, a.SampleRate, a.Channels, from);
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    private IAudioOutput CreateAudio()
    {
        IAudioOutput output = AudioOutputs.Create();
        output.Ended += (_, _) => PlaybackChanged?.Invoke(this, EventArgs.Empty);
        return output;
    }

    /// <summary>止める (位置はそこで止まる)。</summary>
    public void StopAudio()
    {
        if (_audio is { } audio)
        {
            _seek = audio.Position;
            audio.Stop();
            PlaybackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>シークバー: 位置を変える (再生中なら新しい位置から再生し直す)。</summary>
    public void Seek(TimeSpan position)
    {
        bool playing = IsPlaying;
        _audio?.Stop();
        _seek = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (playing)
        {
            Play();
        }

        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _load?.Cancel();
        _audio?.Dispose();
        _audio = null;
    }
}
