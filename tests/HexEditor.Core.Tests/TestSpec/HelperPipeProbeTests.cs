using System.IO.Pipes;
using System.Runtime.Versioning;
using HexEditor.Core.Elevation;
using HexEditor.ManualTests;

namespace HexEditor.Core.Tests.TestSpec;

/// <summary>手動テスト TC-PKG-14-07 でツールが行うパイプの接続の判定。</summary>
[SupportedOSPlatform("windows")]
public sealed class HelperPipeProbeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    [Fact]
    public void A_helper_pipe_is_found_without_elevation()
    {
        string name = HelperPipes.NewPipeName();
        using NamedPipeServerStream server = HelperPipes.CreateServer(name);
        Assert.Contains(name, HelperPipeProbe.FindPipes());
    }

    [Fact]
    public async Task A_pipe_held_by_the_helper_rejects_the_connection()
    {
        string name = HelperPipes.NewPipeName();
        using NamedPipeServerStream server = HelperPipes.CreateServer(name);
        await using var helper = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), helper.ConnectAsync());
        Assert.Equal(HelperPipeProbeResult.Rejected, await HelperPipeProbe.ProbeAsync(name, Timeout));
    }

    [Fact]
    public async Task A_pipe_waiting_for_the_helper_gives_no_response()
    {
        string name = HelperPipes.NewPipeName();
        using NamedPipeServerStream server = HelperPipes.CreateServer(name);
        Task accept = HelperPipes.AcceptAsync(server, () => -1, new byte[HelperProtocol.SecretSize], TimeSpan.FromSeconds(30));
        Assert.Equal(HelperPipeProbeResult.Silent, await HelperPipeProbe.ProbeAsync(name, Timeout));
        Assert.False(accept.IsCompleted);
    }

    [Fact]
    public async Task A_response_is_reported()
    {
        string name = HelperPipes.NewPipeName();
        using NamedPipeServerStream server = HelperPipes.CreateServer(name);
        Task<HelperPipeProbeResult> probe = HelperPipeProbe.ProbeAsync(name, Timeout);
        await server.WaitForConnectionAsync();
        await server.WriteAsync(new byte[] { 1 });
        Assert.Equal(HelperPipeProbeResult.Responded, await probe);
    }

    [Theory]
    [InlineData(HelperPipeProbeResult.Rejected, "OK:")]
    [InlineData(HelperPipeProbeResult.Silent, "OK:")]
    [InlineData(HelperPipeProbeResult.Responded, "NG:")]
    [InlineData(HelperPipeProbeResult.NoPipe, "まだです:")]
    public void Only_a_refused_request_reads_ok(HelperPipeProbeResult result, string start) =>
        Assert.StartsWith(start, HelperPipeProbe.Describe(result));
}
