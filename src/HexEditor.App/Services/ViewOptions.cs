using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.ViewModels;
using HexEditor.Core.Settings;
using HexEditor.Core.View;

namespace HexEditor.App.Services;

/// <summary>
/// Hex 表示の設定 (UI-28 配色、UI-29 フォント、VIEW-07 ツールチップ、VIEW-15 の仕様 8、UI-51 の読み上げの詳しさ) と、
/// ドキュメントごとの表示設定 (VIEW-42)。設定画面ができるまでは settings.json (UI-23) と表示メニューで変える。
/// </summary>
public static class ViewOptions
{
    /// <summary>Hex 表示のフォント (UI-29 の仕様 2。既定 Cascadia Mono、ない場合は Consolas)。</summary>
    public const string FontFamilyKey = "view.font.family";

    /// <summary>Hex 表示の大きさ (UI-29 の仕様 3。6〜72 pt、0.5 pt 刻み、既定 10 pt)。</summary>
    public const string FontSizeKey = "view.font.size";

    /// <summary>行間 (UI-29 の仕様 4。1.0〜2.0 倍、既定 1.2)。</summary>
    public const string LineHeightKey = "view.font.lineHeight";

    /// <summary>テキスト列の代替フォント (UI-29 の仕様 5。既定は空)。</summary>
    public const string FallbackFontKey = "view.font.fallback";

    /// <summary>Windows の文字の大きさに合わせる (UI-29 の仕様 7。既定 true)。</summary>
    public const string FollowTextScalingKey = "view.font.followTextScaling";

    /// <summary>配色の名前 (UI-28。既定 default)。</summary>
    public const string ColorSchemeKey = "view.colorScheme";

    /// <summary>「ツールチップを表示する」(VIEW-07。既定 true)。</summary>
    public const string ToolTipsKey = "view.tooltips";

    /// <summary>「保存後も閉じるまで強調を残す」(VIEW-15 の仕様 8。既定 false)。</summary>
    public const string KeepChangesKey = "view.modified.keepAfterSave";

    /// <summary>「削除位置を表示」(VIEW-15 の仕様 5。既定 false)。</summary>
    public const string ShowDeletionsKey = "view.modified.showDeletions";

    /// <summary>表示しない文字の記号 (VIEW-21 の仕様 7。dot / space / controlPictures、既定 dot)。</summary>
    public const string NonPrintableKey = "view.text.nonPrintable";

    /// <summary>不正なバイトの記号 (VIEW-22 の仕様 5。dot / replacement (U+FFFD)、既定 dot)。</summary>
    public const string InvalidSymbolKey = "view.text.invalidSymbol";

    /// <summary>読み上げの詳しさ (UI-51 の仕様 2。full / brief / offsetOnly / none、既定 full)。</summary>
    public const string VerbosityKey = "a11y.announce.verbosity";

    public const double DefaultFontPoints = 10;

    /// <summary>ドキュメントに付随するデータの置き場所 (前回の位置・ブックマークと同じ。App が起動時に設定する)。</summary>
    public static Core.Files.DocumentDataStore Documents { get; set; } = null!;

    private static readonly ConditionalWeakTable<DocumentViewModel, object> Attached = [];

    /// <summary>Hex ビューに設定を反映する。</summary>
    public static void ApplyTo(HexView view, SettingsStore settings)
    {
        string? configured = settings.GetString(FontFamilyKey, string.Empty) is { Length: > 0 } f ? f : null;
        string family = FontCatalog.Resolve(configured, out string? missing);
        string fallback = settings.GetString(FallbackFontKey, string.Empty);
        string familyList = string.IsNullOrWhiteSpace(fallback) ? family : family + ", " + fallback;

        // フォントの一覧ができる前の仮の指定 (XAML の代替) で、すでに同じフォントで描いているなら替えない。替えると全部の文字の幅を
        // 測り直して描き直すため、遅い PC では起動の数秒後に UI スレッドが数百 ms 止まっていた (ENG-06、VIEW-03)。
        // (仮の指定の先頭のフォントが入っていて、決めたフォントと同じとき。先頭がなければ XAML は次の候補で描いているので替える)。
        bool sameFont = string.IsNullOrWhiteSpace(fallback) && view.HexFontFamily == FontCatalog.Provisional(configured)
            && FontCatalog.IsReady && view.HexFontFamily.Split(',')[0].Trim() == family && FontCatalog.IsInstalled(family);
        if (view.HexFontFamily != familyList && !sameFont)
        {
            view.HexFontFamily = familyList;
            if (FontCatalog.IsReady)
            {
                AppLog.Info($"HexView font: {family}" + (missing is null ? string.Empty : $" ({missing} is not installed)"));
            }
        }

        double points = Math.Clamp(Math.Round(GetDouble(settings, FontSizeKey, DefaultFontPoints) * 2) / 2, 6, 72);
        double size = points * 96 / 72;
        if (Math.Abs(view.HexFontSize - size) > 0.001)
        {
            view.HexFontSize = size;
        }

        double lineHeight = Math.Clamp(GetDouble(settings, LineHeightKey, 1.2), 1.0, 2.0);
        if (view.LineSpacing != lineHeight)
        {
            view.LineSpacing = lineHeight;
        }

        bool follow = settings.GetBool(FollowTextScalingKey, true);
        if (view.FollowTextScaling != follow)
        {
            view.FollowTextScaling = follow;
        }

        ColorScheme scheme = new ColorSchemeStore(settings.Folder).Find(settings.GetString(ColorSchemeKey, ColorScheme.DefaultName));
        // 独自の配色はファイルが編集されているかもしれないので、毎回読み直したものを使う。
        ColorScheme? wanted = scheme.Name == ColorScheme.DefaultName ? null : scheme;
        if (view.ColorScheme?.Name != wanted?.Name || wanted is { BuiltIn: false })
        {
            view.ColorScheme = wanted;
        }

        view.ShowToolTips = settings.GetBool(ToolTipsKey, true);
        view.ShowDeletions = settings.GetBool(ShowDeletionsKey, false);
        view.NonPrintableStyle = settings.GetString(NonPrintableKey, "dot") switch
        {
            "space" => NonPrintableStyle.Space,
            "controlPictures" => NonPrintableStyle.ControlPictures,
            _ => NonPrintableStyle.Dot,
        };
        // オフセット列を固定 (VIEW-28 の仕様 5。既定オン)。
        view.KeepOffsetColumnFixed = settings.GetBool("view.scroll.fixedOffsetColumn", true);

        // 描画性能の診断表示 (VIEW-04 の仕様 8。設定の「詳細」の診断の項目。既定オフ)。
        view.DiagnosticsSetting = settings.GetBool("diagnostics.hexView.overlay", false);

        // スクロールバーの印 (VIEW-02 の仕様 9。既定はカーソル位置と検索結果だけ)。
        view.ShowCursorMarker = settings.GetBool("view.scrollBar.cursorMark", true);
        view.ShowSearchMarkers = settings.GetBool("view.scrollBar.searchMarks", true);
        view.ShowSelectionMarker = settings.GetBool("view.scrollBar.selectionMark", false);
        view.ShowBookmarkMarkers = settings.GetBool("view.scrollBar.bookmarkMarks", false);
        view.ShowDiffMarkers = settings.GetBool("view.scrollBar.diffMarks", false);
        view.RefreshMarkers();
        view.InvalidSymbol = settings.GetString(InvalidSymbolKey, "dot") == "replacement"
            ? TextCellDecoder.ReplacementInvalidSymbol
            : TextCellDecoder.DefaultInvalidSymbol;
        view.AnnouncementVerbosity = settings.GetString(VerbosityKey, "full") switch
        {
            "brief" => AnnounceVerbosity.Brief,
            "offsetOnly" => AnnounceVerbosity.OffsetOnly,
            "none" => AnnounceVerbosity.None,
            _ => AnnounceVerbosity.Full,
        };
    }

    /// <summary>
    /// ドキュメントに表示設定を結び付ける (1 回だけ): 全体の既定値とドキュメントごとの設定を読み込み、変わったら付随データに保存する
    /// (VIEW-42 の仕様 2・3)。
    /// </summary>
    public static void Attach(SettingsStore settings, DocumentViewModel doc)
    {
        doc.Editor.KeepChangesAfterSave = settings.GetBool(KeepChangesKey, false);
        if (Attached.TryGetValue(doc, out _))
        {
            return;
        }

        Attached.Add(doc, new object());
        var store = new ViewSettingsStore(settings, Documents);

        // データソースの種類ごとの既定値 (VIEW-42 の仕様 2 の 3)。ファイルにはない (ディスクなどのデータソースが示す)。
        System.Text.Json.Nodes.JsonObject? sourceDefaults = (doc.Document.Source as IViewDefaultsSource)?.ViewDefaults;
        ViewSettings initial;
        long? reference = null;
        if (doc.FilePath is { } path)
        {
            (initial, reference) = store.Load(path, sourceDefaults);

            // 拡張子で自動適用するプリセット (VIEW-42 の仕様 2 の 4・仕様 6): 初めて開いたときだけ。
            if (store.TakeAutoPreset(path) is { } preset)
            {
                initial = preset.ApplyTo(initial);
                AppLog.Info($"View preset applied: {preset.Name}");
            }
        }
        else
        {
            initial = store.DefaultsFor(sourceDefaults);
        }

        // 分割を先に戻している (VIEW-37 の仕様 10) 場合は、どのペインにも同じ設定を使う。
        foreach (EditorState pane in doc.Panes)
        {
            pane.ApplyView(initial);
            if (doc.FilePath is not null)
            {
                pane.SetReferencePoint(reference);
            }
        }

        // 表示設定を変えたら、操作中のペインの設定をドキュメントごとの設定として保存する (VIEW-42 の仕様 3)。分割した 2 つ目のペインや、
        // 2 つ目のペインを残して分割を解除した後のペインも対象にするため、ペインが替わるたびにつなぎ直す。
        var hooked = new List<EditorState>();
        void Changed(object? sender, EventArgs e)
        {
            if (ReferenceEquals(sender, doc.Editor) && doc.FilePath is { } p && !doc.Document.IsDisposed)
            {
                store.Save(p, doc.Editor.View, doc.Editor.ReferencePoint, sourceDefaults);
            }
        }

        void Rehook()
        {
            foreach (EditorState gone in hooked.Where(e => !doc.Panes.Contains(e)).ToList())
            {
                gone.ViewChanged -= Changed;
                hooked.Remove(gone);
            }

            foreach (EditorState pane in doc.Panes.Where(e => !hooked.Contains(e)))
            {
                pane.ViewChanged += Changed;
                hooked.Add(pane);
            }
        }

        Rehook();
        doc.PanesChanged += (_, _) => Rehook();
    }

    /// <summary>「既定として保存」(VIEW-42 の仕様 4)。</summary>
    public static void SaveAsDefault(SettingsStore settings, EditorState editor) => new ViewSettingsStore(settings, Documents).SaveDefaults(editor.View);

    /// <summary>「既定に戻す」(VIEW-42 の仕様 5): ドキュメントごとの設定を消し、全体の既定値に戻す。</summary>
    public static void ResetToDefault(SettingsStore settings, DocumentViewModel doc)
    {
        var store = new ViewSettingsStore(settings, Documents);
        if (doc.FilePath is { } path)
        {
            store.Remove(path);
        }

        doc.Editor.ClearReferencePoint();
        doc.Editor.ApplyView(store.DefaultsFor((doc.Document.Source as IViewDefaultsSource)?.ViewDefaults));
    }

    private static double GetDouble(SettingsStore settings, string key, double defaultValue) =>
        settings.GetNode(key) is JsonValue v && v.TryGetValue(out double d) ? d
        : double.TryParse(settings.GetString(key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed
        : defaultValue;
}
