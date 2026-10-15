using System.Text;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.App.ViewModels;
using HexEditor.Core.Templates;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// テンプレートの値の検索 (04 の FIND-29): テンプレート結果パネルの検索欄に <c>name:</c>・<c>type:</c>・<c>value</c> の条件を書いて Enter で
/// 検索し、結果を共通の結果一覧 (下パネル「検索結果」) に出す。結果を選ぶとツリーのノードとエディタの範囲を選ぶ。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>直前のテンプレートの値の検索の件数 (テスト用の命令)。</summary>
    internal long? LastTemplateValueSearchCount { get; private set; }

    /// <summary>結果の更新を見ているドキュメントの適用の一覧。</summary>
    private readonly HashSet<DocumentTemplates> _valueSearchWatched = [];

    /// <summary>検索する。条件の構文エラー・テンプレートがないときは誤りの文を返す (検索欄の下に出す)。</summary>
    public async Task<string?> FindTemplateValuesAsync(string text, bool evaluateAll)
    {
        if (Vm.Selected is not { } doc || DocumentTemplates.Find(doc.Document) is not { } templates || !templates.Entries.Any(e => e.Result is not null))
        {
            return Loc.Get("Tpl_ValueSearchNoTemplate");
        }

        TemplateValueQuery query;
        try
        {
            query = TemplateValueQuery.Parse(text);
        }
        catch (TemplateQueryException ex)
        {
            // 構文エラーは位置を示す (FIND-29 の「エラー」)
            return Loc.Format("Tpl_ValueSearchSyntax", ex.Position + 1, Loc.Get("Tpl_ValueSearchError_" + ex.Kind));
        }

        if (_valueSearchWatched.Add(templates))
        {
            templates.ResultChanged += (_, _) =>
            {
                if (!_closingConfirmed)
                {
                    TemplateValueSearchResultChanged();
                }
            };
        }

        TemplatePanelViewModel panel = TemplateVm;
        var results = TemplateValueSearch.CreateResults(doc.Editor.Document.Current, () => CurrentResults(templates), query, evaluateAll,
            match => DispatcherQueue.TryEnqueue(async () => await panel.SelectNodeAsync(match.Node)));
        var cts = new CancellationTokenSource();
        await SearchResults.RunAsync(doc.Editor, results, Loc.Get("Tpl_ValueSearchKind"), text, Encoding.ASCII, cts);
        LastTemplateValueSearchCount = results.LongCount;
        return null;
    }

    /// <summary>今の評価の結果 (どのスレッドからも呼ぶ。一覧が変わる途中なら空として次の再検索に任せる)。</summary>
    private static IReadOnlyList<TemplateResult> CurrentResults(DocumentTemplates templates)
    {
        try
        {
            return [.. templates.Entries.ToArray().Where(e => !e.IsFollow).Select(e => e.Result).OfType<TemplateResult>()];
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>コマンドパレット「テンプレートの値を検索」: テンプレート結果パネルを出して検索欄にフォーカスを移す。</summary>
    private void FindTemplateValuesCommand()
    {
        ShowPanel(TemplatesPanelId);
        DispatcherQueue.TryEnqueue(() => TemplatePanelView?.FocusSearchBox());
    }

    /// <summary>テンプレートの結果が更新された: 結果一覧の「古い結果」の表示を更新する (FIND-29 の仕様 5)。</summary>
    private void TemplateValueSearchResultChanged() => SearchResults.RefreshState();
}
