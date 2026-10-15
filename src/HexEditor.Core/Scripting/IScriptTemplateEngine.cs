using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting;

/// <summary>
/// テンプレートエンジン (07) とスクリプトの API (AUTO-13) の間の取り決め。テンプレートの領域がこれを実装し、App が
/// <see cref="IScriptEnvironment.Templates"/> で渡す。呼び出しはすべてスクリプトのスレッドから行い、ドキュメントの変更は
/// <see cref="ITemplateResultNode.SetValue"/> の中で UI スレッドに渡す (実装側の責任)。
/// </summary>
public interface IScriptTemplateEngine
{
    /// <summary>使えるテンプレートの一覧 (AUTO-13 の仕様 1)。</summary>
    IReadOnlyList<TemplateInfo> List();

    /// <summary>自動適用 (07) と同じ判定で合うテンプレートの候補 (仕様 5)。</summary>
    IReadOnlyList<TemplateInfo> Detect(Document document, string? path);

    /// <summary>
    /// テンプレートを適用する (仕様 2)。<paramref name="show"/> が true ならテンプレートパネルに結果を表示する。07 の上限 (要素数・時間) に従う。
    /// テンプレートのエラーは <see cref="TemplateScriptException"/> (行・列を含む)。
    /// </summary>
    Task<ITemplateResultNode> ApplyAsync(IScriptDocumentHost document, string nameOrPath, long offset, bool show, CancellationToken cancellationToken);
}

/// <summary>解析結果のノード (AUTO-13 の仕様 3)。</summary>
public interface ITemplateResultNode
{
    string Name { get; }

    string Type { get; }

    long Start { get; }

    long Length { get; }

    /// <summary>値 (数値・文字列・バイト列など)。</summary>
    object? Value { get; }

    string DisplayValue { get; }

    IReadOnlyList<ITemplateResultNode> Children { get; }

    /// <summary>テンプレートのツリーから編集した場合と同じくドキュメントを書き換える (仕様 4。Undo できる)。</summary>
    void SetValue(object? value);

    /// <summary>コマンドラインの <c>template</c> (AUTO-42) と同じ形の JSON (仕様 3 の <c>toJSON()</c>)。</summary>
    JsonNode ToJson();
}

/// <summary>テンプレートのエラー (07 と同じ内容。行・列を含む)。</summary>
public sealed class TemplateScriptException(string message, string? file, int line, int column) : Exception(message)
{
    public string? File { get; } = file;

    public int Line { get; } = line;

    public int Column { get; } = column;
}
