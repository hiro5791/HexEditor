#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// テスト用のビルドだけの入口 (テスト方針 7.2)。キー入力の注入と、描いた内容の読み出し。
/// フォーカスや実際のキーボードを使わずに、キー処理と同じ経路を通す。
/// </summary>
public sealed partial class HexView
{
    /// <summary>キー 1 つを、実際のキー入力と同じ処理に渡す。処理したら true。</summary>
    public bool InjectKey(VirtualKey key, bool shift, bool ctrl, bool alt) => HandleKey(key, shift, ctrl, alt);

    /// <summary>入力した文字を 1 つずつ、実際の文字入力と同じ処理に渡す。処理した文字の数を返す。</summary>
    public int InjectText(string text, bool ctrl = false)
    {
        int handled = 0;
        foreach (char c in text)
        {
            if (HandleCharacter(c, ctrl))
            {
                handled++;
            }
        }

        return handled;
    }

    /// <summary>
    /// 描いた内容 (テスト方針 7.2「描画内容の読み出し」): 行ごとのオフセット・文字列と、セルごとの文字・文字色・
    /// 背景色・線、カーソルの位置と形。描画の後の状態を返すため、呼び出し側は先に描画を済ませる (<see cref="RenderNow"/>)。
    /// </summary>
    public JsonObject ReadRendered()
    {
        var result = new JsonObject();
        if (_editor is null)
        {
            return result;
        }

        HexLayout layout = _editor.Layout;
        int bytesPerRow = layout.BytesPerRow;
        var columns = new RowColumns(bytesPerRow);
        result["topRow"] = _editor.TopRow;
        result["visibleRows"] = _editor.VisibleRows;
        result["bytesPerRow"] = bytesPerRow;
        result["offsetDigits"] = _rows.FirstOrDefault(r => r.Visible)?.OffsetDigits ?? 8;
        result["cellWidth"] = _cellWidth;
        result["rowHeight"] = _rowHeight;
        result["focused"] = FocusState != FocusState.Unfocused;
        AddViewInfo(result);
        result["flowDirection"] = FlowDirection.ToString();

        // 列の位置 (Hex ビューの左端からの距離。Hex ビューは常に左から右なので、表示上の左右と一致する。UI-44)。
        if (_rows.FirstOrDefault(r => r.Visible) is { } first)
        {
            result["offsetLeft"] = first.Offset.TransformToVisual(this).TransformBounds(new Windows.Foundation.Rect(0, 0, first.Offset.ActualWidth, 1)).X;
            result["contentLeft"] = first.Content.TransformToVisual(this).TransformBounds(new Windows.Foundation.Rect(0, 0, first.Content.ActualWidth, 1)).X;
            result["contentWidth"] = first.Content.ActualWidth;
        }

        var rows = new JsonArray();
        foreach (RowVisual row in _rows)
        {
            if (!row.Visible)
            {
                continue;
            }

            // 行の文字列 (Hex 列とテキスト列) と、文字ごとの Run (色・線) を作る。
            var text = new System.Text.StringBuilder();
            var runAt = new List<Run>();
            foreach (Inline inline in row.Content.Inlines)
            {
                if (inline is Run run)
                {
                    text.Append(run.Text);
                    for (int i = 0; i < run.Text.Length; i++)
                    {
                        runAt.Add(run);
                    }
                }
            }

            string line = text.ToString();

            // 選択範囲の層 (検索の一致の強調は除く)。
            var highlighted = new bool[line.Length];
            var matched = new bool[line.Length];
            string? highlightBackground = null;
            string? highlightForeground = null;
            foreach (TextHighlighter h in row.Content.TextHighlighters)
            {
                bool isMatch = _palette is not null && ReferenceEquals(h.Background, _palette.Match);
                if (!isMatch)
                {
                    highlightBackground = ColorOf(h.Background);
                    highlightForeground = ColorOf(h.Foreground);
                }

                foreach (TextRange r in h.Ranges)
                {
                    for (int i = r.StartIndex; i < r.StartIndex + r.Length && i < line.Length; i++)
                    {
                        (isMatch ? matched : highlighted)[i] = true;
                    }
                }
            }

            var cells = new JsonArray();
            for (int c = 0; c < bytesPerRow; c++)
            {
                int hex = columns.HexIndex(c);
                int textIndex = columns.TextIndex(c);
                if (hex + 2 > line.Length)
                {
                    break;
                }

                var cell = new JsonObject
                {
                    ["hex"] = line.Substring(hex, 2),
                    ["text"] = textIndex < line.Length ? line[textIndex].ToString() : string.Empty,
                    ["foreground"] = ColorOf(runAt[hex].Foreground),
                    ["decoration"] = runAt[hex].TextDecorations.ToString(),
                    ["selected"] = highlighted[hex],
                    ["matched"] = matched[hex],

                    // 読み込みの状態 (Unreadable のセルには斜線の模様を重ねて描く。VIEW-03 の仕様 5)。
                    ["state"] = c < row.States.Length ? row.States[c].ToString() : null,
                    ["hatched"] = c < row.States.Length && row.States[c] == Core.Engine.ByteState.Unreadable,
                };
                if (highlighted[hex])
                {
                    cell["background"] = highlightBackground;
                    cell["selectedForeground"] = highlightForeground;
                }

                cells.Add(cell);
            }

            rows.Add(new JsonObject
            {
                ["index"] = (int)Math.Round(row.Top / _rowHeight),
                ["offsetText"] = row.OffsetText,
                ["line"] = row.OffsetText + "  " + line,
                ["cells"] = cells,
            });
        }

        result["rows"] = rows;
        result["caret"] = new JsonObject
        {
            ["visible"] = Caret.Visibility == Visibility.Visible,
            ["left"] = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(Caret),
            ["top"] = Microsoft.UI.Xaml.Controls.Canvas.GetTop(Caret),
            ["width"] = Caret.Width,
            ["height"] = Caret.Height,

            // 縦線 (挿入モード) は塗りつぶし、枠 (上書きモード) は線で描く。
            ["shape"] = Caret.Fill is not null ? "bar" : "box",
            ["strokeThickness"] = Caret.StrokeThickness,
            ["row"] = (int)Math.Round(Microsoft.UI.Xaml.Controls.Canvas.GetTop(Caret) / _rowHeight),
        };
        result["secondaryCaret"] = new JsonObject
        {
            ["visible"] = SecondaryCaret.Visibility == Visibility.Visible,
            ["left"] = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(SecondaryCaret),
            ["top"] = Microsoft.UI.Xaml.Controls.Canvas.GetTop(SecondaryCaret),
            ["width"] = SecondaryCaret.Width,
        };
        return result;
    }

    /// <summary>待っている描画をすぐに行う。</summary>
    public void RenderNow()
    {
        _renderQueued = false;
        UpdateVisibleRows();
        UpdateScrollBar();
        Render();
    }

    // ---- Windows の設定の差し替え (利用者の PC の設定を変えずに、設定に従う処理を確かめる) ----

    /// <summary>ホイール 1 ノッチの行数 (SPI_GETWHEELSCROLLLINES の値。「1 画面ずつ」は uint.MaxValue)。null なら Windows の設定。</summary>
    internal static uint? TestWheelScrollLines { get; set; }

    /// <summary>Windows の文字サイズの倍率 (TextScaleFactor)。null なら Windows の設定。</summary>
    internal static double? TestTextScaleFactor { get; set; }

    /// <summary>文字サイズの設定の変更の通知と同じ処理 (セル幅と行の高さを測り直す)。</summary>
    public void SimulateTextScaleChanged() => RemeasureAndRender();

    /// <summary>表示倍率の異なるモニターへ移ったときの通知 (XamlRoot.Changed) と同じ処理。</summary>
    public void SimulateRasterizationScale(double scale)
    {
        _rasterizationScale = scale;
        RemeasureAndRender();
    }

    // ---- ポインタの注入 (実際のマウス・タッチを使わずに、ポインタのイベントと同じ処理に渡す。座標は描画面の座標) ----

    /// <summary>ボタンを押す。右ボタンは、離した後のジェスチャで開く右クリックメニューも開く。</summary>
    public void InjectPointerDown(Point position, string device, bool right, bool shift)
    {
        if (device == "touch")
        {
            TouchPressed(position);
        }
        else if (right)
        {
            RightButtonPressed(position);
            ShowContextMenu(Surface.TransformToVisual(this).TransformPoint(position));
        }
        else
        {
            LeftButtonPressed(position, shift, pointerId: 1);
        }
    }

    public void InjectPointerMove(Point position)
    {
        if (_touchActive)
        {
            TouchMoved(position);
        }
        else if (_pressed)
        {
            DragMoved(position);
        }
        else
        {
            UpdateHover(position);
        }
    }

    public void InjectPointerUp(Point position)
    {
        if (_touchActive)
        {
            TouchReleased(position);
        }

        EndPointer();
    }

    /// <summary>ホイールの入力 (<paramref name="delta"/> は 1 ノッチ 120。下へ回すと負)。</summary>
    public void InjectWheel(int delta, bool horizontal, bool shift) => Wheel(delta, horizontal, shift);

    /// <summary>縦スクロールバーの Scroll イベント (つまみのドラッグ ThumbTrack・EndScroll など) と同じ処理。</summary>
    public void InjectVerticalScroll(ScrollEventType type, double value) => VerticalScroll(type, value);

    /// <summary>
    /// 縦スクロールバーのテンプレートの部品 (矢印ボタン VerticalSmallDecrease / VerticalSmallIncrease など) を、UI オートメーションの
    /// Invoke と同じ処理で押す (矢印ボタンは UI オートメーションの木に出ないため)。
    /// </summary>
    public void InvokeScrollBarPart(string name)
    {
        var pending = new Queue<DependencyObject>([VerticalBar]);
        while (pending.Count > 0)
        {
            DependencyObject node = pending.Dequeue();
            if (node is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button && button.Name == name)
            {
                var peer = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer
                    .CreatePeerForElement(button).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
                peer.Invoke();
                return;
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        throw new ArgumentException($"Scroll bar part not found: {name}");
    }

    /// <summary>右クリックメニューを閉じる (Esc と同じ)。</summary>
    public void HideContextMenu() => _contextMenu?.Hide();

    // ---- 記録 ----

    private readonly List<(long Ms, string Id, string Text)> _announcements = [];
    private int _frames;
    private int _placeholderFrames;

    partial void OnAnnounced(string text, string activityId)
    {
        _announcements.Add((Environment.TickCount64, activityId, text));
        if (_announcements.Count > 1000)
        {
            _announcements.RemoveAt(0);
        }
    }

    partial void OnRendered(int placeholderCells)
    {
        _frames++;
        if (placeholderCells > 0)
        {
            _placeholderFrames++;
        }
    }

    /// <summary>送った読み上げ文 (時刻は Environment.TickCount64)。<paramref name="clear"/> なら読んだ後に消す。</summary>
    public JsonArray ReadAnnouncements(bool clear)
    {
        var result = new JsonArray([.. _announcements.Select(a => (JsonNode?)new JsonObject
        {
            ["time"] = a.Ms,
            ["id"] = a.Id,
            ["text"] = a.Text,
        })]);
        if (clear)
        {
            _announcements.Clear();
        }

        return result;
    }

    /// <summary>描画の情報の追加分 (ReadRendered に加える)。</summary>
    private void AddViewInfo(JsonObject result)
    {
        result["contentLeft"] = ContentLeft;
        result["offsetLeft"] = LeftPadding;
        result["subRowOffset"] = _subRowOffset;
        result["horizontalOffset"] = _horizontalOffset;
        result["surfaceWidth"] = Surface.ActualWidth;
        result["surfaceHeight"] = Surface.ActualHeight;
        result["fontSize"] = _fontSize;
        result["rasterizationScale"] = _rasterizationScale;
        result["flowDirection"] = FlowDirection.ToString();
        result["horizontalBarVisible"] = HorizontalBar.Visibility == Visibility.Visible;
        result["contextMenuOpen"] = _contextMenu?.IsOpen ?? false;
        result["focusFrame"] = FocusFrameOuter.Visibility == Visibility.Visible ? FocusFrameOuter.StrokeThickness : 0;
        result["caretBlinking"] = _focused && _blinkTimer.IsRunning;
        result["frames"] = _frames;
        result["placeholderFrames"] = _placeholderFrames;
        result["highContrast"] = _accessibilitySettings.HighContrast;
        if (_palette is not null)
        {
            result["background"] = ColorOf(_palette.Background);
            result["textColor"] = ColorOf(_palette.Text);
            result["offsetColor"] = ColorOf(_palette.OffsetText);
            result["selectionColor"] = ColorOf(_palette.Selection);
            result["modifiedColor"] = ColorOf(_palette.Modified);
        }
    }
}
#endif
