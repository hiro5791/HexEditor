using HexEditor.Core.Engine;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Templates;

/// <summary>
/// 構造体テンプレートの入口 (TPL-20)。テンプレートを読み込み (<see cref="TemplateLoader"/>)、データに適用して結果ツリーを作る。
/// 評価はバックグラウンドの専用のスレッドで行い、呼び出しはすぐに戻る。
/// </summary>
public static class TemplateEngine
{
    /// <summary>適用を始める。結果は評価中から読める (<see cref="TemplateResult.Completion"/> で完了を待てる)。</summary>
    public static TemplateResult Apply(TemplateProgram program, TemplateDataSource data, TemplateApplyOptions? options = null)
    {
        var result = new TemplateResult(program, data, options ?? new TemplateApplyOptions());
        result.Evaluator.Start();
        return result;
    }

    /// <summary>ドキュメントのスナップショットに適用する。</summary>
    public static TemplateResult Apply(TemplateProgram program, DocumentSnapshot snapshot, string? fileName = null, TemplateApplyOptions? options = null) =>
        Apply(program, TemplateDataSource.FromSnapshot(snapshot, fileName), options);

    /// <summary>適用して評価が終わるまで待つ (スクリプト・コマンドラインから使う。AUTO-13、AUTO-42)。</summary>
    public static TemplateResult ApplyAndWait(TemplateProgram program, TemplateDataSource data, TemplateApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TemplateResult result = Apply(program, data, options);
        using (cancellationToken.Register(result.Cancel))
        {
            result.Completion.Wait(CancellationToken.None);
        }

        return result;
    }
}
