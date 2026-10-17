using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Templates.Presentation;

/// <summary>
/// テンプレートの型をデータインスペクタで使う (TPL-29) ときの、テンプレートの側の決まり。型の一覧の読み込みと評価は、ユーザー定義型と
/// 同じ経路 (<see cref="Inspector.InspectorUserTypes"/>。登録したテンプレートは <see cref="Inspector.InspectorUserTypeSource.Registered"/>) で行う。
/// </summary>
public static class TemplateInspectorTypes
{
    /// <summary>インスペクタ用の型の推奨の上限 (バイト)。</summary>
    public const int RecommendedMaxBytes = 4096;

    /// <summary>4 KB 以下の固定長か (違うデータで 2 回評価して大きさを比べる。可変長は登録時に警告する。TPL-29 の仕様 4)。</summary>
    public static bool IsSmallFixed(TemplateProgram program, string type)
    {
        long? size = null;
        foreach (byte fill in new byte[] { 0x00, 0xFF })
        {
            byte[] data = new byte[RecommendedMaxBytes * 2];
            Array.Fill(data, fill);
            var options = new TemplateApplyOptions { TypeName = type, LimitOverride = TemplateApplication.FollowLimits };
            using TemplateResult r = TemplateEngine.ApplyAndWait(program, TemplateDataSource.FromBytes(data), options);
            long s = r.Root.Size;
            if (size is { } previous && previous != s)
            {
                return false;
            }

            size = s;
        }

        return size is > 0 and <= RecommendedMaxBytes;
    }

    /// <summary>インスペクタからの書き換え (TPL-22 と同じ規則。TPL-29 の仕様 3)。書き込む内容を返す。</summary>
    public static TemplateEditPlan PlanEdit(TemplateNode node, string input)
    {
        TemplateValueEditSession? session = node.Result.Invoke(() => TemplateValueEditSession.Create(node, documentReadOnly: false, out _));
        return session is null ? new TemplateEditPlan(0, null, TemplateEditError.Blocked, null) : session.Plan(input);
    }

    /// <summary>書き換えられる値か (TPL-22 の規則で書き換えを始められる)。</summary>
    public static bool CanEdit(TemplateNode node) =>
        node.Result.Invoke(() => TemplateValueEditSession.Create(node, documentReadOnly: false, out _)) is not null;
}
