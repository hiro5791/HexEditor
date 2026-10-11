using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HexEditor.App.Controls;

/// <summary>
/// 配色 (VIEW-01 の仕様 10、UI-28)。既定の色は XAML の ThemeDictionaries (Light / Dark / HighContrast) から取り、テーマと
/// ハイコントラストの切り替えに追従する。配色 (UI-28) の値のある要素だけ、その色で置き換える。ハイコントラストでは配色を使わず、
/// システム色だけを使う (UI-28 の仕様 6)。
/// </summary>
public sealed partial class HexView
{
    internal sealed record Palette(
        Brush Text,
        Brush OffsetText,
        Brush Modified,
        Brush Dim,
        Brush Selection,
        Brush SelectionText,
        Brush SelectionInactive,
        Brush SelectionInactiveText,
        Brush Caret,
        Brush Hatch,
        Brush Background,
        Brush CursorMarker,
        Brush SearchMarker,
        Brush Match,
        Brush MatchText,
        Brush HexText,
        Brush TextText,
        Brush Zero,
        Brush NonPrintable,
        Brush Inserted,
        Brush SavedChange,
        Brush Invalid,
        Brush Alternate,
        Brush CurrentRow,
        Brush CurrentRowLine,
        Brush RulerText,
        Brush RulerHighlight,
        Brush RulerHighlightText,
        Brush FocusRange,
        Brush Separator,
        Brush SecondaryCaret,
        Brush RecordAlternate,
        Brush Difference,
        bool HighContrast)
    {
        /// <summary>文字色の置き換え (VIEW-17 の仕様 9) の結果。色の組ごとに 1 回だけ計算する。</summary>
        public ContrastCache Contrast { get; } = new();

        /// <summary>テーマのリソースから作り、配色の値のある要素を置き換える。</summary>
        public static Palette Load(HexView view, ColorScheme? scheme, bool highContrast)
        {
            if (highContrast && view.ForcedHighContrast)
            {
                return SystemColors();
            }

            var p = new Palette(
                view.ProbeText.Fill,
                view.ProbeOffset.Fill,
                view.ProbeModified.Fill,
                view.ProbeDim.Fill,
                view.ProbeSelection.Fill,
                view.ProbeSelectionText.Fill,
                view.ProbeSelectionInactive.Fill,
                view.ProbeSelectionInactiveText.Fill,
                view.ProbeCaret.Fill,
                view.ProbeHatch.Fill,
                view.ProbeBackground.Fill,
                view.ProbeCursorMarker.Fill,
                view.ProbeSearchMarker.Fill,
                view.ProbeMatch.Fill,
                view.ProbeMatchText.Fill,
                view.ProbeText.Fill,
                view.ProbeText.Fill,
                view.ProbeDim.Fill,
                view.ProbeText.Fill,
                view.ProbeInserted.Fill,
                view.ProbeSavedChange.Fill,
                view.ProbeInvalid.Fill,
                view.ProbeAlternate.Fill,
                view.ProbeCurrentRow.Fill,
                view.ProbeCurrentRowLine.Fill,
                view.ProbeRulerText.Fill,
                view.ProbeRulerHighlight.Fill,
                view.ProbeRulerHighlightText.Fill,
                view.ProbeFocusRange.Fill,
                view.ProbeSeparator.Fill,
                view.ProbeSecondaryCaret.Fill,
                view.ProbeRecordAlternate.Fill,
                view.ProbeDifference.Fill,
                highContrast);
            if (highContrast || scheme is null)
            {
                return p;
            }

            bool dark = view.ActualTheme == ElementTheme.Dark;
            // 配色の色は利用者が設定した値 (配色ファイル) で、コードに直書きした色ではない。
            Brush Pick(SchemeElement element, Brush fallback) =>
                scheme.Get(element, dark) is { } c ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(c.A, c.R, c.G, c.B)) : fallback;

            Brush hexText = Pick(SchemeElement.HexText, p.HexText);
            return p with
            {
                Background = Pick(SchemeElement.Background, p.Background),
                Alternate = Pick(SchemeElement.AlternateBackground, p.Alternate),
                OffsetText = Pick(SchemeElement.OffsetText, p.OffsetText),
                HexText = hexText,
                Text = hexText,
                TextText = Pick(SchemeElement.TextText, p.TextText),
                Zero = Pick(SchemeElement.Zero, p.Zero),
                NonPrintable = Pick(SchemeElement.NonPrintable, p.NonPrintable),
                Modified = Pick(SchemeElement.Modified, p.Modified),
                Inserted = Pick(SchemeElement.Inserted, p.Inserted),
                Selection = Pick(SchemeElement.SelectionBackground, p.Selection),
                SelectionText = Pick(SchemeElement.SelectionText, p.SelectionText),
                Caret = Pick(SchemeElement.Caret, p.Caret),
                CurrentRow = Pick(SchemeElement.CurrentRowBackground, p.CurrentRow),
                Match = Pick(SchemeElement.Match, p.Match),
                Separator = Pick(SchemeElement.Separator, p.Separator),
                RecordAlternate = Pick(SchemeElement.RecordAlternate, p.RecordAlternate),
                Difference = Pick(SchemeElement.DiffChanged, p.Difference),
            };
        }

        /// <summary>
        /// ハイコントラストの配色を、Windows のシステム色のリソース (SystemColorWindowColor など) から直接作る。テスト用のビルドで
        /// ハイコントラストの描き方を、OS の設定を変えずに確かめるために使う。
        /// </summary>
        private static Palette SystemColors()
        {
            static Brush Sys(string key) => new SolidColorBrush(Application.Current.Resources[key] is Color c ? c : default);
            Brush window = Sys("SystemColorWindowColor");
            Brush text = Sys("SystemColorWindowTextColor");
            Brush gray = Sys("SystemColorGrayTextColor");
            Brush highlight = Sys("SystemColorHighlightColor");
            Brush highlightText = Sys("SystemColorHighlightTextColor");
            Brush hot = Sys("SystemColorHotlightColor");
            return new Palette(
                text, text, hot, gray, highlight, highlightText, highlight, highlightText, text, gray, window, highlight, hot, hot, window,
                text, text, gray, text, text, text, gray, window, window, highlight, text, highlight, highlightText, window, text, gray,
                window, hot, HighContrast: true);
        }

        /// <summary>
        /// 背景 <paramref name="under"/> (行の下の面の層。null なら通常の背景) と <paramref name="top"/> (手前の層の背景) の上の文字色
        /// <paramref name="fore"/> を、コントラストの規則 (VIEW-17 の仕様 9) で読める色にする。<paramref name="normal"/> は通常の文字色。
        /// ハイコントラストでは置き換えない (システム色だけを使う)。
        /// </summary>
        public Brush Readable(Brush fore, Brush? under, Brush? top, Brush normal) =>
            HighContrast || (under is null && top is null && (ReferenceEquals(fore, normal) || ReferenceEquals(fore, Text)))
                ? fore
                : Contrast.Readable(fore, under, top, normal, Background);

        public Brush For(CellKind kind) => kind switch
        {
            CellKind.Modified => Modified,
            CellKind.Loading or CellKind.Empty or CellKind.NoData => Dim,
            _ => Text,
        };
    }

    /// <summary>
    /// 文字色の置き換え (VIEW-17 の仕様 9) のキャッシュ。ブラシの組 (参照) ごとに結果を持ち、描画のたびに色を計算しない
    /// (VIEW-04 の仕様 3)。同じ組が続くことが多いので、直前の結果も持つ。
    /// </summary>
    internal sealed class ContrastCache
    {
        /// <summary>覚える組の数の上限 (提供元が毎回ブラシを作っても増え続けないように)。</summary>
        private const int MaxEntries = 4096;

        private readonly Dictionary<(Brush Fore, Brush? Under, Brush? Top, Brush Normal), Brush> _map = new(KeyComparer.Instance);
        private (Brush Fore, Brush? Under, Brush? Top, Brush Normal) _lastKey;
        private Brush? _last;

        public Brush Readable(Brush fore, Brush? under, Brush? top, Brush normal, Brush background)
        {
            var key = (fore, under, top, normal);
            if (_last is not null && KeyComparer.Instance.Equals(key, _lastKey))
            {
                return _last;
            }

            if (!_map.TryGetValue(key, out Brush? result))
            {
                result = Compute(fore, under, top, normal, background);
                if (_map.Count >= MaxEntries)
                {
                    _map.Clear();
                }

                _map[key] = result;
            }

            _lastKey = key;
            _last = result;
            return result;
        }

        private static Brush Compute(Brush fore, Brush? under, Brush? top, Brush normal, Brush background)
        {
            if (fore is not SolidColorBrush f || normal is not SolidColorBrush n || background is not SolidColorBrush b)
            {
                return fore;
            }

            SchemeColor normalColor = ToScheme(n.Color);

            // 通常の背景が透明なら、通常の文字色が暗ければ明るいテーマ (白の上)、明るければ暗いテーマ (黒の上) とみなす。
            SchemeColor back = CellContrast.Flatten(ToScheme(b.Color), normalColor.Luminance() < 0.5,
                (under as SolidColorBrush)?.Color is { } u ? ToScheme(u) : null, (top as SolidColorBrush)?.Color is { } t ? ToScheme(t) : null);
            if (CellContrast.Replacement(ToScheme(f.Color), back, normalColor) is not { } replacement)
            {
                return fore;
            }

            // 置き換えの色は通常の文字色 (テーマのリソース) か、それでも読めない背景のときだけ計算で求めた黒・白。
            return replacement == normalColor ? normal
                : new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(replacement.A, replacement.R, replacement.G, replacement.B));
        }

        private static SchemeColor ToScheme(Color c) => new(c.A, c.R, c.G, c.B);

        /// <summary>ブラシは参照で比べる (色を読まない)。</summary>
        private sealed class KeyComparer : IEqualityComparer<(Brush Fore, Brush? Under, Brush? Top, Brush Normal)>
        {
            public static readonly KeyComparer Instance = new();

            public bool Equals((Brush Fore, Brush? Under, Brush? Top, Brush Normal) x, (Brush Fore, Brush? Under, Brush? Top, Brush Normal) y) =>
                ReferenceEquals(x.Fore, y.Fore) && ReferenceEquals(x.Under, y.Under) && ReferenceEquals(x.Top, y.Top) && ReferenceEquals(x.Normal, y.Normal);

            public int GetHashCode((Brush Fore, Brush? Under, Brush? Top, Brush Normal) k) => HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Fore),
                k.Under is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Under),
                k.Top is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Top),
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Normal));
        }
    }
}
