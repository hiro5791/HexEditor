using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Macros;

namespace HexEditor.App.Services.Scripting;

/// <summary>
/// マクロの記録と「直前のマクロ」(08 の AUTO-27、AUTO-28)。アプリ全体で 1 つ (記録中はどのウィンドウの操作も記録し、ステータスバーの表示は
/// すべてのウィンドウに出す)。UI スレッドで使う。
/// </summary>
internal sealed class MacroService
{
    /// <summary>位置の記録方式の保存先 (state.json。記録開始時の選択を覚える。AUTO-27 の仕様 3)。</summary>
    public const string PositionModeKey = "macro.positionMode";

    /// <summary>記録開始時に方式を尋ねるか (「今後は確認しない」で false。state.json)。</summary>
    public const string AskPositionKey = "macro.askPosition";

    private MacroRecorder? _recorder;

    /// <summary>記録中の記録。記録中でなければ null。</summary>
    public MacroRecorder? Recorder => _recorder;

    public bool IsRecording => _recorder is not null;

    /// <summary>「直前のマクロ」の本文 (AUTO-28 の仕様 1)。直前に記録した、または直前に再生したマクロ。アプリの終了まで再生できる。</summary>
    public string? LastScript { get; private set; }

    /// <summary>「直前のマクロ」が保存したファイルなら、そのパス (信頼の確認は保存した内容と同じなら出さない。AUTO-28 の仕様 7)。</summary>
    public string? LastPath { get; private set; }

    /// <summary>記録の状態 (開始・停止・操作の数) が変わった。</summary>
    public event EventHandler? Changed;

    /// <summary>記録できない操作があった (ステータスバーに「記録できない操作: 名前」と一時表示する。AUTO-27 の仕様 5)。</summary>
    public event EventHandler<UnrecordableEventArgs>? Unrecordable;

    /// <summary>記録を始める。</summary>
    public MacroRecorder Start(MacroPositionMode mode)
    {
        _recorder = new MacroRecorder(mode);
        _recorder.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _recorder.Unrecordable += (_, e) => Unrecordable?.Invoke(this, e);
        Changed?.Invoke(this, EventArgs.Empty);
        return _recorder;
    }

    /// <summary>記録を止め、「直前のマクロ」にする。記録中でなければ null。</summary>
    public MacroRecorder? Stop()
    {
        MacroRecorder? recorder = _recorder;
        if (recorder is null)
        {
            return null;
        }

        _recorder = null;
        LastScript = recorder.ToScript();
        LastPath = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return recorder;
    }

    /// <summary>直前のマクロにする (保存した、またはファイルから再生した)。</summary>
    public void SetLast(string script, string? path)
    {
        LastScript = script;
        LastPath = path;
    }

    /// <summary>記録できない操作を行った。記録中でなければ何もしない。</summary>
    public void RaiseUnrecordable(string name) => _recorder?.RaiseUnrecordable(name);

    /// <summary>記録中なら操作を記録する。</summary>
    public void Record(Action<MacroRecorder> record)
    {
        if (_recorder is { } recorder)
        {
            record(recorder);
        }
    }
}
