using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Inspector;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Presentation;

namespace HexEditor.App.ViewModels;

/// <summary>
/// テンプレートの型をデータインスペクタで使う (TPL-29、INSP-22)。設定「インスペクタ用テンプレート」の <c>[[inspector("表示名")]]</c> の型を、
/// 「ユーザー定義」のグループの行にする。カーソル位置 (起点) で追従と同じ上限で評価し、書き換えは結果ツリーと同じ規則 (TPL-22)。
/// </summary>
public sealed partial class InspectorViewModel
{
    private const string TemplateRowPrefix = "tpl:";

    private readonly Dictionary<string, TemplateNode?> _templateNodes = [];

    private static bool IsTemplateRow(InspectorItemViewModel item) => item.TypeId.StartsWith(TemplateRowPrefix, StringComparison.Ordinal);

    /// <summary>ユーザー定義の型の行を加える (Refresh から呼ぶ)。</summary>
    private void AppendTemplateRows(DocumentViewModel doc, List<InspectorItemViewModel> wanted)
    {
        IReadOnlyList<TemplateInspectorType> types = TemplateService.InspectorTypes.Types;
        if (types.Count == 0)
        {
            return;
        }

        InspectorItemViewModel header = Item(new InspectorItemViewModel(InspectorItemKind.Group, InspectorGroup.User, TemplateRowPrefix + "group"));
        header.Name = Loc.Get("Inspector_Group_" + InspectorGroup.User);
        header.IsCollapsed = _collapsed.Contains(InspectorGroup.User);
        wanted.Add(header);
        if (header.IsCollapsed)
        {
            return;
        }

        TemplateDataSource data = TemplateDataSource.FromSnapshot(doc.Document.Current, doc.FilePath);
        foreach (TemplateInspectorType type in types)
        {
            string id = TemplateRowPrefix + type.TemplatePath + "|" + type.TypeName;
            InspectorItemViewModel row = Item(new InspectorItemViewModel(InspectorItemKind.Row, InspectorGroup.User, id));
            row.Name = type.DisplayName;
            TemplateInspectorValue value = TemplateService.InspectorTypes.Evaluate(type, data, Origin, out _);
            _templateNodes[id] = value.Node;
            row.ByteOffset = 0;
            row.ByteCount = value.Node is { } n ? (int)Math.Min(int.MaxValue, n.Size) : 0;
            row.Status = value.Error is null ? InspectorStatus.Ok : InspectorStatus.NotEnoughData;
            if (!row.IsEditing)
            {
                row.Value = value.Error is null ? value.Display : "—";
            }

            // 評価に失敗したら「—」とツールチップで理由 (TPL-29 の「エラー」)
            row.ToolTip = value.Error ?? (ReadOnly ? Loc.Get("Inspector_ReadOnlyTip") : null);
            row.CanExpand = value.Fields.Count > 0;
            row.IsExpanded = _expanded.Contains(row.Key);
            wanted.Add(row);
            if (row.IsExpanded)
            {
                foreach ((string name, string v) in value.Fields)
                {
                    InspectorItemViewModel part = Item(new InspectorItemViewModel(InspectorItemKind.Component, InspectorGroup.User, id, false, name));
                    part.Name = name;
                    part.Value = v;
                    wanted.Add(part);
                }
            }
        }
    }

    private bool BeginTemplateEdit(InspectorItemViewModel item)
    {
        if (!_templateNodes.TryGetValue(item.TypeId, out TemplateNode? node) || node is null)
        {
            return false;
        }

        CancelEdit();
        item.EditText = item.Value;
        item.IsEditing = true;
        UpdateTemplateEdit(item, item.EditText);
        return true;
    }

    private void UpdateTemplateEdit(InspectorItemViewModel item, string text)
    {
        item.EditText = text;
        TemplateEditPlan plan = _templateNodes.TryGetValue(item.TypeId, out TemplateNode? node) && node is not null
            ? TemplateInspectorTypes.PlanEdit(node, text)
            : new TemplateEditPlan(0, null, TemplateEditError.Blocked, null);
        item.Preview = plan.Bytes is { } b ? string.Join(' ', b.Select(x => x.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))) : string.Empty;
        item.Error = plan.Success ? string.Empty : Loc.Get("Tpl_EditInvalid");
    }

    private bool CommitTemplateEdit(InspectorItemViewModel item)
    {
        if (_document is not { } doc || !_templateNodes.TryGetValue(item.TypeId, out TemplateNode? node) || node is null)
        {
            return false;
        }

        TemplateEditPlan plan = TemplateInspectorTypes.PlanEdit(node, item.EditText);
        if (!plan.Success || ReadOnly)
        {
            item.Error = ReadOnly ? Loc.Get("Inspector_ReadOnlyTip") : Loc.Get("Tpl_EditInvalid");
            return false;
        }

        item.IsEditing = false;
        doc.Document.Overwrite(plan.Offset, plan.Bytes!, Loc.Get("Inspector_UndoDescription"));
        Refresh();
        return true;
    }

    /// <summary>テスト用: インスペクタの行の値を書き換える (入力と確定)。</summary>
    internal bool EditForTest(string name, string text)
    {
        InspectorItemViewModel? row = Items.FirstOrDefault(i => i.Kind == InspectorItemKind.Row && i.Name == name);
        if (row is null || !BeginEdit(row))
        {
            return false;
        }

        UpdateEdit(row, text);
        return CommitEdit(row);
    }
}
