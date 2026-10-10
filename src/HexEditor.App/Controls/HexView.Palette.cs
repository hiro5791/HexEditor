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

        public Brush For(CellKind kind) => kind switch
        {
            CellKind.Modified => Modified,
            CellKind.Loading or CellKind.Empty => Dim,
            _ => Text,
        };
    }
}
