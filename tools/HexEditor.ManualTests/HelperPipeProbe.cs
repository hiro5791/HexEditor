using System.IO.Pipes;
using System.Runtime.Versioning;
using HexEditor.Core.Elevation;

namespace HexEditor.ManualTests;

/// <summary><see cref="HelperPipeProbe"/> の結果。悪いほど大きい値。</summary>
public enum HelperPipeProbeResult
{
    /// <summary>補助プロセスのパイプがない (ディスクが開かれていない)。</summary>
    NoPipe,

    /// <summary>接続できない。</summary>
    Rejected,

    /// <summary>接続できたが、要求に応答がない。</summary>
    Silent,

    /// <summary>応答が返った (不具合)。</summary>
    Responded,
}

/// <summary>
/// 手動テスト TC-PKG-14-07: 補助プロセスのパイプに、このツール (HexEditor.exe ではない、昇格していないプロセス) から接続し、要求を送る。
/// パイプ名は一般の権限で一覧できるので、管理者の権限は要らない。
/// </summary>
[SupportedOSPlatform("windows")]
public static class HelperPipeProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>ReadSectors の形の要求 (長さ 12、要求 ID 1、コマンド・フラグ、ハンドル 1・オフセット 0)。合言葉は知らない。</summary>
    private static readonly byte[] Request = Convert.FromHexString("0C000000010000000700000001000000" + "0000000000000000");

    public static IReadOnlyList<string> FindPipes() =>
        Directory.GetFiles(@"\\.\pipe\", HelperPipes.Prefix + "*").Select(path => Path.GetFileName(path)).ToList();

    /// <summary>いま動いている補助プロセスのパイプをすべて試し、いちばん悪い結果を返す。</summary>
    public static async Task<HelperPipeProbeResult> RunAsync(TimeSpan timeout)
    {
        HelperPipeProbeResult worst = HelperPipeProbeResult.NoPipe;
        foreach (string name in FindPipes())
        {
            HelperPipeProbeResult result = await ProbeAsync(name, timeout);
            worst = result > worst ? result : worst;
        }

        return worst;
    }

    public static async Task<HelperPipeProbeResult> ProbeAsync(string name, TimeSpan timeout)
    {
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            await pipe.ConnectAsync(limit.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or UnauthorizedAccessException or IOException)
        {
            return HelperPipeProbeResult.Rejected;
        }

        try
        {
            await pipe.WriteAsync(Request, limit.Token);
            await pipe.FlushAsync(limit.Token);
            return await pipe.ReadAsync(new byte[4096], limit.Token) > 0 ? HelperPipeProbeResult.Responded : HelperPipeProbeResult.Silent;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            return HelperPipeProbeResult.Silent;
        }
    }

    public static string Describe(HelperPipeProbeResult result) => result switch
    {
        HelperPipeProbeResult.Rejected => "OK: 別のプロセスからは接続できません。",
        HelperPipeProbeResult.Silent => "OK: 接続できましたが、応答は返りません。",
        HelperPipeProbeResult.Responded => "NG: 別のプロセスに応答が返りました。",
        _ => "まだです: 補助プロセスのパイプがありません。ディスク 0 を開いてから、もう一度押してください。",
    };
}
