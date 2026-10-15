using System.Text.Json.Nodes;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Api;

/// <summary><c>templates</c> (AUTO-13)。テンプレートエンジン (07) は <see cref="IScriptEnvironment.Templates"/> から使う。</summary>
internal sealed class ScriptTemplates(ScriptContext ctx) : ITemplates
{
    private IScriptTemplateEngine Engine => ctx.Environment.Templates ?? throw new Sdk.ArgumentError(Msg.T("テンプレートの機能が使えません。", "The template feature is not available."));

    public IReadOnlyList<TemplateInfo> List() => ScriptErrorMap.Guard(() => Engine.List());

    public IReadOnlyList<TemplateInfo> Detect(IDocument document) => ScriptErrorMap.Guard(() =>
    {
        if (document is not ScriptDocument doc)
        {
            throw new Sdk.ArgumentError(Msg.T("document にはドキュメントを指定してください。", "Specify a document for document."));
        }

        doc.ThrowIfClosed();
        return Engine.Detect(doc.Document, doc.Path);
    });
}

/// <summary>解析結果のノード (AUTO-13 の仕様 3・4)。</summary>
internal sealed class ScriptTemplateNode(ScriptDocument doc, ITemplateResultNode node) : ITemplateNode
{
    private IReadOnlyList<ITemplateNode>? _children;

    public string Name => node.Name;

    public string Type => node.Type;

    public long Start => node.Start;

    public long Length => node.Length;

    public object? Value
    {
        get => node.Value;
        set => ScriptErrorMap.Guard(() =>
        {
            // テンプレートのツリーから編集した場合と同じく書き換える (Undo できる)。書き込みの排他と Undo の単位はスクリプトと同じ。
            WriteSession session = doc.Context.AcquireWrite(doc);
            doc.Context.Dispatcher.Invoke(() => session.Apply(_ =>
            {
                node.SetValue(value);
                return true;
            }));
        });
    }

    public string DisplayValue => node.DisplayValue;

    public IReadOnlyList<ITemplateNode> Children => _children ??= [.. node.Children.Select(c => (ITemplateNode)new ScriptTemplateNode(doc, c))];

    public ITemplateNode? Get(string path)
    {
        ITemplateNode? current = this;
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current?.Children.FirstOrDefault(c => c.Name == part);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public JsonNode ToJson() => node.ToJson();
}
