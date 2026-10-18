using System.Globalization;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Presentation;

namespace HexEditor.App.ViewModels;

/// <summary>
/// ユーザー定義型 (INSP-22) の行: 「ユーザー定義」のグループに、設定フォルダの <c>inspector/</c> のテンプレートの型、設定「インスペクタ用
/// テンプレート」の型 (07 の TPL-29)、有効なプラグインのインスペクタの型 (08 の AUTO-31) を出す。読み込みと解釈は <see cref="InspectorUserTypes"/>
/// の 1 つの経路。解釈は別のスレッドで行い、終わるまで値を「…」にする (UI を止めない)。時間切れ・エラーは起点 (またはデータ) が変わるまで
/// 再実行しない (仕様 3)。構造体はフィールドを展開できる行にする。書き換えは、テンプレートの型は TPL-22 の規則、プラグインの型は変換
/// (表示文字列からバイト列) があるものだけ (仕様 5)。
/// </summary>
public sealed partial class InspectorViewModel
{
    private const string UserRowPrefix = "user:";

    /// <summary>ユーザー定義型の定義 (アプリ全体。null なら行を出さない)。</summary>
    public InspectorUserTypes? UserTypes { get; set; }

    /// <summary>解釈の結果の控え (起点とデータの版が同じ間だけ使う)。</summary>
    private readonly Dictionary<string, InspectorUserValue> _userValues = [];
    private readonly HashSet<string> _userPending = [];
    private (long Origin, DocumentSnapshot? Snapshot, Endianness Endian) _userKey;

    /// <summary>書き換え中のテンプレートの型の評価 (確定・取り消しで破棄する)。</summary>
    private (string TypeId, TemplateResult Result, TemplateNode Node)? _userEdit;

    /// <summary>ユーザー定義型の解釈が終わった (テストで待つ)。</summary>
    public event EventHandler? UserTypesEvaluated;

    /// <summary>定義ファイルを読み込み直した: 控えを捨てて解釈し直す。</summary>
    public void UserTypesReloaded()
    {
        _userValues.Clear();
        Refresh();
    }

    /// <summary>ユーザー定義型の行を加える (<see cref="Refresh"/> から呼ぶ)。</summary>
    private void AppendUserTypeRows(DocumentViewModel doc, List<InspectorItemViewModel> wanted)
    {
        IReadOnlyList<InspectorUserType> types = UserTypes?.Types ?? [];
        if (types.Count == 0)
        {
            return;
        }

        // 起点・データ・エンディアンが変わったら控えを捨てる (時間切れも、起点が変わるまで再実行しない)。
        var key = (Origin, doc.Document.Current, Endian);
        if (key.Origin != _userKey.Origin || !ReferenceEquals(key.Current, _userKey.Snapshot) || key.Endian != _userKey.Endian)
        {
            _userKey = (key.Origin, key.Current, key.Endian);
            _userValues.Clear();
        }

        InspectorItemViewModel header = Item(new InspectorItemViewModel(InspectorItemKind.Group, InspectorGroup.User, UserRowPrefix + "group"));
        header.Name = Loc.Get("Inspector_Group_" + InspectorGroup.User);
        header.IsCollapsed = _collapsed.Contains(InspectorGroup.User);
        wanted.Add(header);
        if (header.IsCollapsed)
        {
            return;
        }

        foreach (InspectorUserType type in types)
        {
            string id = UserRowPrefix + type.Key;
            InspectorItemViewModel row = Item(new InspectorItemViewModel(InspectorItemKind.Row, InspectorGroup.User, id));
            row.Name = type.DisplayName;
            row.ByteOffset = 0;
            row.Endian = Endian;
            if (!_userValues.TryGetValue(type.Key, out InspectorUserValue? value))
            {
                StartUserEvaluation(type, doc);
                row.Status = InspectorStatus.Loading;
                if (!row.IsEditing)
                {
                    row.Value = InspectorStrings.Current.Loading;
                }

                row.ToolTip = PluginTip(type);
                row.CanExpand = false;
                row.ByteCount = 0;
                wanted.Add(row);
                continue;
            }

            row.ByteCount = (int)Math.Clamp(value.Size, 0, InspectorDecoder.MaxReadLength);
            (InspectorStatus status, string text, string? tip) = value switch
            {
                { TimedOut: true } => (InspectorStatus.Invalid, Loc.Get("Inspector_UserTimedOut"), Loc.Format("Inspector_UserTimedOutTip", (int)UserTypes!.TimeLimit.TotalMilliseconds)),
                { Stopped: true } => (InspectorStatus.Invalid, Loc.Get("Inspector_PluginStopped"), PluginTip(type)),
                { NotEnoughData: true } => (InspectorStatus.NotEnoughData, "—", InspectorStrings.Current.NotEnoughTip),
                { Error: { } error } => (InspectorStatus.Invalid, Loc.Format("Inspector_UserError", error), error),
                _ => (InspectorStatus.Ok, value.Display, PluginTip(type) ?? (ReadOnly ? Loc.Get("Inspector_ReadOnlyTip") : null)),
            };
            row.Status = status;
            if (!row.IsEditing)
            {
                row.Value = text;
            }

            row.ToolTip = tip;
            row.FullText = status == InspectorStatus.Ok ? value.Display : null;
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

    /// <summary>プラグインの型のツールチップ (プラグイン名。AUTO-31 の仕様 4)。</summary>
    private static string? PluginTip(InspectorUserType type) =>
        type.Source == InspectorUserTypeSource.Plugin ? Loc.Format("Plugin_ToolTip", type.File) : null;

    /// <summary>型の解釈を別のスレッドで始める。終わったら、起点が同じなら行を更新する。</summary>
    private void StartUserEvaluation(InspectorUserType type, DocumentViewModel doc)
    {
        if (UserTypes is not { } userTypes || !_userPending.Add(type.Key))
        {
            return;
        }

        var key = _userKey;
        byte[] data = UserTypeData(doc);
        InspectorOptions options = Options;
        Microsoft.UI.Dispatching.DispatcherQueue queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            InspectorUserValue value;
            try
            {
                value = await userTypes.EvaluateAsync(type, data, key.Endian, options).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 1 つの型の失敗は、その行だけのエラーにする (仕様 4)。
                value = InspectorUserValue.Failed(ex.Message);
            }

            queue.TryEnqueue(() =>
            {
                _userPending.Remove(type.Key);
                if (key == _userKey)
                {
                    _userValues[type.Key] = value;
                }

                // 起点が変わっていたら、待っている間に始められなかった今の起点の解釈を始める (行を「…」のままにしない)。
                Refresh();

                UserTypesEvaluated?.Invoke(this, EventArgs.Empty);
            });
        });
    }

    /// <summary>起点から最大 4 KB (INSP-01 の仕様 4)。読み込み中のバイトは 0 のまま (読み込みが終われば解釈し直す)。</summary>
    private byte[] UserTypeData(DocumentViewModel doc)
    {
        DocumentSnapshot snapshot = doc.Document.Current;
        int count = (int)Math.Clamp(snapshot.Length - Origin, 0, InspectorDecoder.MaxReadLength);
        byte[] data = new byte[count];
        var states = new ByteState[count];
        if (count > 0)
        {
            count = snapshot.ReadForDisplay(Origin, data, states);
        }

        return data[..count];
    }

    private static bool IsUserRow(InspectorItemViewModel item) => item.TypeId.StartsWith(UserRowPrefix, StringComparison.Ordinal);

    private InspectorUserType? UserTypeOf(InspectorItemViewModel item) =>
        UserTypes?.Types.FirstOrDefault(t => UserRowPrefix + t.Key == item.TypeId);

    // ---- 書き換え (INSP-22 の仕様 5、TPL-29 の仕様 3) ----

    /// <summary>ユーザー定義型の行の入力を始める。書き換えられない型 (変換のない型・TPL-22 の規則で書き換えられない値) は始めない。</summary>
    private bool BeginUserEdit(InspectorItemViewModel item)
    {
        if (_document is not { } doc || UserTypeOf(item) is not { } type || UserTypes is not { } userTypes || item.Status != InspectorStatus.Ok)
        {
            return false;
        }

        CancelEdit();
        if (type.Source != InspectorUserTypeSource.Plugin)
        {
            TemplateResult? result = userTypes.ApplyForEdit(type, UserTypeData(doc), Endian);
            TemplateNode? node = result?.Invoke(() => result.Root.Children.Count > 0 ? result.Root.Children[0] : null);
            if (result is null || node is null || !TemplateInspectorTypes.CanEdit(node))
            {
                result?.Dispose();
                return false;
            }

            _userEdit = (item.TypeId, result, node);
        }

        item.EditText = item.FullText ?? item.Value;
        item.IsEditing = true;
        UpdateUserEdit(item, item.EditText);
        return true;
    }

    /// <summary>書き込む内容 (オフセットは起点から)。書き換えられなければ誤りの文。</summary>
    private (long Offset, byte[]? Bytes, string? Error) PlanUserEdit(InspectorItemViewModel item, string text)
    {
        if (UserTypeOf(item) is not { } type)
        {
            return (0, null, Loc.Get("Inspector_PluginNotWritable"));
        }

        long available = (_document?.Document.Length ?? 0) - Origin;
        if (type.Source == InspectorUserTypeSource.Plugin)
        {
            byte[]? bytes = InspectorUserTypes.EncodePlugin(type, text, item.Endian);
            return bytes is null ? (0, null, Loc.Get("Inspector_PluginNotWritable"))
                : bytes.Length > available ? (0, null, Loc.Get("Inspector_NotEnough"))
                : (0, bytes, null);
        }

        if (_userEdit is not { } edit || edit.TypeId != item.TypeId)
        {
            return (0, null, Loc.Get("Tpl_EditInvalid"));
        }

        TemplateEditPlan plan = TemplateInspectorTypes.PlanEdit(edit.Node, text);
        return plan.Success && plan.Bytes is { } b ? (plan.Offset, b, null) : (0, null, Loc.Get("Tpl_EditInvalid"));
    }

    private void UpdateUserEdit(InspectorItemViewModel item, string text)
    {
        item.EditText = text;
        (_, byte[]? bytes, string? error) = PlanUserEdit(item, text);
        _editBytes = bytes;
        item.Preview = bytes is { } b ? string.Join(' ', b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture))) : string.Empty;
        item.Error = error ?? string.Empty;
        HighlightChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CommitUserEdit(InspectorItemViewModel item)
    {
        if (_document is not { } doc)
        {
            return false;
        }

        (long offset, byte[]? bytes, string? error) = PlanUserEdit(item, item.EditText);
        if (bytes is null || ReadOnly)
        {
            item.Error = ReadOnly ? Loc.Get("Inspector_ReadOnlyTip") : error ?? Loc.Get("Tpl_EditInvalid");
            return false;
        }

        item.IsEditing = false;
        _editBytes = null;
        EndUserEdit();

        // 上書きで書き、1 回の Undo で戻せる (INSP-17)。
        doc.Document.Overwrite(Origin + offset, ForWrite(doc, Origin + offset, bytes), Loc.Get("Inspector_UndoDescription"));
        Refresh();
        return true;
    }

    private void EndUserEdit()
    {
        if (_userEdit is { } edit)
        {
            _userEdit = null;
            edit.Result.Dispose();
        }
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
