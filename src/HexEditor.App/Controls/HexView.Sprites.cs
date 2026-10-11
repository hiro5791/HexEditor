using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>
/// Hex ビューの背景の面: 四角形の背景を合成の図形 (SpriteVisual) で塗る。XAML の要素を作らないので、1 画面に数千あっても
/// レイアウトの負担にならない (VIEW-04、INSP-33 の仕様 5)。行の下の面 (VIEW-17 の層 12〜16: 列の交互色・レコードの交互色・現在行・
/// バイトテーマの背景) は行ごとの層に、範囲の強調の軽い背景 (色付けルールなど、セルごとに多いもの) はそれより手前の 1 つの層に描く。
/// どちらも同じ根 (Hex ビューの UnderLayer の子の図形) の下に置くので、描き方は 1 通り。
/// </summary>
internal sealed class SpriteSurface
{
    private readonly ContainerVisual _root;
    private readonly Dictionary<Windows.UI.Color, CompositionColorBrush> _brushes = [];

    public SpriteSurface(UIElement host)
    {
        Compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _root = Compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(host, _root);
        Highlights = new SpriteLayer(this);
        _root.Children.InsertAtTop(Highlights.Visual);
    }

    public Compositor Compositor { get; }

    /// <summary>範囲の強調の軽い背景の層 (行の層より手前)。座標は内容の領域の左上から。</summary>
    public SpriteLayer Highlights { get; }

    /// <summary>行の層を作る (行の層はすべて <see cref="Highlights"/> より奥)。</summary>
    public SpriteLayer CreateRowLayer()
    {
        var layer = new SpriteLayer(this);
        _root.Children.InsertAtBottom(layer.Visual);
        return layer;
    }

    /// <summary>XAML のブラシの色 (不透明度を掛けたもの) の合成のブラシ。単色以外のブラシは透明にする (背景の面には単色だけを使う)。</summary>
    public CompositionColorBrush BrushFor(Brush brush)
    {
        Windows.UI.Color color = brush is SolidColorBrush solid
            ? solid.Color with { A = (byte)Math.Round(solid.Color.A * Math.Clamp(solid.Opacity, 0, 1)) }
            : default;
        if (!_brushes.TryGetValue(color, out CompositionColorBrush? result))
        {
            result = Compositor.CreateColorBrush(color);
            _brushes[color] = result;
        }

        return result;
    }
}

/// <summary>
/// 合成の図形の四角形の層。図形は使い回す (<see cref="Begin"/> から <see cref="End"/> までに塗った分だけ見せ、残りは隠す)。
/// 後に塗ったものほど手前。
/// </summary>
internal sealed class SpriteLayer
{
    private readonly SpriteSurface _surface;
    private readonly List<SpriteVisual> _sprites = [];

    // 図形ごとに最後に設定した値 (合成の図形の値を読むと ABI をまたぐため、managed の側で覚えて比べる)。
    private readonly List<(CompositionColorBrush? Brush, Vector3 Offset, Vector2 Size)> _state = [];
    private bool _visible = true;
    private int _used;
    private int _shown;

    public SpriteLayer(SpriteSurface surface)
    {
        _surface = surface;
        Visual = surface.Compositor.CreateContainerVisual();
    }

    public ContainerVisual Visual { get; }

    /// <summary>層の縦の位置 (行の層では行の上端)。</summary>
    public void SetTop(double y) => Visual.Offset = new Vector3(0, (float)y, 0);

    public bool IsVisible
    {
        set
        {
            if (_visible != value)
            {
                _visible = value;
                Visual.IsVisible = value;
            }
        }
    }

    /// <summary>今回塗った図形の数。</summary>
    public int Count => _used;

    public void Begin() => _used = 0;

    public void Fill(Brush brush, double x, double y, double width, double height)
    {
        SpriteVisual sprite;
        if (_used < _sprites.Count)
        {
            sprite = _sprites[_used];
        }
        else
        {
            sprite = _surface.Compositor.CreateSpriteVisual();
            _sprites.Add(sprite);
            _state.Add(default);
            Visual.Children.InsertAtTop(sprite);
        }

        (CompositionColorBrush? oldBrush, Vector3 oldOffset, Vector2 oldSize) = _state[_used];
        CompositionColorBrush composition = _surface.BrushFor(brush);
        var offset = new Vector3((float)x, (float)y, 0);
        var size = new Vector2((float)Math.Max(0, width), (float)height);
        if (!ReferenceEquals(oldBrush, composition))
        {
            sprite.Brush = composition;
        }

        if (oldOffset != offset)
        {
            sprite.Offset = offset;
        }

        if (oldSize != size)
        {
            sprite.Size = size;
        }

        if (_used >= _shown)
        {
            sprite.IsVisible = true;
        }

        _state[_used] = (composition, offset, size);
        _used++;
    }

    /// <summary>今回塗らなかった図形を隠す。</summary>
    public void End()
    {
        for (int i = _used; i < _shown; i++)
        {
            _sprites[i].IsVisible = false;
        }

        _shown = _used;
    }
}
