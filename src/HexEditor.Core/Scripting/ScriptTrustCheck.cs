namespace HexEditor.Core.Scripting;

/// <summary>信頼の確認の結果。<see cref="Permissions"/> は実行前に与える許可。</summary>
internal sealed record TrustDecision(bool Allowed, IReadOnlyCollection<string> Permissions, string Reason)
{
    public static TrustDecision Allow(IEnumerable<string> permissions) => new(true, [.. permissions], "");

    public static TrustDecision Deny(string reason) => new(false, [], reason);
}

/// <summary>
/// 実行前の信頼の確認 (AUTO-34 の仕様 1・2・6・8)。
/// <list type="bullet">
/// <item>確認を出さないもの: 同梱のサンプル、アプリ内のエディタで保存したもの・記録したマクロ (信頼済みの一覧に登録済みで内容が同じ)、
/// コンソールの入力、保存していない直前のマクロ、エディタの未保存の内容。宣言した許可を与える (コンソールは許可なし)。</item>
/// <item>「常に許可する」の記録があり内容が同じなら確認を出さない。</item>
/// <item>管理者として実行中は、確認を出さないもの以外は毎回確認する (仕様 8。「常に許可する」の記録があっても省略しない)。</item>
/// <item>ヘッドレス実行: 記録があるか、<c>--trust</c> があれば実行する (<c>--allow</c> の許可を与える)。管理者として実行中は <c>--trust</c> が必要。</item>
/// </list>
/// </summary>
internal sealed class ScriptTrustCheck(ScriptHost host, IScriptEnvironment env)
{
    public async Task<TrustDecision> CheckAsync(ScriptRunRequest request, ScriptRun run, ScriptMetadata meta, string code)
    {
        IReadOnlyCollection<string> declared = meta.Permissions;

        if (request.Origin == ScriptOrigin.Console)
        {
            return TrustDecision.Allow([]);
        }

        // エディタの内容は利用者が書いたものとして扱う。ただし開いたファイルを書き換えずに実行した場合はファイルと同じ扱い。
        if ((request.Origin == ScriptOrigin.Editor && !SameAsFile(request.Path, code)) || request.Origin == ScriptOrigin.UnsavedMacro
            || IsBundledSample(request.Path))
        {
            return TrustDecision.Allow(declared);
        }

        TrustRecord? record = null;
        if (request.Path is not null)
        {
            record = host.Trust.Find(TrustStore.ScriptKind, TrustStore.ScriptKey(request.Path), TrustStore.HashOf(code));
            if (record is { Source: TrustSource.SavedInApp or TrustSource.RecordedMacro })
            {
                // アプリ内で保存したもの・記録したマクロは、管理者として実行中でも確認しない (仕様 8)。
                return TrustDecision.Allow(declared.Union(record.Permissions));
            }
        }

        if (request.Headless)
        {
            if (env.IsElevated && !request.CommandLineTrust)
            {
                return TrustDecision.Deny("管理者として実行中は --trust が必要です。");
            }

            if (request.CommandLineTrust)
            {
                return TrustDecision.Allow(request.CommandLineAllow.Union(record?.Permissions ?? []));
            }

            return record is not null
                ? TrustDecision.Allow(record.Permissions)
                : TrustDecision.Deny("信頼されていないスクリプトです。--trust と --allow を指定するか、アプリで「常に許可する」を選んでください。");
        }

        if (record is not null && !env.IsElevated)
        {
            return TrustDecision.Allow(record.Permissions);
        }

        bool fromInternet = request.Path is not null && MarkOfTheWeb.IsFromInternet(request.Path);
        var prompt = new TrustPrompt(run.Name, request.Path, meta.Author, run.Language, [.. declared.Order(StringComparer.Ordinal)],
            FullTrust: run.Language == ScriptLanguage.CSharp, FromInternet: fromInternet, Elevated: env.IsElevated, ElevationOnly: record is not null);
        TrustAnswer answer = await env.Dispatcher.InvokeAsync(() => env.Ui.ConfirmTrustAsync(prompt)).ConfigureAwait(false);
        switch (answer)
        {
            case TrustAnswer.Deny:
                return TrustDecision.Deny("実行を許可しませんでした。");
            case TrustAnswer.AlwaysAllow when request.Path is not null:
                host.Trust.Add(TrustStore.ScriptKind, TrustStore.ScriptKey(request.Path), TrustStore.HashOf(code), TrustSource.UserAllowed, declared);
                break;
        }

        return TrustDecision.Allow(declared.Union(record?.Permissions ?? []));
    }

    private static bool SameAsFile(string? path, string code)
    {
        try
        {
            return path is not null && File.Exists(path) && File.ReadAllText(path) == code;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsBundledSample(string? path) =>
        path is not null && host.BundledSampleFolders.Any(f => ScriptContext.IsUnder(Path.GetFullPath(path), f));
}
