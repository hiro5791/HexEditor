#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
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
}
#endif
