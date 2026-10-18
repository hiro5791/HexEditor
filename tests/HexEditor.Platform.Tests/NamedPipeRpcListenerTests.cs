using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using HexEditor.Platform.Automation;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// 外部からの自動操作の名前付きパイプ (08 の AUTO-45 の仕様 2・4)。テスト用の名前のパイプを作る (アプリのパイプ名は使わない)。
/// 別の利用者からの接続の拒否は、利用者を作れる CI の管理者のジョブで確かめる (UI テストの TC-AUTO-45-03)。ここでは ACL を確かめる。
/// </summary>
public sealed class NamedPipeRpcListenerTests
{
    private static string TestName() => "HexEditor.platform-test." + Guid.NewGuid().ToString("N") + ".rpc";

    [Fact]
    [Trait(TC, "TC-AUTO-45-01")]
    public async Task Pipe_exists_only_while_the_listener_runs()
    {
        string name = TestName();
        Assert.False(NamedPipeRpcListener.Exists(name));
        var listener = new NamedPipeRpcListener(name, _ => Task.CompletedTask);
        listener.Start();
        Assert.True(NamedPipeRpcListener.Exists(name));
        await listener.DisposeAsync();
        Assert.False(NamedPipeRpcListener.Exists(name));
    }

    [Fact]
    public void Pipe_name_contains_the_user_sid()
    {
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        Assert.Equal($"HexEditor.{sid}.rpc", NamedPipeRpcListener.CurrentUserPipeName);
        Assert.Equal(@"\\.\pipe\HexEditor.S-1-5-21-1.rpc", NamedPipeRpcListener.FullName(NamedPipeRpcListener.NameFor(new SecurityIdentifier("S-1-5-21-1"))));
    }

    [Fact]
    [Trait(TC, "TC-AUTO-45-03")]
    public void Acl_allows_only_the_current_user_and_denies_network_logons()
    {
        PipeSecurity security = NamedPipeRpcListener.CreateSecurity();
        Assert.True(security.AreAccessRulesProtected); // 継承しない (ほかの利用者・グループの許可が入らない)
        AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        var allows = rules.Cast<PipeAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow).ToList();
        Assert.All(allows, r => Assert.Equal(user, r.IdentityReference));
        Assert.Single(allows);
        Assert.Contains(rules.Cast<PipeAccessRule>(), r => r.AccessControlType == AccessControlType.Deny
            && r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
    }

    [Fact]
    [Trait(TC, "TC-AUTO-45-04")]
    public async Task Connection_reports_the_client_process_and_executable()
    {
        string name = TestName();
        var connected = new TaskCompletionSource<PipeClientConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource();
        await using var listener = new NamedPipeRpcListener(name, async c =>
        {
            connected.TrySetResult(c);
            await release.Task;
        });
        listener.Start();
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        await client.ConnectAsync(10_000);
        PipeClientConnection connection = await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Environment.ProcessId, connection.ProcessId);
        Assert.Equal(Process.GetCurrentProcess().MainModule!.FileName, connection.ExecutablePath, ignoreCase: true);
        release.SetResult();
    }

    [Fact]
    public async Task Second_listener_with_the_same_name_fails()
    {
        string name = TestName();
        await using var first = new NamedPipeRpcListener(name, _ => Task.CompletedTask);
        first.Start();
        var second = new NamedPipeRpcListener(name, _ => Task.CompletedTask);
        Assert.ThrowsAny<Exception>(second.Start);
    }
}
