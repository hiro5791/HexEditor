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
        RowColumns columns = Columns;
        result["topRow"] = _editor.TopRow;
        result["visibleRows"] = _editor.VisibleRows;
        result["bytesPerRow"] = bytesPerRow;
        result["rowShift"] = layout.RowShift;
        result["offsetDigits"] = _rows.FirstOrDefault(r => r.Visible)?.OffsetDigits ?? _digits;
        result["cellWidth"] = _cellWidth;
        result["proportional"] = _proportional;
        result["rowHeight"] = _rowHeight;
        result["focused"] = FocusState != FocusState.Unfocused;
        AddViewInfo(result);
        result["flowDirection"] = FlowDirection.ToString();
        result["columns"] = new JsonArray([.. new (string, bool)[] { ("offset", _showOffset), ("hex", columns.ShowHex), ("text", columns.ShowText) }
            .Where(c => c.Item2).Select(c => (JsonNode?)c.Item1)]);
        result["textStart"] = columns.Format.TextStart;

        // 列の位置 (Hex ビューの左端からの距離。Hex ビューは常に左から右なので、表示上の左右と一致する。UI-44)。
        if (_rows.FirstOrDefault(r => r.Visible) is { } first)
        {
            result["offsetVisualLeft"] = first.Offset.TransformToVisual(this).TransformBounds(new Windows.Foundation.Rect(0, 0, first.Offset.ActualWidth, 1)).X;
            result["contentVisualLeft"] = first.Content.TransformToVisual(this).TransformBounds(new Windows.Foundation.Rect(0, 0, first.Content.ActualWidth, 1)).X;
            result["contentVisualWidth"] = first.Content.ActualWidth;
        }

        var rows = new JsonArray();
        foreach (RowVisual row in _rows)
        {
            if (!row.Visible)
            {
                continue;
            }

            string line = row.ContentText;
            var cells = new JsonArray();
            RowFormat format = columns.Format;
            int valid = row.Count;
            for (int c = 0; c < bytesPerRow; c++)
            {
                CellPaint hexPaint = c < row.HexPaint.Length ? row.HexPaint[c] : default;
                CellPaint textPaint = c < row.TextPaint.Length ? row.TextPaint[c] : default;
                (int hex, int hexLength) = columns.ShowHex ? format.ByteSpan(c, valid) : (-1, 0);
                int textIndex = columns.ShowText ? columns.TextIndex(c) : -1;
                if (columns.ShowHex && hex + hexLength > line.Length)
                {
                    break;
                }

                bool selected = hexPaint.Layer == "selection";
                string underline = row.UnderlineAt(c);

                // Hex 形式は 1 バイトの 2 文字、ほかの形式はバイトを含むセルの文字列 (VIEW-10)。
                string? hexText = !columns.ShowHex ? null : format.IsHexBytes ? line.Substring(hex, 2) : hexPaint.Text ?? line.Substring(hex, hexLength);
                var cell = new JsonObject
                {
                    ["hex"] = hexText,
                    ["frame"] = hexPaint.Frame,
                    ["highlightBackground"] = _placed.Count == 0 ? null : HighlightBackgroundAt(row.ContentRowStart + c, "hex"),

                    // テキスト列が複数のときだけ列ごとの内容を出す (1 行 4,096 バイトの読み出しを重くしない)。
                    ["texts"] = format.ShownTextColumns <= 1 ? null : new JsonArray([.. Enumerable.Range(0, format.ShownTextColumns).Select(t =>
                    {
                        int at = columns.ShowText ? columns.TextIndex(t, c) : -1;
                        (string Glyph, double, double, double)? g = t < row.GlyphsByColumn.Length && c < row.GlyphsByColumn[t].Length ? row.GlyphsByColumn[t][c] : null;
                        return (JsonNode?)new JsonObject
                        {
                            ["text"] = at >= 0 && at < line.Length ? line[at].ToString() : string.Empty,
                            ["glyph"] = g?.Glyph,
                            ["kind"] = row.TextAt(t, c).Kind.ToString(),
                            ["left"] = at >= 0 ? at * _cellWidth : null,
                            ["background"] = t < row.TextPaints.Length && c < row.TextPaints[t].Length && row.TextPaints[t][c].Background is { } tb ? ColorOf(tb) : null,
                        };
                    })]),
                    ["text"] = textIndex >= 0 && textIndex < line.Length ? line[textIndex].ToString() : string.Empty,
                    ["foreground"] = hexPaint.Foreground is { } hf ? ColorOf(hf) : null,
                    ["textForeground"] = textPaint.Foreground is { } tf ? ColorOf(tf) : null,
                    ["hexBackground"] = hexPaint.Background is { } hb ? ColorOf(hb) : ColorOf(_palette!.Background),
                    ["textBackground"] = textPaint.Background is { } tb ? ColorOf(tb) : ColorOf(_palette!.Background),
                    ["hexLayer"] = hexPaint.Layer ?? "normal",
                    ["textLayer"] = textPaint.Layer ?? "normal",
                    ["decoration"] = underline == "none" ? "None" : "Underline",
                    ["underline"] = underline,
                    ["selected"] = selected,
                    ["matched"] = hexPaint.Layer == "match" || textPaint.Layer == "match",
                    ["hexLeft"] = hex >= 0 ? hex * _cellWidth : null,
                    ["textLeft"] = textIndex >= 0 ? textIndex * _cellWidth : null,

                    // 読み込みの状態 (Unreadable のセルには斜線の模様を重ねて描く。VIEW-03 の仕様 5)。
                    ["state"] = c >= row.Lead && c < row.Count ? row.States[c].ToString() : null,
                    ["hatched"] = c >= row.Lead && c < row.Count && row.States[c] == Core.Engine.ByteState.Unreadable,
                };
                if (selected)
                {
                    cell["background"] = cell["hexBackground"]!.GetValue<string>();
                    cell["selectedForeground"] = cell["foreground"]?.GetValue<string>();
                }

                // テキスト列の文字 (VIEW-22): 描いた字形・範囲・横方向の縮小率と、解読の種類。
                if (c < row.Glyphs.Length && row.Glyphs[c] is { } glyph)
                {
                    cell["glyph"] = glyph.Glyph;
                    cell["glyphLeft"] = glyph.Left;
                    cell["glyphWidth"] = glyph.Width;
                    cell["glyphScaleX"] = glyph.ScaleX;
                }

                cell["textKind"] = row.TextAt(c).Kind.ToString();
                cells.Add(cell);
            }

            rows.Add(new JsonObject
            {
                ["index"] = (int)Math.Round((row.Top + _subRowOffset) / _rowHeight),
                ["rowStart"] = row.ContentRowStart,
                ["offsetText"] = row.OffsetText,
                ["line"] = RowLineOf(row),
                ["cells"] = cells,
                ["hexCells"] = format.IsHexBytes && !format.Reverse ? null : HexCellsOf(row, format, line),
                ["separator"] = row.SeparatorLabel,
                ["underFills"] = new JsonArray([.. row.UnderFills.Select(u => (JsonNode?)new JsonObject
                {
                    ["layer"] = u.Layer, ["column"] = u.Column, ["first"] = u.First, ["last"] = u.Last, ["color"] = ColorOf(u.Brush),
                })]),
                ["currentRow"] = row.IsCurrentRow,
                ["lines"] = new JsonArray([.. row.Lines.Select(l => (JsonNode?)new JsonObject
                {
                    ["kind"] = l.Kind, ["x1"] = l.X1, ["y1"] = l.Y1, ["x2"] = l.X2, ["y2"] = l.Y2,
                })]),
            });
        }

        result["rows"] = rows;
        result["ruler"] = RulerModel();
        result["toolTip"] = new JsonObject { ["open"] = CellToolTipOpen, ["text"] = LastToolTip, ["rich"] = RichToolTipRuns(LastRichToolTip) };
        result["caret"] = new JsonObject
        {
            ["visible"] = Caret.Visibility == Visibility.Visible,
            ["left"] = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(Caret),
            ["top"] = Microsoft.UI.Xaml.Controls.Canvas.GetTop(Caret),
            ["width"] = Caret.Width,
            ["height"] = Caret.Height,

            // 縦棒 (挿入モード) は幅 2 の塗りつぶし、帯 (上書きモード) はセルの幅の塗りつぶし、フォーカスのないときは枠線。
            ["shape"] = Caret.Fill is not null && Caret.Width <= 3 ? "bar" : "box",
            ["filled"] = Caret.Fill is not null,
            ["strokeThickness"] = Caret.Fill is not null ? 0 : Caret.StrokeThickness,
            ["row"] = (int)Math.Round((Microsoft.UI.Xaml.Controls.Canvas.GetTop(Caret) + _subRowOffset) / _rowHeight),
        };
        result["secondaryCaret"] = new JsonObject
        {
            ["visible"] = SecondaryCaret.Visibility == Visibility.Visible,
            ["left"] = Microsoft.UI.Xaml.Controls.Canvas.GetLeft(SecondaryCaret),
            ["top"] = Microsoft.UI.Xaml.Controls.Canvas.GetTop(SecondaryCaret),
            ["width"] = SecondaryCaret.Width,
            ["strokeThickness"] = SecondaryCaret.StrokeThickness,
        };
        result["scrollMarkers"] = new JsonArray([.. PlacedMarkers.Select(m => (JsonNode?)new JsonObject
        {
            ["kind"] = m.Kind,
            ["top"] = m.Top,
            ["height"] = m.Height,
        })]);
        result["characterRange"] = new JsonObject
        {
            ["visible"] = _characterRange?.Visibility == Visibility.Visible,
            ["left"] = _characterRange is null ? 0 : Microsoft.UI.Xaml.Controls.Canvas.GetLeft(_characterRange),
            ["width"] = _characterRange?.Width ?? 0,
        };
        return result;
    }

    private string RowLineOf(RowVisual row) => _showOffset ? row.OffsetText.PadRight(_digits) + "  " + row.ContentText : row.ContentText;

    /// <summary>
    /// 行の Hex 列のセルの一覧 (VIEW-10 の確認用): 表示の並び順に、文字列・単位のバイト数・最初のバイト・枠の種類・左端。
    /// </summary>
    private JsonArray HexCellsOf(RowVisual row, RowFormat format, string line)
    {
        var result = new JsonArray();
        if (!format.ShowHex)
        {
            return result;
        }

        int valid = row.Count;
        if (format.IsHexBytes)
        {
            for (int s = 0; s < format.BytesPerRow; s++)
            {
                int c = format.ByteOfSlot(s, valid);
                int at = format.CellStart(s);
                result.Add(new JsonObject
                {
                    ["text"] = at + 2 <= line.Length ? line.Substring(at, 2) : string.Empty,
                    ["bytes"] = 1,
                    ["first"] = c,
                    ["frame"] = c < row.HexPaint.Length ? row.HexPaint[c].Frame : null,
                    ["left"] = at * _cellWidth,
                });
            }

            return result;
        }

        int unit = format.Unit;
        for (int k = 0; k < format.CellsPerRow; k++)
        {
            int first = k * unit;
            if (format.IsCompleteCell(k, valid) && first >= row.Lead)
            {
                result.Add(new JsonObject
                {
                    ["text"] = row.HexPaint[first].Text,
                    ["bytes"] = unit,
                    ["first"] = first,
                    ["frame"] = null,
                    ["left"] = format.CellStart(k) * _cellWidth,
                });
                continue;
            }

            // 端数のバイト (VIEW-10 の仕様 5): 1 バイトずつ。
            for (int c = first; c < Math.Min(format.BytesPerRow, first + unit); c++)
            {
                if (c < row.Lead || c >= valid)
                {
                    continue;
                }

                (int start, _) = format.ByteSpan(c, valid);
                result.Add(new JsonObject
                {
                    ["text"] = row.HexPaint[c].Text,
                    ["bytes"] = 1,
                    ["first"] = c,
                    ["frame"] = row.HexPaint[c].Frame,
                    ["left"] = start * _cellWidth,
                });
            }
        }

        return result;
    }

    /// <summary>範囲の強調の面に描いたバイトの背景 (一番手前のもの。VIEW-17 の層 7〜11)。なければ null。</summary>
    private string? HighlightBackgroundAt(long offset, string column)
    {
        string? color = null;
        int layer = int.MaxValue;
        foreach (PlacedHighlight p in _placed)
        {
            if (p.Background is { } brush && p.Column == column && offset >= p.First && offset <= p.Last && (int)p.Layer <= layer)
            {
                color = ColorOf(brush);
                layer = (int)p.Layer;
            }
        }

        return color;
    }

    /// <summary>列見出しの描画モデル (VIEW-05、VIEW-06 の仕様 2)。x 座標は内容の領域の左端から (セルの hexLeft と同じ基準)。</summary>
    private JsonObject RulerModel()
    {
        var labels = new JsonArray();
        foreach ((string text, int index, bool hex) in _rulerLabels)
        {
            bool highlighted = _rulerHighlight.Any(h => h.Start == index);
            labels.Add(new JsonObject
            {
                ["text"] = text,
                ["column"] = hex ? "hex" : "text",
                ["left"] = index * _cellWidth,
                ["right"] = (index + text.Length) * _cellWidth,
                ["highlighted"] = highlighted,
            });
        }

        double top = RulerBar.Visibility == Visibility.Visible
            ? RulerText.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).Y
            : double.NaN;
        return new JsonObject
        {
            ["visible"] = RulerBar.Visibility == Visibility.Visible,
            ["offsetHeader"] = OffsetHeaderText.Text,
            ["labels"] = labels,
            ["top"] = double.IsNaN(top) ? null : top,
            ["shift"] = RulerShift.X,
            ["fontSize"] = RulerText.FontSize,
            ["height"] = RulerBar.ActualHeight,
            ["radixMenuOpen"] = RadixMenuOpen,
            ["textHeaders"] = new JsonArray([.. _textHeaders.Select(h => (JsonNode?)new JsonObject
            {
                ["column"] = h.Column, ["name"] = h.Name, ["left"] = h.Index * _cellWidth,
            })]),
            ["textColumnMenuOpen"] = _textColumnMenu?.IsOpen ?? false,
        };
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
    public void InjectPointerDown(Point position, string device, bool right, bool shift, bool ctrl = false, bool alt = false)
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
            LeftButtonPressed(position, shift, pointerId: 1, ctrl, alt);
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

    public void InjectPointerUp(Point position, bool ctrl = false, bool shift = false)
    {
        if (_touchActive)
        {
            TouchReleased(position);
        }

        ReleaseWithMode(ctrl, shift);
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

    /// <summary>マウスの「戻る」「進む」ボタン (VIEW-31) と同じ処理。</summary>
    public void InjectHistoryButton(bool back) => MouseHistoryButton(back);

    /// <summary>右クリックメニューを閉じる (Esc と同じ)。</summary>
    public void HideContextMenu() => _contextMenu?.Hide();

    // ---- 記録 ----

    private readonly List<(long Ms, string Id, string Text)> _announcements = [];
    private int _frames;

    /// <summary>最初に描いた時刻 (Environment.TickCount64)。まだ描いていなければ 0。</summary>
    private long _firstFrameTime;
    private int _placeholderFrames;

    partial void OnAnnounced(string text, string activityId)
    {
        _announcements.Add((Environment.TickCount64, activityId, text));
        if (_announcements.Count > 1000)
        {
            _announcements.RemoveAt(0);
        }
    }

    private double _maxRenderMs;
    private int _glyphMeasures;

    partial void OnRenderTimed(TimeSpan time) => _maxRenderMs = Math.Max(_maxRenderMs, time.TotalMilliseconds);

    partial void OnGlyphMeasured() => _glyphMeasures++;

    partial void OnRendered(int placeholderCells)
    {
        _frames++;
        if (_firstFrameTime == 0)
        {
            _firstFrameTime = Environment.TickCount64;
        }
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
        result["offsetLeft"] = LeftPadding - OffsetColumnShift;
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
        result["firstFrameTime"] = _firstFrameTime;
        result["maxRenderMs"] = _maxRenderMs;
        result["glyphMeasures"] = _glyphMeasures;
        result["placeholderFrames"] = _placeholderFrames;
        result["highContrast"] = IsHighContrast;
        result["zoom"] = _zoom;
        result["screenZoom"] = _screenZoom;
        result["fontFamily"] = _fontFamilyName;
        result["encoding"] = _editor?.TextEncoding.Name;
        result["activeColumn"] = _editor is null ? null : _editor.ActiveColumn == Core.View.ActiveColumn.Hex ? "hex" : "text" + (_editor.TextColumn + 1);
        result["textColumn"] = _editor?.TextColumn;
        result["cellFormat"] = _editor?.View.CellFormat.ToString();
        result["textColumns"] = _editor is null ? null : new JsonArray([.. Enumerable.Range(0, _editor.View.TextColumnCount)
            .Select(t => (JsonNode?)new JsonObject { ["name"] = TextColumnName(t), ["encoding"] = _editor.TextEncodingOf(t).Id })]);
        result["byteTheme"] = _byteTheme?.Name;
        result["recordAlternateColor"] = _palette is null ? null : ColorOf(_palette.RecordAlternate);
        result["differenceColor"] = _palette is null ? null : ColorOf(_palette.Difference);
        result["separatorColor"] = _palette is null ? null : ColorOf(_palette.Separator);
        if (_palette is not null)
        {
            result["background"] = ColorOf(_palette.Background);
            result["textColor"] = ColorOf(_palette.Text);
            result["offsetColor"] = ColorOf(_palette.OffsetText);
            result["selectionColor"] = ColorOf(_palette.Selection);
            result["modifiedColor"] = ColorOf(_palette.Modified);
            result["insertedColor"] = ColorOf(_palette.Inserted);
            result["savedChangeColor"] = ColorOf(_palette.SavedChange);
            result["dimColor"] = ColorOf(_palette.Zero);
            result["invalidColor"] = ColorOf(_palette.Invalid);
            result["alternateColor"] = ColorOf(_palette.Alternate);
            result["currentRowColor"] = ColorOf(_palette.CurrentRow);
            result["hexTextColor"] = ColorOf(_palette.HexText);
            result["rulerHighlightColor"] = ColorOf(_palette.RulerHighlight);
        }
    }
}
#endif
