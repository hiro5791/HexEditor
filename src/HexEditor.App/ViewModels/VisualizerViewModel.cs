using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>
/// 画像表示 (VIEW-36) のパネルの状態: 画素の形式・幅・ストライド・開始位置・上下の向き・拡大率と、描いた画素。読むのは表示範囲だけ
/// (幅 × 表示行数 × 画素のバイト数、64 MB まで)。パネルを閉じている間は描かない。ウィンドウごとに 1 つ。
/// </summary>
public sealed partial class VisualizerViewModel : ObservableObject
{
    /// <summary>選べる画素の形式 (仕様 2 の表の順)。</summary>
    public static IReadOnlyList<BytePixelFormat> Formats { get; } = Enum.GetValues<BytePixelFormat>();

    /// <summary>選べる拡大率 (0 は自動。仕様 2)。</summary>
    public static IReadOnlyList<int> Zooms { get; } = [0, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32];

    private DocumentViewModel? _document;

    /// <summary>描き直した (パネルが画像を作り直す)。</summary>
    public event EventHandler? Rendered;

    /// <summary>パネルが表示されている (描く)。</summary>
    public bool IsActive { get; set; }

    public DocumentViewModel? Document => _document;

    [ObservableProperty]
    public partial BytePixelFormat Format { get; set; } = BytePixelFormat.Gray8;

    /// <summary>幅 (画素。1〜65,536)。</summary>
    [ObservableProperty]
    public partial int Width { get; set; } = 256;

    /// <summary>1 行のバイト数 (0 は幅 × 画素のバイト数)。</summary>
    [ObservableProperty]
    public partial long Stride { get; set; }

    [ObservableProperty]
    public partial ByteImageStart StartMode { get; set; } = ByteImageStart.FollowCursor;

    /// <summary>「固定」の開始位置の入力式。</summary>
    [ObservableProperty]
    public partial string FixedStartText { get; set; } = "0";

    [ObservableProperty]
    public partial bool BottomUp { get; set; }

    /// <summary>拡大率 (0 は自動: パネルの幅に合わせる)。</summary>
    [ObservableProperty]
    public partial int Zoom { get; set; }

    /// <summary>パネル内のスクロール (開始位置からの行数)。</summary>
    [ObservableProperty]
    public partial long ScrollRow { get; set; }

    /// <summary>状態の表示 (「表示範囲が大きすぎます」、入力の誤りなど)。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>マウスを合わせた画素の表示 (仕様 6)。</summary>
    [ObservableProperty]
    public partial string HoverText { get; set; } = string.Empty;

    /// <summary>描いた範囲 (なければ null)。</summary>
    public ByteImageLayout? Layout { get; private set; }

    /// <summary>描いた画素 (0xAARRGGBB。幅 × 行数)。</summary>
    public uint[] Pixels { get; private set; } = [];

    /// <summary>実際の拡大率 (自動のときはパネルの幅から決めた値)。</summary>
    public int EffectiveZoom { get; private set; } = 1;

    /// <summary>画像の領域の大きさ (DIP。パネルが渡す)。</summary>
    public double ViewportWidth { get; set; }

    public double ViewportHeight { get; set; }

    /// <summary>スクロールできる行数 (開始位置から末尾までの行数 − 表示行数)。</summary>
    [ObservableProperty]
    public partial long ScrollMaximum { get; set; }

    public void Attach(DocumentViewModel? document)
    {
        _document = document;
        ScrollRow = 0;
        Render();
    }

    partial void OnFormatChanged(BytePixelFormat value) => Render();

    partial void OnWidthChanged(int value) => Render();

    partial void OnStrideChanged(long value) => Render();

    partial void OnStartModeChanged(ByteImageStart value)
    {
        ScrollRow = 0;
        Render();
    }

    partial void OnBottomUpChanged(bool value) => Render();

    partial void OnZoomChanged(int value) => Render();

    partial void OnScrollRowChanged(long value) => Render();

    /// <summary>描き始めの位置 (開始位置の決め方に従う)。「固定」の式が不正なら null。</summary>
    public long? BaseStart()
    {
        if (_document is not { } doc)
        {
            return null;
        }

        EditorState editor = doc.Editor;
        switch (StartMode)
        {
            case ByteImageStart.SelectionStart:
                return editor.HasSelection ? editor.SelectionStart : editor.Cursor;
            case ByteImageStart.Fixed:
                return ExpressionEvaluator.TryEvaluate(FixedStartText, new EditorExpressionContext(editor), out long value, out _) && value >= 0
                    ? Math.Min(value, doc.Document.Length)
                    : null;
            default:
                return editor.Cursor;
        }
    }

    /// <summary>「固定」の開始位置を入れる (確定)。不正なら false。</summary>
    public bool SetFixedStart(string text)
    {
        FixedStartText = text;
        ScrollRow = 0;
        bool ok = StartMode != ByteImageStart.Fixed || BaseStart() is not null;
        Render();
        return ok;
    }

    /// <summary>表示範囲の画素を描き直す (表示中の範囲だけを読む)。</summary>
    public void Render()
    {
        if (!IsActive || _document is not { } doc || doc.Document.IsDisposed)
        {
            Layout = null;
            Pixels = [];
            Rendered?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (BaseStart() is not { } baseStart)
        {
            StatusText = Loc.Get("Visualizer_BadStart");
            Layout = null;
            Pixels = [];
            Rendered?.Invoke(this, EventArgs.Empty);
            return;
        }

        int width = Math.Clamp(Width, 1, ByteImage.MaxWidth);
        long stride = Math.Max(Stride, ByteImage.MinStride(Format, width));
        int zoom = Zoom > 0 ? Zoom : Math.Clamp((int)Math.Floor(Math.Max(1, ViewportWidth) / width), 1, 32);
        EffectiveZoom = zoom;
        int maxRows = ViewportHeight > 0 ? Math.Max(1, (int)(ViewportHeight / zoom)) : 256;
        long length = doc.Document.Length;
        long totalRows = baseStart >= length ? 0 : (length - baseStart + stride - 1) / stride;
        ScrollMaximum = Math.Max(0, totalRows - maxRows);
        long scroll = Math.Clamp(ScrollRow, 0, ScrollMaximum);
        long start = baseStart + scroll * stride;
        ByteImageLayout layout = ByteImage.Layout(Format, width, stride, BottomUp, start, length, maxRows);
        int count = (int)Math.Min(layout.ByteLength, Math.Max(0, length - layout.Start));
        byte[] data = new byte[count];
        var states = new ByteState[count];
        if (count > 0)
        {
            count = doc.Document.Current.ReadForDisplay(layout.Start, data, states);
        }

        uint[] pixels = new uint[layout.Width * layout.Rows];
        ByteImage.Render(layout, data.AsSpan(0, count), states.AsSpan(0, count), pixels);
        Layout = layout;
        Pixels = pixels;
        StatusText = layout.Truncated ? Loc.Get("Visualizer_TooLarge") : string.Empty;
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>画素 (x, y) の先頭バイトの位置。描いていない画素なら null。</summary>
    public long? OffsetAt(int x, int y) =>
        Layout is { } l && x >= 0 && y >= 0 && x < l.Width && y < l.Rows && l.OffsetOf(x, y) is var offset && _document is { } doc
            && offset <= doc.Editor.Layout.MaxCursor ? offset : null;

    /// <summary>画素をクリックした (仕様 4): その画素の先頭バイトに Hex ビューのカーソルを移す (ジャンプ履歴に記録)。</summary>
    public bool Click(int x, int y)
    {
        if (OffsetAt(x, y) is not { } offset || _document is not { } doc)
        {
            return false;
        }

        doc.Editor.GoTo(offset);
        return true;
    }

    /// <summary>マウスを合わせた画素のオフセットと値 (仕様 6。例: <c>0x3F20 RGB(255, 128, 0)</c>)。</summary>
    public void Hover(int x, int y)
    {
        if (OffsetAt(x, y) is not { } offset || Layout is not { } l)
        {
            HoverText = string.Empty;
            return;
        }

        HoverText = "0x" + offset.ToString("X", CultureInfo.InvariantCulture) + " " + ByteImage.PixelText(l.Format, Pixels[y * l.Width + x]);
    }
}
