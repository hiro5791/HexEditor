using HexEditor.Core.Scripting;
using HexEditor.Sdk;

namespace HexEditor.App.Services.Scripting;

/// <summary>スクリプトの UI (08 の AUTO-16、AUTO-34、AUTO-02 の仕様 5、AUTO-03 の仕様 8)。アクティブなウィンドウに出す。</summary>
internal sealed class AppScriptUi(AppScriptEnvironment env) : IScriptUiService
{
    private static MainWindow Window => WindowManager.Current;

    public Task MessageAsync(string scriptName, string text, string kind, bool modal) => Window.ScriptMessageAsync(scriptName, text, kind, modal);

    public Task<int> ConfirmAsync(string scriptName, string text, IReadOnlyList<string> buttons) => Window.ScriptConfirmAsync(scriptName, text, buttons);

    public Task<IReadOnlyDictionary<string, string>?> FormAsync(string scriptName, string? title, IReadOnlyList<ScriptFormField> fields,
        Func<IReadOnlyDictionary<string, string>, (string Field, string Message)?> validate) => Window.ScriptFormAsync(scriptName, title, fields, validate);

    public Task<IReadOnlyList<string>?> PickAsync(string scriptName, string kind, PickOptions options) => Window.ScriptPickAsync(kind, options);

    public void Status(string text) => Window.ShowStatusMessage(text, TimeSpan.FromSeconds(5));

    public void ShowResults(string scriptName, string title, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<string> columns,
        IScriptDocumentHost? document) => Window.ShowScriptResults(scriptName, title, rows, columns, (document as AppDocumentHost)?.ViewModel);

    public Task<TrustAnswer> ConfirmTrustAsync(TrustPrompt prompt) => Window.ConfirmScriptTrustAsync(prompt);

    public Task<bool> ConfirmPermissionAsync(string scriptName, string permission, string detail) =>
        Window.ConfirmScriptPermissionAsync(scriptName, permission, detail);

    public Task<bool> ConfirmContinueAfterTimeoutAsync(ScriptRun run, TimeSpan limit) => Window.ConfirmScriptTimeoutAsync(run, limit);

    public Task<bool> OfferCSharpComponentAsync() => Window.OfferCSharpComponentAsync();

    internal AppScriptEnvironment Environment => env;
}
