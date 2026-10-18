using System.Security.Cryptography;

namespace HexEditor.Core.Scripting.Rpc;

/// <summary>接続してきたプログラム (名前付きパイプのクライアントのプロセスの実行ファイル)。</summary>
/// <param name="ExecutablePath">実行ファイルのフルパス (分からなければ null。その場合は接続を許可しない)。</param>
/// <param name="ProcessId">プロセス ID。</param>
/// <param name="Signer">Authenticode の署名者 (署名がなければ null。確認ダイアログに出す)。</param>
public sealed record JsonRpcClient(string? ExecutablePath, int ProcessId, string? Signer);

/// <summary>接続を許可するかの結果。<see cref="Permissions"/> は接続に与える許可。</summary>
public sealed record JsonRpcAdmission(bool Allowed, IReadOnlyCollection<string> Permissions, string Reason);

/// <summary>
/// 外部からの自動操作の接続の信頼の確認 (AUTO-45 の仕様 4)。初めての実行ファイル (パスと内容の SHA-256 で識別する。内容が変われば再確認)
/// からの接続で、スクリプトと同じ形式の確認 (AUTO-34) を出す。ボタンも同じ: 「1 回だけ」(この接続だけ)、「常に許可する」(設定の
/// 信頼の一覧に記録。取り消せる)、「実行しない」(接続を切る)。
/// <para>
/// 接続してきたプログラムは利用者の権限で動くネイティブのプログラムで、アプリの外で同じことができるため、許可したプログラムには
/// <c>disk</c> 以外の許可を与える (C# スクリプトと同じ完全信頼の扱い。ダイアログにその旨を出す)。<c>disk</c> (ディスク・プロセスメモリを開く)
/// だけは、アプリが管理者として動いている場合に権限を越えないよう、使うときにその場で確認する (AUTO-34 の仕様 4)。
/// アプリを管理者として実行している間は、記録があっても毎回確認する (AUTO-34 の仕様 8)。
/// </para>
/// </summary>
public static class JsonRpcTrust
{
    /// <summary>信頼の一覧 (<c>scripts/trust.json</c>) の種類。</summary>
    public const string Kind = "rpc-client";

    /// <summary>許可したプログラムに与える許可。</summary>
    public static IReadOnlyList<string> GrantedPermissions { get; } =
    [
        ScriptPermissions.FileRead, ScriptPermissions.FileWrite, ScriptPermissions.Network, ScriptPermissions.Process,
        ScriptPermissions.ClipboardRead, ScriptPermissions.Settings,
    ];

    public static string HashOf(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>記録があるか (実行ファイルの内容が同じもの)。</summary>
    public static TrustRecord? FindRecord(TrustStore trust, string executablePath)
    {
        try
        {
            return trust.Find(Kind, TrustStore.ScriptKey(executablePath), HashOf(executablePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>許可したことを記録する (テストの前提の「許可した記録を置いている」もこれで作る)。</summary>
    public static TrustRecord Remember(TrustStore trust, string executablePath) =>
        trust.Add(Kind, TrustStore.ScriptKey(executablePath), HashOf(executablePath), TrustSource.UserAllowed, GrantedPermissions,
            Path.GetFullPath(executablePath));

    /// <summary>確認する (UI スレッド以外から呼ぶ)。拒否すると <see cref="JsonRpcAdmission.Allowed"/> が false。</summary>
    public static async Task<JsonRpcAdmission> CheckAsync(ScriptHost host, IScriptEnvironment env, JsonRpcClient client)
    {
        if (host.Policy() == ScriptSettingKeys.PolicyDisabled)
        {
            return new(false, [], Msg.T("組織の設定 (scripts.policy) で自動化は無効にされています。", "Automation is disabled by the organization setting (scripts.policy)."));
        }

        if (client.ExecutablePath is not { } exe || !File.Exists(exe))
        {
            return new(false, [], Msg.T("接続してきたプログラムを確認できません。", "The connecting program cannot be identified."));
        }

        TrustRecord? record = FindRecord(host.Trust, exe);
        if (record is not null && !env.IsElevated)
        {
            return new(true, GrantedPermissions, string.Empty);
        }

        var prompt = new TrustPrompt(Path.GetFileName(exe), exe, client.Signer, ScriptLanguage.CSharp, [], FullTrust: true,
            FromInternet: MarkOfTheWeb.IsFromInternet(exe), Elevated: env.IsElevated, ElevationOnly: record is not null, External: true);
        TrustAnswer answer = await env.Dispatcher.InvokeAsync(() => env.Ui.ConfirmTrustAsync(prompt)).ConfigureAwait(false);
        switch (answer)
        {
            case TrustAnswer.Deny:
                return new(false, [], Msg.T("利用者が接続を許可しませんでした。", "The user did not allow the connection."));
            case TrustAnswer.AlwaysAllow:
                Remember(host.Trust, exe);
                break;
        }

        return new(true, GrantedPermissions, string.Empty);
    }

    /// <summary>
    /// 許可したプログラムの一覧 (設定画面「外部からの自動操作」。取り消せる)。新しい順。
    /// </summary>
    public static IReadOnlyList<TrustRecord> Allowed(TrustStore trust) =>
        [.. trust.Records.Where(r => r.Kind == Kind).OrderByDescending(r => r.Time)];
}
