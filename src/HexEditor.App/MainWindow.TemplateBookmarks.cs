using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Presentation;

namespace HexEditor.App;

/// <summary>
/// 型付きブックマーク (05 の INSP-29 の仕様 1・3・4) のうち、構造体テンプレートの型 (<c>tpl:パス|型名</c>) とユーザー定義型 (INSP-22。
/// <c>user:鍵</c>。テンプレートで定義した型だけ) の型。値と長さはブックマークの位置で型を評価して作り (追従と同じ上限)、書き換えは TPL-22 の規則。
/// 構造体テンプレートの型のブックマークを選ぶと、テンプレート結果パネルにその位置の構造体を出す (仕様 3)。
/// </summary>
public sealed partial class MainWindow
{
    internal const string TemplateBookmarkTypePrefix = "tpl:";
    internal const string UserBookmarkTypePrefix = "user:";

    /// <summary>ブックマークの位置の構造体を出すための適用 (ドキュメントごとに 1 つ。選ぶたびに置き換える)。</summary>
    private readonly Dictionary<DocumentTemplates, TemplateApplicationEntry> _bookmarkApplications = [];

    private static bool IsExtraBookmarkType(string? typeId) =>
        typeId is not null && (typeId.StartsWith(TemplateBookmarkTypePrefix, StringComparison.Ordinal) || typeId.StartsWith(UserBookmarkTypePrefix, StringComparison.Ordinal));

    /// <summary>「型」の欄の追加の選択肢: ユーザー定義型と、ドキュメントに適用しているテンプレートの型。</summary>
    private IReadOnlyList<(string Name, string Id)> ExtraBookmarkTypes()
    {
        var list = new List<(string, string)>();
        // プラグインの型は出さない (一覧の値の列は UI スレッドで作るため、戻らないプラグインの呼び出しで UI を止めない)
        foreach (InspectorUserType t in InspectorUserTypeService.Instance.Types.Where(t => t.Plugin is null))
        {
            list.Add((Loc.Format("Bookmark_TypeUser", t.DisplayName), UserBookmarkTypePrefix + t.Key));
        }

        if (Vm.Selected is { } doc && DocumentTemplates.Find(doc.Document) is { } templates)
        {
            foreach (TemplateApplicationEntry e in templates.Entries.Where(e => !e.IsFollow && e.Application.Program is not null)
                .DistinctBy(e => e.Info.TemplatePath, StringComparer.OrdinalIgnoreCase))
            {
                foreach (string type in e.Application.Program!.TypeNames.Order(StringComparer.Ordinal).Take(500))
                {
                    list.Add((Loc.Format("Bookmark_TypeTemplate", type, e.DisplayName), TemplateBookmarkTypePrefix + e.Info.TemplatePath + "|" + type));
                }
            }
        }

        return list;
    }

    /// <summary>テンプレートの型の ID を (パス, 型名) に分ける。</summary>
    private static (string Path, string Type)? SplitTemplateType(string typeId)
    {
        string rest = typeId[TemplateBookmarkTypePrefix.Length..];
        int bar = rest.LastIndexOf('|');
        return bar > 0 && bar < rest.Length - 1 ? (rest[..bar], rest[(bar + 1)..]) : null;
    }

    /// <summary>テンプレートのプログラム (適用中のものがあればそれ、なければファイルから読む)。</summary>
    private static TemplateProgram? ProgramOf(DocumentViewModel doc, string path)
    {
        if (DocumentTemplates.Find(doc.Document)?.Entries.FirstOrDefault(e => string.Equals(e.Info.TemplatePath, path, StringComparison.OrdinalIgnoreCase))
            ?.Application.Program is { } p)
        {
            return p;
        }

        try
        {
            TemplateProgram program = TemplateLoader.LoadFile(path, TemplateService.LoadOptions);
            return program.HasErrors ? null : program;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>ブックマークの位置で型を評価する (結果は呼び出し側が破棄する)。評価できなければ null。</summary>
    private static TemplateResult? EvaluateBookmarkType(string typeId, long start, DocumentViewModel doc, Endianness endian)
    {
        if (typeId.StartsWith(TemplateBookmarkTypePrefix, StringComparison.Ordinal))
        {
            if (SplitTemplateType(typeId) is not { } t || ProgramOf(doc, t.Path) is not { } program)
            {
                return null;
            }

            var options = new TemplateApplyOptions
            {
                TypeName = t.Type,
                StartOffset = start,
                BigEndian = endian == Endianness.Big,
                LimitOverride = TemplateApplication.FollowLimits,
            };
            return TemplateEngine.ApplyAndWait(program, TemplateDataSource.FromSnapshot(doc.Document.Current, doc.FilePath), options);
        }

        if (UserTypeOf(typeId) is { Plugin: null } user)
        {
            return InspectorUserTypeService.Instance.ApplyForEdit(user, BookmarkData(start, doc), endian);
        }

        return null;
    }

    private static InspectorUserType? UserTypeOf(string typeId) =>
        InspectorUserTypeService.Instance.Types.FirstOrDefault(t => UserBookmarkTypePrefix + t.Key == typeId);

    /// <summary>ブックマークの位置から最大 4 KB。</summary>
    private static byte[] BookmarkData(long start, DocumentViewModel doc)
    {
        DocumentSnapshot snapshot = doc.Document.Current;
        int count = (int)Math.Clamp(snapshot.Length - start, 0, InspectorDecoder.MaxReadLength);
        byte[] data = new byte[count];
        var states = new ByteState[count];
        count = count > 0 ? snapshot.ReadForDisplay(start, data, states) : 0;
        return data[..count];
    }

    private static TemplateNode? FirstNode(TemplateResult r) => r.Invoke(() => r.Root.Children.Count > 0 ? r.Root.Children[0] : null);

    /// <summary>値の列の文字列 (評価に失敗したら空)。</summary>
    private string ExtraBookmarkValue(Bookmark b, DocumentViewModel doc, Endianness endian)
    {
        using TemplateResult? r = EvaluateBookmarkType(b.TypeId!, b.Start, doc, endian);
        return r is not null && FirstNode(r) is { } node ? r.Invoke(() => node.HasError ? string.Empty : node.GetDisplay(TemplateDisplayContext.Current)) : string.Empty;
    }

    /// <summary>型の長さ (評価した大きさ。INSP-29 の仕様 1)。評価できなければ null。</summary>
    internal long? ExtraBookmarkTypeLength(Bookmark b, string typeId)
    {
        if (Vm.Selected is not { } doc)
        {
            return null;
        }

        using TemplateResult? r = EvaluateBookmarkType(typeId, b.Start, doc, b.TypeEndian ?? (doc.Editor.View.BigEndian ? Endianness.Big : Endianness.Little));
        return r is not null && FirstNode(r) is { } node && node.Size > 0 ? node.Size : null;
    }

    /// <summary>値の列の書き換え。書き込めなければ理由。</summary>
    private string? CommitExtraBookmarkValue(Bookmark b, DocumentViewModel doc, Endianness endian, string text)
    {
        using TemplateResult? r = EvaluateBookmarkType(b.TypeId!, b.Start, doc, endian);
        if (r is null || FirstNode(r) is not { } node)
        {
            return Loc.Get("Tpl_EditInvalid");
        }

        TemplateEditPlan plan = TemplateInspectorTypes.PlanEdit(node, text);
        if (!plan.Success || plan.Bytes is not { } planned)
        {
            return Loc.Get("Tpl_EditInvalid");
        }

        // テンプレートの型 (tpl:) はドキュメントの位置のまま、ユーザー定義型 (user:) はブックマークの位置からのデータで評価している
        long at = b.TypeId!.StartsWith(TemplateBookmarkTypePrefix, StringComparison.Ordinal) ? plan.Offset : b.Start + plan.Offset;
        doc.Document.Overwrite(at, planned, Loc.Get("Bookmarks_ValueUndo"));
        return null;
    }

    /// <summary>構造体テンプレートの型のブックマークを選んだ: テンプレート結果パネルにその位置の構造体を出す (INSP-29 の仕様 3)。</summary>
    private void ShowBookmarkStruct(Bookmark b)
    {
        if (b.TypeId is not { } typeId || !typeId.StartsWith(TemplateBookmarkTypePrefix, StringComparison.Ordinal)
            || SplitTemplateType(typeId) is not { } t || Vm.Selected is not { } doc)
        {
            return;
        }

        DocumentTemplates templates = DocumentTemplates.For(doc);
        if (_bookmarkApplications.Remove(templates, out TemplateApplicationEntry? previous) && templates.Entries.Contains(previous))
        {
            templates.Remove(previous);
        }

        var info = new TemplateApplicationInfo
        {
            TemplatePath = t.Path,
            StartOffset = b.Start,
            TypeName = t.Type,
            BigEndian = b.TypeEndian is { } e ? e == Endianness.Big : null,
        };
        if (AddTemplateApplication(doc, info) is { } entry)
        {
            _bookmarkApplications[templates] = entry;
            ShowPanel(TemplatesPanelId, focus: false);
        }
    }
}
