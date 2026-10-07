using System.Diagnostics;
using System.Runtime.InteropServices;
using HexEditor.App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI.Text.Core;

namespace HexEditor.App.Controls;

/// <summary>
/// 文字入力 (EDIT-12 の仕様 1・2)。TSF (Windows.UI.Text.Core の <see cref="CoreTextEditContext"/>) で受け取り、
/// IME の変換中の文字列はカーソル位置に下線付きで重ねるだけにして、確定してから書き込む。
/// <para>
/// TSF が使えない環境では <see cref="UIElement.CharacterReceived"/> だけで受け取る。両方から同じ文字が届く場合に
/// 二重に書かないよう、直前 1 秒の文字を照らし合わせる。
/// </para>
/// </summary>
public sealed partial class HexView
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(1);

    private CoreTextEditContext? _textContext;

    /// <summary>TSF に見せる文字列 (変換中の文字列だけを持ち、確定したら空にする)。</summary>
    private string _imeText = string.Empty;

    private CoreTextRange _imeSelection;
    private bool _composing;
    private bool _textContextFocused;

    /// <summary>TSF で書き込んだ文字 (CharacterReceived で同じ文字が来たら捨てる)。</summary>
    private readonly Queue<(char Character, long Time)> _deliveredByTextInput = new();

    /// <summary>CharacterReceived で書き込んだ文字 (TSF から同じ文字が来たら捨てる)。</summary>
    private readonly Queue<(char Character, long Time)> _deliveredByCharacter = new();

    private void InitializeTextInput()
    {
        try
        {
            CoreTextEditContext context = CoreTextServicesManager.GetForCurrentView().CreateEditContext();
            context.InputScope = CoreTextInputScope.Default;
            context.TextRequested += TextContext_TextRequested;
            context.SelectionRequested += (_, a) => a.Request.Selection = _imeSelection;
            context.TextUpdating += TextContext_TextUpdating;
            context.SelectionUpdating += (_, a) =>
            {
                _imeSelection = ClampRange(a.Selection);
                a.Result = CoreTextSelectionUpdatingResult.Succeeded;
            };
            context.FormatUpdating += (_, a) => a.Result = CoreTextFormatUpdatingResult.Succeeded;
            context.LayoutRequested += TextContext_LayoutRequested;
            context.CompositionStarted += (_, _) =>
            {
                _composing = true;
                UpdateCompositionView();
            };
            context.CompositionCompleted += (_, _) =>
            {
                _composing = false;
                CommitImeText();
            };
            context.FocusRemoved += (_, _) =>
            {
                _textContextFocused = false;
                CancelComposition();
            };
            _textContext = context;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // TSF が使えない (CharacterReceived だけで受け取る)。
            AppLog.Warning($"HexView: TSF を使えません ({ex.HResult:X8})");
            _textContext = null;
        }
    }

    private void TextInputFocusEnter()
    {
        if (_textContext is not null && !_textContextFocused)
        {
            _textContextFocused = true;
            _textContext.NotifyFocusEnter();
        }
    }

    private void TextInputFocusLeave()
    {
        if (_textContext is not null && _textContextFocused)
        {
            _textContextFocused = false;
            _textContext.NotifyFocusLeave();
        }

        CancelComposition();
    }

    private void TextContext_TextRequested(CoreTextEditContext sender, CoreTextTextRequestedEventArgs args)
    {
        CoreTextRange r = ClampRange(args.Request.Range);
        args.Request.Text = _imeText.Substring(r.StartCaretPosition, r.EndCaretPosition - r.StartCaretPosition);
    }

    private void TextContext_TextUpdating(CoreTextEditContext sender, CoreTextTextUpdatingEventArgs args)
    {
        if (_editor is null || _editor.ReadOnly || _editor.Document.IsEditLocked)
        {
            args.Result = CoreTextTextUpdatingResult.Failed;
            if (_editor is not null)
            {
                Report(Core.View.EditResult.NotEditable);
            }

            return;
        }

        CoreTextRange r = ClampRange(args.Range);
        _imeText = string.Concat(_imeText.AsSpan(0, r.StartCaretPosition), args.Text, _imeText.AsSpan(r.EndCaretPosition));
        _imeSelection = ClampRange(args.NewSelection);
        args.Result = CoreTextTextUpdatingResult.Succeeded;
        if (_composing)
        {
            // 変換中はデータを変えない (EDIT-12 の仕様 2)。
            UpdateCompositionView();
        }
        else
        {
            // IME を使わない入力 (直接入力・デッドキー・絵文字パネル) はその場で確定する。
            CommitImeText();
        }
    }

    private void TextContext_LayoutRequested(CoreTextEditContext sender, CoreTextLayoutRequestedEventArgs args)
    {
        // 変換候補ウィンドウをカーソルの近くに出す (EDIT-12 の仕様 2)。座標は画面の物理ピクセル。
        if (_editor is not null && TryGetCellRect(_editor.Cursor, out Rect cell, _editor.ActiveColumn))
        {
            args.Request.LayoutBounds.TextBounds = ToScreen(Surface, cell);
        }

        args.Request.LayoutBounds.ControlBounds = ToScreen(this, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    /// <summary>確定した文字列を書き込み、TSF の文字列を空に戻す。</summary>
    private void CommitImeText()
    {
        string text = _imeText;
        int length = _imeText.Length;
        _imeText = string.Empty;
        _imeSelection = default;
        CompositionBox.Visibility = Visibility.Collapsed;
        if (length > 0 && _textContext is not null)
        {
            // TSF の通知の中で文字列の変更を通知し返さない (次の処理に回す)。
            _uiQueue.TryEnqueue(() => _textContext?.NotifyTextChanged(new CoreTextRange { StartCaretPosition = 0, EndCaretPosition = length }, 0, default));
        }

        string filtered = FilterDuplicates(text);
        if (filtered.Length > 0)
        {
            TypeCharacters(filtered);
        }
    }

    private void CancelComposition()
    {
        _composing = false;
        _imeText = string.Empty;
        _imeSelection = default;
        CompositionBox.Visibility = Visibility.Collapsed;
    }

    private void UpdateCompositionView()
    {
        if (_imeText.Length == 0)
        {
            CompositionBox.Visibility = Visibility.Collapsed;
            return;
        }

        CompositionText.Text = _imeText;
        CompositionBox.Visibility = Visibility.Visible;
        QueueRender();
    }

    /// <summary>変換中の文字列をカーソル位置に重ねる (Render から呼ぶ)。</summary>
    private void PlaceComposition(double x, double y)
    {
        if (CompositionBox.Visibility == Visibility.Visible)
        {
            Microsoft.UI.Xaml.Controls.Canvas.SetLeft(CompositionBox, x);
            Microsoft.UI.Xaml.Controls.Canvas.SetTop(CompositionBox, y);
            CompositionBox.Height = _rowHeight;
        }
    }

    /// <summary>TSF から確定した文字のうち、CharacterReceived で書き込み済みのものを除く。</summary>
    private string FilterDuplicates(string text)
    {
        long now = Stopwatch.GetTimestamp();
        Expire(_deliveredByCharacter, now);
        var kept = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (_deliveredByCharacter.TryPeek(out (char Character, long Time) head) && head.Character == c)
            {
                _deliveredByCharacter.Dequeue();
                continue;
            }

            kept.Append(c);
            _deliveredByTextInput.Enqueue((c, now));
        }

        return kept.ToString();
    }

    /// <summary>CharacterReceived の文字が TSF で書き込み済みなら true。</summary>
    private bool ConsumeIfDeliveredByTextInput(char c)
    {
        Expire(_deliveredByTextInput, Stopwatch.GetTimestamp());
        if (_deliveredByTextInput.TryPeek(out (char Character, long Time) head) && head.Character == c)
        {
            _deliveredByTextInput.Dequeue();
            return true;
        }

        return false;
    }

    private void RememberCharacterInput(char c)
    {
        if (_textContext is not null)
        {
            _deliveredByCharacter.Enqueue((c, Stopwatch.GetTimestamp()));
        }
    }

    private static void Expire(Queue<(char Character, long Time)> queue, long now)
    {
        while (queue.TryPeek(out (char Character, long Time) head) && Stopwatch.GetElapsedTime(head.Time, now) > DuplicateWindow)
        {
            queue.Dequeue();
        }
    }

    private CoreTextRange ClampRange(CoreTextRange r)
    {
        int start = Math.Clamp(Math.Min(r.StartCaretPosition, r.EndCaretPosition), 0, _imeText.Length);
        int end = Math.Clamp(Math.Max(r.StartCaretPosition, r.EndCaretPosition), 0, _imeText.Length);
        return new CoreTextRange { StartCaretPosition = start, EndCaretPosition = end };
    }

    /// <summary>要素の座標の矩形を、画面の物理ピクセルに変える (TSF・UI オートメーション用)。</summary>
    internal Rect ToScreen(UIElement element, Rect rect)
    {
        if (XamlRoot is null)
        {
            return rect;
        }

        Rect root = element.TransformToVisual(null).TransformBounds(rect);
        double scale = XamlRoot.RasterizationScale;
        var origin = new NativePoint();
        nint hwnd = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (hwnd != 0)
        {
            ClientToScreen(hwnd, ref origin);
        }

        return new Rect(origin.X + root.X * scale, origin.Y + root.Y * scale, root.Width * scale, root.Height * scale);
    }

    /// <summary>画面の物理ピクセルの点を、Surface の座標に変える。</summary>
    internal Point FromScreen(Point screen)
    {
        if (XamlRoot is null)
        {
            return screen;
        }

        double scale = XamlRoot.RasterizationScale;
        var origin = new NativePoint();
        nint hwnd = Win32Interop.GetWindowFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);
        if (hwnd != 0)
        {
            ClientToScreen(hwnd, ref origin);
        }

        var root = new Point((screen.X - origin.X) / scale, (screen.Y - origin.Y) / scale);
        return Surface.TransformToVisual(null).Inverse.TransformPoint(root);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref NativePoint point);
}
