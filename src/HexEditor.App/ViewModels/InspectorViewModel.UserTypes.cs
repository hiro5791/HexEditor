using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;

namespace HexEditor.App.ViewModels;

/// <summary>
/// ユーザー定義型 (INSP-22) の行: 設定フォルダの <c>inspector/</c> のテンプレートの <c>[[inspector("表示名")]]</c> の型を「ユーザー定義」の
/// グループに出す。解釈は別のスレッドで行い、終わるまで値を「…」にする (UI を止めない)。時間切れ・エラーは起点 (またはデータ) が変わるまで
/// 再実行しない (仕様 3)。構造体はフィールドを展開できる行にする。
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

        InspectorItemViewModel header = wanted.FirstOrDefault(i => i.IsGroup && i.Group == InspectorGroup.User)
            ?? Item(new InspectorItemViewModel(InspectorItemKind.Group, InspectorGroup.User, UserRowPrefix + "group"));
        if (!wanted.Contains(header))
        {
            header.Name = Loc.Get("Inspector_Group_" + InspectorGroup.User);
            header.IsCollapsed = _collapsed.Contains(InspectorGroup.User);
            wanted.Add(header);
        }

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
                row.Value = InspectorStrings.Current.Loading;
                row.ToolTip = null;
                row.CanExpand = false;
                row.ByteCount = 0;
                wanted.Add(row);
                continue;
            }

            row.ByteCount = (int)Math.Clamp(value.Size, 0, InspectorDecoder.MaxReadLength);
            (row.Status, row.Value, row.ToolTip) = value switch
            {
                { TimedOut: true } => (InspectorStatus.Invalid, Loc.Get("Inspector_UserTimedOut"), Loc.Format("Inspector_UserTimedOutTip", (int)UserTypes!.TimeLimit.TotalMilliseconds)),
                { Error: { } error } => (InspectorStatus.Invalid, Loc.Format("Inspector_UserError", error), error),
                _ => (InspectorStatus.Ok, value.Display, (string?)null),
            };
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
                    Refresh();
                }

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
}
