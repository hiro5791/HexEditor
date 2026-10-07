#if HEX_TEST_HOOKS
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// テスト用のビルドだけの、性能のテストの入口 (テスト方針 7.2「描画の計測ログ」)。キー入力を注入し、実際のキー入力と同じく
/// 診断の記録 (key の行) に残す。描画はすぐに行わず、通常の描画の予約に任せる (キー入力から画面に出るまでを計るため)。
/// </summary>
public sealed partial class HexView
{
    /// <summary>キー 1 つを、診断の記録に残してから処理する。処理したら true。</summary>
    public bool InjectMeasuredKey(VirtualKey key, bool shift, bool ctrl, bool alt)
    {
        _diagnostics.RecordKey(key);
        return HandleKey(key, shift, ctrl, alt);
    }

    /// <summary>
    /// キー以外の操作 (ジャンプの実行など) の開始を、キー入力と同じく診断の記録に残す。この後に描いた最初のフレームまでを計れる。
    /// </summary>
    public void MarkDiagnostics() => _diagnostics.RecordKey(VirtualKey.None);
}
#endif
