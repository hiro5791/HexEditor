using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Core.Scripting;
using HexEditor.Core.Templates.Extensions;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates;

/// <summary>
/// スクリプトの <c>templates</c> と <c>doc.applyTemplate</c> (08 の AUTO-13) をテンプレートエンジン (07) につなぐ。
/// テンプレートは <see cref="TemplateCatalog"/> から名前かパスで探し、評価はテンプレートの専用のスレッドで行う。
/// </summary>
public sealed class TemplateScriptEngine(TemplateCatalog catalog) : IScriptTemplateEngine
{
    /// <summary>JSON の形式の識別子 (コマンドラインの <c>template</c> の出力の <c>schema</c>。AUTO-42)。</summary>
    public const string JsonSchema = "hexeditor.template/1";

    public TemplateCatalog Catalog { get; } = catalog;

    /// <summary>
    /// テンプレート拡張 (TPL-19)。スクリプト・プラグインが登録した関数をテンプレートから呼べるようにする。登録できるのは信頼の確認
    /// (AUTO-34) を通って実行中のスクリプトだけなので、登録された関数の呼び出しは許可する (AUTO-13 の仕様 6)。
    /// </summary>
    public TemplateExtensionRegistry Extensions { get; } = new() { Permission = _ => true };

    /// <summary>
    /// <c>show: true</c> の結果をテンプレートパネルに出す (App が設定する。どのスレッドから呼ばれてもよいように実装する)。
    /// null なら表示しない (ヘッドレス実行)。
    /// </summary>
    public Action<IScriptDocumentHost, TemplateCatalogEntry, TemplateResult>? Show { get; set; }

    public IReadOnlyList<Sdk.TemplateInfo> List() =>
        [.. Catalog.Entries.Select(e => new Sdk.TemplateInfo(e.Name, e.Path, [.. e.Metadata.Extensions]))];

    public IReadOnlyList<Sdk.TemplateInfo> Detect(Document document, string? path) =>
        [.. Catalog.Detect(document.Current, path).Select(c => new Sdk.TemplateInfo(c.Entry.Name, c.Entry.Path, [.. c.Entry.Metadata.Extensions]))];

    public async Task<ITemplateResultNode> ApplyAsync(IScriptDocumentHost document, string nameOrPath, long offset, bool show, CancellationToken cancellationToken)
    {
        TemplateCatalogEntry entry = Catalog.Find(nameOrPath)
            ?? throw new Sdk.ArgumentError(Msg.T($"テンプレート「{nameOrPath}」が見つかりません。", $"The template \"{nameOrPath}\" was not found."));
        TemplateResult result = await Task.Run(() => Apply(entry, document.Document.Current, document.Path ?? document.Name, offset, Extensions, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (show)
        {
            Show?.Invoke(document, entry, result);
        }

        return new Node(document, result.Root, entry.Name);
    }

    /// <summary>
    /// テンプレートを適用して評価が終わるまで待つ (スクリプトとコマンドラインの共通)。読み込みのエラー (構文エラーなど) は
    /// <see cref="TemplateScriptException"/> (最初のエラーの位置)。評価中のエラーは部分結果 (TPL-27) として結果に残す。
    /// </summary>
    public static TemplateResult Apply(TemplateCatalog catalog, TemplateCatalogEntry entry, DocumentSnapshot snapshot, string? fileName, long offset,
        ITemplateExtensionHost? extensions, CancellationToken cancellationToken)
    {
        TemplateProgram program = catalog.Load(entry);
        ThrowIfErrors(program.Diagnostics);
        var options = new TemplateApplyOptions { StartOffset = offset, Extensions = extensions };
        TemplateResult result = TemplateEngine.ApplyAndWait(program, TemplateDataSource.FromSnapshot(snapshot, fileName), options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.State == TemplateRunState.SyntaxError)
        {
            ThrowIfErrors(result.Diagnostics);
        }

        return result;
    }

    private TemplateResult Apply(TemplateCatalogEntry entry, DocumentSnapshot snapshot, string? fileName, long offset, ITemplateExtensionHost? extensions,
        CancellationToken cancellationToken) => Apply(Catalog, entry, snapshot, fileName, offset, extensions, cancellationToken);

    /// <summary>JSON の最上位に加えるプロパティ (<c>toJSON()</c> とコマンドラインで共通。コマンドラインはさらに <c>schema</c> を前に加える)。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> RootProperties(string templateName) => [new("template", templateName)];

    private static void ThrowIfErrors(IReadOnlyList<TemplateDiagnostic> diagnostics)
    {
        if (diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Error) is { } error)
        {
            TemplateSourceLocation? at = error.Location;
            throw new TemplateScriptException(error.Code + ": " + error.Message, at?.File, at?.Line ?? 0, at?.Column ?? 0);
        }
    }

    /// <summary>結果のノード (AUTO-13 の仕様 3・4)。子は読まれたときに作る (大きな配列でも全体を作らない)。</summary>
    private sealed class Node(IScriptDocumentHost document, TemplateNode node, string? templateName) : ITemplateResultNode
    {
        public string Name => node.DisplayName;

        public string Type => node.Kind == TemplateNodeKind.Application ? "application" : node.TypeName;

        public long Start => node.Offset;

        public long Length => node.Size;

        public object? Value => node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Section or TemplateNodeKind.Application
            ? null
            : node.Value switch
            {
                Int128 i => i.ToString(CultureInfo.InvariantCulture),
                UInt128 u => u.ToString(CultureInfo.InvariantCulture),
                var v => v,
            };

        public string DisplayValue => node.GetDisplay(TemplateDisplayContext.Current);

        public IReadOnlyList<ITemplateResultNode> Children => new ChildList(document, node);

        /// <summary>
        /// ツリーから編集した場合と同じく書き込む (TPL-22。<c>write</c> の変換を通す)。呼び出し側 (スクリプトの API) が UI スレッドで
        /// 書き込みの単位 (Undo の 1 回分) の中で呼ぶ。
        /// </summary>
        public void SetValue(object? value)
        {
            string text = value switch
            {
                null => throw new Sdk.ArgumentError(Msg.T("値を指定してください。", "Specify a value.")),
                double d when Math.Abs(d) < 9.007199254740992E15 && d == Math.Floor(d) => ((long)d).ToString(CultureInfo.InvariantCulture),
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                bool b => b ? "1" : "0",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
            TemplateEditResult edit = node.EncodeValue(text);
            if (!edit.Success || edit.Bytes is null)
            {
                throw new Sdk.ArgumentError(Msg.T($"値を書き込めません ({node.Path}): {edit.Error}", $"Cannot write the value ({node.Path}): {edit.Error}"));
            }

            Document doc = document.Document;
            if (edit.Offset < 0 || edit.Offset + edit.Bytes.Length > doc.Length)
            {
                throw new Sdk.RangeError(Msg.T("範囲がドキュメントの外です。", "The range is outside the document."));
            }

            doc.Overwrite(edit.Offset, edit.Bytes, "テンプレートの値の編集");
        }

        public JsonNode ToJson()
        {
            var options = new TemplateExportOptions
            {
                Indented = false,
                RootProperties = node.Kind == TemplateNodeKind.Application && templateName is not null ? RootProperties(templateName) : [],
            };
            return JsonNode.Parse(TemplateJsonExporter.ToJson(node, options))!;
        }
    }

    private sealed class ChildList(IScriptDocumentHost document, TemplateNode node) : IReadOnlyList<ITemplateResultNode>
    {
        private readonly IReadOnlyList<TemplateNode> _children = node.Children;

        public ITemplateResultNode this[int index] => new Node(document, _children[index], null);

        public int Count => _children.Count;

        public IEnumerator<ITemplateResultNode> GetEnumerator()
        {
            for (int i = 0; i < _children.Count; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
