using HexEditor.Core.Engine;

namespace HexEditor.Cli;

/// <summary>
/// コマンドラインが外部に任せる機能。テンプレートエンジン (07) とスクリプトのホスト (AUTO-01〜AUTO-35) は別の領域が作るため、
/// ここでは小さなインターフェースだけを定める。実際の実装は <see cref="CliServices"/> に差し込む
/// (差し込み先は <c>Program.cs</c> の <c>CliServices.Default</c>)。差し込まれていない間、該当のサブコマンドは終了コード 2
/// (「この版では使えません」) で終わる。
/// </summary>
public sealed class CliServices
{
    /// <summary>テンプレートの適用 (AUTO-42)。</summary>
    public ITemplateService? Templates { get; init; }

    /// <summary>スクリプトの実行 (AUTO-41)。</summary>
    public IScriptRunner? Scripts { get; init; }

    /// <summary>GUI (HexEditor.exe) の起動 (AUTO-37)。</summary>
    public IGuiLauncher Gui { get; init; } = new GuiLauncher();

    /// <summary>製品の既定: Core のテンプレートエンジン (07) とスクリプトのホスト (08)。</summary>
    public static CliServices Default { get; } = new()
    {
        Templates = new Services.CoreTemplateService(),
        Scripts = new Services.CoreScriptRunner(),
    };
}

/// <summary>テンプレートを適用して結果を出力する (AUTO-42)。</summary>
public interface ITemplateService
{
    /// <summary>
    /// テンプレートを適用し、<see cref="TemplateRequest.Format"/> の形式で <see cref="TemplateRequest.Output"/> に書く。
    /// 合うテンプレートがない (<see cref="TemplateRequest.Auto"/>) ときは終了コード 1、テンプレートのエラーは 4 を返す
    /// (<see cref="CliException"/> を投げてもよい)。
    /// </summary>
    int Apply(TemplateRequest request, CancellationToken cancellationToken);
}

/// <summary>テンプレートの適用の依頼。</summary>
public sealed record TemplateRequest(
    Document Document,
    string FilePath,
    string? Template,
    bool Auto,
    long Offset,
    string Format,
    int? Depth,
    IReadOnlyList<string> Fields,
    TextWriter Output,
    TextWriter Error,
    string Language = "en");

/// <summary>スクリプトをヘッドレスで実行する (AUTO-41)。</summary>
public interface IScriptRunner
{
    /// <summary>
    /// スクリプトを実行して終了コードを返す。信頼の確認 (<see cref="ScriptRunRequest.Trust"/>)・許可 (<see cref="ScriptRunRequest.Allow"/>)・
    /// 実行時間の上限 (<see cref="ScriptRunRequest.Timeout"/>) は実行側が扱う。構文エラー・実行時の例外は 4、信頼されていないスクリプトは 5、
    /// 上限を超えたら 7、<c>app.exit(コード)</c> はそのコードを返す。
    /// </summary>
    int Run(ScriptRunRequest request, CancellationToken cancellationToken);
}

/// <summary>スクリプトの実行の依頼。</summary>
public sealed record ScriptRunRequest(
    string ScriptPath,
    IReadOnlyList<Document> Documents,
    IReadOnlyList<KeyValuePair<string, string>> Arguments,
    bool Trust,
    IReadOnlyList<string> Allow,
    TimeSpan? Timeout,
    bool ReadOnly,
    Func<Document, bool> SaveDocument,
    TextWriter Output,
    TextWriter Error,
    Func<string, string?> Prompt,
    IReadOnlyList<string>? DocumentPaths = null,
    bool DryRun = false,
    string Language = "en");

/// <summary>GUI の起動 (AUTO-37)。</summary>
public interface IGuiLauncher
{
    /// <summary>GUI の実行ファイル (HexEditor.exe)。見つからなければ null。</summary>
    string? FindExecutable();

    /// <summary>GUI を起動する (待たない)。起動したプロセスの ID を返す。</summary>
    int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);

    /// <summary>GUI の実行ファイルをウィンドウを出さない用途 (<c>--unregister</c>) で起動し、終わるまで待って終了コードを返す。</summary>
    int RunAndWait(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken);
}
