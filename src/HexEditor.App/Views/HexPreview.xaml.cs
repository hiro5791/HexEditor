using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HexEditor.App.Views;

/// <summary>
/// 設定画面の Hex 表示のプレビュー (UI-28 の仕様 3、UI-29 の仕様 8)。見本の 3 行 (オフセット、Hex、テキスト) を、指定のフォント・大きさ・
/// 行間・配色で描く。変更されたバイト・挿入されたバイト・選択範囲・検索の一致・カーソル行・交互の行・00 と表示できない文字を含む。
/// 変更されたバイトは色に加えて下線、検索の一致は枠線でも示す (色だけで伝えない。UI-28 の仕様 7)。
/// </summary>
public sealed partial class HexPreview : UserControl
{
    private const int BytesPerRow = 16;

    /// <summary>見本のデータ (3 行)。</summary>
    private static readonly byte[] Sample =
    [
        0x48, 0x65, 0x78, 0x45, 0x64, 0x69, 0x74, 0x6F, 0x72, 0x00, 0x00, 0x00, 0x01, 0x02, 0x7F, 0xFF,
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x53, 0x61, 0x6D, 0x70, 0x6C, 0x65, 0x20, 0x74, 0x65, 0x78, 0x74, 0x2E, 0x00, 0x10, 0x20, 0x30,
    ];

    // 見本の強調の位置 (データの中の番号)。
    private static readonly HashSet<int> ModifiedBytes = [3, 4];
    private static readonly HashSet<int> InsertedBytes = [6];
    private static readonly HashSet<int> MatchBytes = [17, 18, 19];
    private static readonly HashSet<int> SelectedBytes = [36, 37, 38, 39];
    private const int CaretByte = 22;
    private const int CurrentRow = 1;

    private ColorScheme? _scheme;
    private string _fontFamily = "Cascadia Mono, Consolas";
    private double _fontPoints = 10;
    private double _lineHeight = 1.2;

    public HexPreview()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Render();
        Loaded += (_, _) => Render();
    }

    /// <summary>配色 (null は「既定」= テーマの色だけ)。</summary>
    public ColorScheme? Scheme
    {
        get => _scheme;
        set
        {
            _scheme = value;
            Render();
        }
    }

    /// <summary>フォント・大きさ (pt)・行間を変える。</summary>
    public void SetFont(string family, double points, double lineHeight)
    {
        _fontFamily = family;
        _fontPoints = points;
        _lineHeight = lineHeight;
        Render();
    }

    /// <summary>描いている背景の色 (#AARRGGBB) とフォント (テスト用)。</summary>
    internal (string? Background, string Family, double Points, double LineHeight) StateForTest =>
        (Frame.Background is SolidColorBrush b ? $"#{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : null, _fontFamily, _fontPoints, _lineHeight);

    /// <summary>今の見た目で描き直す (配色を編集したとき)。</summary>
    public void Render()
    {
        if (Rows is null)
        {
            return;
        }

        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        bool dark = ActualTheme == ElementTheme.Dark;
        ColorScheme? scheme = highContrast ? null : _scheme;

        // 配色の色は利用者が設定した値 (配色ファイル) で、コードに直書きした色ではない。値がなければテーマの色。
        Brush Pick(SchemeElement element, Shape probe) =>
            scheme?.Get(element, dark) is { } c ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(c.A, c.R, c.G, c.B)) : probe.Fill;

        Brush background = Pick(SchemeElement.Background, ProbeBackground);
        Brush hexText = Pick(SchemeElement.HexText, ProbeText);
        Brush textText = Pick(SchemeElement.TextText, ProbeText);
        Brush offset = Pick(SchemeElement.OffsetText, ProbeOffset);
        Brush zero = Pick(SchemeElement.Zero, ProbeDim);
        Brush nonPrintable = Pick(SchemeElement.NonPrintable, ProbeText);
        Brush modified = Pick(SchemeElement.Modified, ProbeModified);
        Brush inserted = Pick(SchemeElement.Inserted, ProbeInserted);
        Brush selection = Pick(SchemeElement.SelectionBackground, ProbeSelection);
        Brush selectionText = Pick(SchemeElement.SelectionText, ProbeSelectionText);
        Brush caret = Pick(SchemeElement.Caret, ProbeCaret);
        Brush match = Pick(SchemeElement.Match, ProbeMatch);
        Brush alternate = Pick(SchemeElement.AlternateBackground, ProbeAlternate);
        Brush currentRow = Pick(SchemeElement.CurrentRowBackground, ProbeCurrentRow);
        Brush separator = Pick(SchemeElement.Separator, ProbeSeparator);

        Frame.Background = background;
        Rows.Children.Clear();
        var font = new FontFamily(_fontFamily);
        double size = _fontPoints * 96 / 72;
        double rowHeight = Math.Round(size * _lineHeight);
        for (int row = 0; row < Sample.Length / BytesPerRow; row++)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Height = rowHeight };
            line.Background = row == CurrentRow ? currentRow : row % 2 == 1 ? alternate : null;
            line.Children.Add(Cell((row * BytesPerRow).ToString("X8", System.Globalization.CultureInfo.InvariantCulture), offset, null, false, false));
            line.Children.Add(new Border { Width = size });
            for (int i = 0; i < BytesPerRow; i++)
            {
                int index = (row * BytesPerRow) + i;
                byte b = Sample[index];
                (Brush fore, Brush? back) = Paint(index, b == 0 ? zero : hexText);
                line.Children.Add(Cell(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture), fore, back,
                    ModifiedBytes.Contains(index), MatchBytes.Contains(index), index == CaretByte, i < BytesPerRow - 1));
            }

            line.Children.Add(new Border { Width = size / 2 });
            line.Children.Add(new Rectangle { Width = 1, Fill = separator });
            line.Children.Add(new Border { Width = size / 2 });
            for (int i = 0; i < BytesPerRow; i++)
            {
                int index = (row * BytesPerRow) + i;
                byte b = Sample[index];
                bool printable = b is >= 0x20 and < 0x7F;
                (Brush fore, Brush? back) = Paint(index, printable ? textText : b == 0 ? zero : nonPrintable);
                line.Children.Add(Cell(printable ? ((char)b).ToString() : ".", fore, back, ModifiedBytes.Contains(index), MatchBytes.Contains(index)));
            }

            Rows.Children.Add(line);
        }

        (Brush Fore, Brush? Back) Paint(int index, Brush normal) =>
            SelectedBytes.Contains(index) ? (selectionText, selection)
            : MatchBytes.Contains(index) ? (normal, match)
            : ModifiedBytes.Contains(index) ? (modified, null)
            : InsertedBytes.Contains(index) ? (inserted, null)
            : (normal, null);

        FrameworkElement Cell(string text, Brush fore, Brush? back, bool underline, bool outline, bool isCaret = false, bool gap = false)
        {
            var block = new TextBlock
            {
                Text = text,
                FontFamily = font,
                FontSize = size,
                Foreground = fore,
                VerticalAlignment = VerticalAlignment.Center,
                TextDecorations = underline ? Windows.UI.Text.TextDecorations.Underline : Windows.UI.Text.TextDecorations.None,
                IsTextScaleFactorEnabled = false,
            };
            return new Border
            {
                Child = block,
                Background = back,
                BorderBrush = outline ? fore : isCaret ? caret : null,
                BorderThickness = outline || isCaret ? new Thickness(1) : new Thickness(0),
                Padding = gap ? new Thickness(0, 0, size * 0.3, 0) : new Thickness(0),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
        }
    }
}
