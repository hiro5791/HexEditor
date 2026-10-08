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

    /// <summary>読み上げの詳しさ (UI-51 の仕様 2。full / brief / offsetOnly / none、既定 full)。</summary>
    public const string VerbosityKey = "a11y.announce.verbosity";

    public const double DefaultFontPoints = 10;

    private static readonly ConditionalWeakTable<DocumentViewModel, object> Attached = [];

    /// <summary>Hex ビューに設定を反映する。</summary>
    public static void ApplyTo(HexView view, SettingsStore settings)
    {
        string family = FontCatalog.Resolve(settings.GetString(FontFamilyKey, string.Empty) is { Length: > 0 } f ? f : null, out string? missing);
        string fallback = settings.GetString(FallbackFontKey, string.Empty);
        string familyList = string.IsNullOrWhiteSpace(fallback) ? family : family + ", " + fallback;
        if (view.HexFontFamily != familyList)
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
        var store = new ViewSettingsStore(settings);
        EditorState editor = doc.Editor;
        if (doc.FilePath is { } path)
        {
            (ViewSettings view, long? reference) = store.Load(path);
            editor.ApplyView(view);
            editor.SetReferencePoint(reference);
        }
        else
        {
            editor.ApplyView(store.Defaults);
        }

        editor.ViewChanged += (_, _) =>
        {
            if (doc.FilePath is { } p && !doc.Document.IsDisposed)
            {
                store.Save(p, editor.View, editor.ReferencePoint);
            }
        };
    }

    /// <summary>「既定として保存」(VIEW-42 の仕様 4)。</summary>
    public static void SaveAsDefault(SettingsStore settings, EditorState editor) => new ViewSettingsStore(settings).SaveDefaults(editor.View);

    /// <summary>「既定に戻す」(VIEW-42 の仕様 5): ドキュメントごとの設定を消し、全体の既定値に戻す。</summary>
    public static void ResetToDefault(SettingsStore settings, DocumentViewModel doc)
    {
        var store = new ViewSettingsStore(settings);
        if (doc.FilePath is { } path)
        {
            store.Remove(path);
        }

        doc.Editor.ClearReferencePoint();
        doc.Editor.ApplyView(store.Defaults);
    }

    private static double GetDouble(SettingsStore settings, string key, double defaultValue) =>
        settings.GetNode(key) is JsonValue v && v.TryGetValue(out double d) ? d
        : double.TryParse(settings.GetString(key, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed
        : defaultValue;
}
