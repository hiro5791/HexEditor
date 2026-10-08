using System.Text.RegularExpressions;

namespace HexEditor.Core.Commands;

/// <summary>引数を尋ねるコマンドの引数の定義 (UI-16 の仕様 1、UI-17 の仕様 7)。</summary>
/// <param name="PromptKey">入力欄の案内のリソースのキー。</param>
/// <param name="Kind">入力の種類 (<c>expression</c>: 00-overview.md 6 章の入力式、<c>text</c>: 文字列)。</param>
public sealed record CommandArgument(string PromptKey, string Kind = "text");

/// <summary>
/// コマンドの定義 (UI-16 の仕様 1)。メニュー・ツールバー・コマンドパレット・ショートカット・マクロ・スクリプトから同じものを呼ぶ。
/// 処理 (実行と有効条件) はウィンドウごとに App が登録する (<c>CommandHost</c>)。
/// </summary>
/// <remarks>
/// 表示名は表示言語のリソース <see cref="NameKey"/> (英語名はその <c>en</c> の値)、検索用の別名は <see cref="AliasKey"/>
/// (半角スペース区切り。<c>ja</c> では表示名の読みをひらがなで置く)、カテゴリ名は <see cref="CategoryKey"/>。
/// プラグイン・スクリプトが実行時に登録するコマンドは、リソースの代わりに <see cref="Text"/> に表示名を持つ。
/// </remarks>
public sealed partial record CommandDefinition
{
    public CommandDefinition(string id, string category)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"コマンド ID の形式が正しくありません: {id}", nameof(id));
        }

        Id = id;
        Category = category;
    }

    /// <summary>不変の識別子 (例: <c>file.open</c>)。一度公開したら変えない (仕様 5)。</summary>
    public string Id { get; }

    /// <summary>カテゴリ (コマンドパレットの接頭辞。メニューの名前に合わせる: file、edit、search、go、view …)。</summary>
    public string Category { get; }

    /// <summary>Segoe Fluent Icons の文字 (任意)。ツールバーに使う。</summary>
    public string? Icon { get; init; }

    /// <summary>既定のショートカット (00-overview.md 8 章の表から)。</summary>
    public IReadOnlyList<KeyBinding> DefaultBindings { get; init; } = [];

    /// <summary>
    /// 既定のショートカットを、その有効範囲の部品自身が処理する有効範囲 (例: Hex ビューの Ctrl+C)。この有効範囲に
    /// フォーカスがあり、押されたキーが既定の割り当てのままなら、キーの振り分けは部品に任せる。
    /// </summary>
    public IReadOnlyList<KeyScope> NativeScopes { get; init; } = [];

    /// <summary>有効条件の式 (説明用。例: <c>documentOpen &amp;&amp; !readOnly</c>)。判定は App の処理が行う。</summary>
    public string? Condition { get; init; }

    /// <summary>引数 (任意)。</summary>
    public CommandArgument? Argument { get; init; }

    /// <summary>廃止した以前の ID (ショートカット設定の互換のため別名として残す。仕様 5)。</summary>
    public IReadOnlyList<string> FormerIds { get; init; } = [];

    /// <summary>実行時に登録されたコマンド (プラグイン・スクリプト) の表示名。リソースを持つ組み込みのコマンドでは null。</summary>
    public string? Text { get; init; }

    /// <summary>メニュー・パレットに出さない内部用のコマンドなら true。</summary>
    public bool Hidden { get; init; }

    public string NameKey => "Cmd_" + KeyPart(Id);

    public string AliasKey => "CmdAlias_" + KeyPart(Id);

    public string CategoryKey => "CmdCategory_" + Category;

    /// <summary>ID からリソースのキーの部分を作る (<c>.</c> を <c>_</c> に)。</summary>
    public static string KeyPart(string id) => id.Replace('.', '_');

    /// <summary>
    /// ID の形式: 英字と数字とドット。ドットで区切った各部分は英小文字で始める (例: <c>view.zoomIn</c>)。
    /// プラグインとスクリプトは <c>plugin.&lt;プラグイン ID&gt;.&lt;名前&gt;</c>、<c>script.&lt;名前&gt;</c> (仕様 3)。
    /// </summary>
    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    [GeneratedRegex(@"^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)+$")]
    private static partial Regex IdPattern();
}
