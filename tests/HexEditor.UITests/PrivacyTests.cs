using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>テレメトリを送らない方針 (UI-57): 普段の操作で外部への通信がない。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class PrivacyTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-UI-57-01")]
    [Trait(UiTest.Category, "Nightly")]
    public Task An_hour_of_operations_makes_no_outbound_connections() => UiTestContext.RunAsync(async ctx =>
    {
        // ETW の TCP/IP・DNS の記録の代わりに、(1) アプリの通信の窓口 (NetworkClient) の記録と、(2) プロセスの TCP 接続の一覧
        // (GetExtendedTcpTable) を操作のたびに調べる。時間は環境変数 HEXEDITOR_UI57_MINUTES (既定 60 分) で変えられる。
        double minutes = double.TryParse(Environment.GetEnvironmentVariable("HEXEDITOR_UI57_MINUTES"), out double m) ? m : 60;

        // TD-UI-SET-NOUPDATE: 自動の更新の確認をしない。
        string profile = ctx.NewProfile();
        ViewOps.WriteSettings(profile, new JsonObject { ["update.checkAutomatically"] = false });
        string seq = ctx.CopyTestData("TD-SEQ-1M");
        string random = ctx.CopyTestData("TD-RANDOM-16M");

        // TD-PE-X64 の代わりに、テスト対象の HexEditor の実行ファイルの複製。
        string pe = Path.Combine(ctx.Root, "pe-x64.exe");
        File.Copy(AppLocator.ExePath, pe, overwrite: true);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [seq] });

        var outbound = new List<string>();
        void CheckConnections(string step)
        {
            foreach (IPEndPoint remote in TcpConnections.RemoteEndPoints(app.Process.Id))
            {
                if (!IPAddress.IsLoopback(remote.Address) && !remote.Address.Equals(IPAddress.Any) && !remote.Address.Equals(IPAddress.IPv6Any))
                {
                    outbound.Add($"{step}: {remote}");
                }
            }
        }

        // 1. 開く・編集・検索・すべて検索・ハッシュ・保存・設定の変更・タブの操作を、決めた時間だけ繰り返す。
        var watch = Stopwatch.StartNew();
        int round = 0;
        do
        {
            round++;
            await app.OpenAsync(random);
            await app.OpenAsync(pe);
            await app.WaitForTabsAsync(3);
            await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
            await app.IdleAsync();
            CheckConnections("open");

            await app.GoToAsync(0x40);
            await app.TypeAsync("4142");
            await SearchResultsTests.OpenFindAsync(app, 0, "41 42");
            await app.SendAsync("findKey", new JsonObject { ["key"] = "Enter" });
            await SearchResultsTests.FindAllAsync(app);
            await SearchResultsTests.WaitForResultsAsync(app, r => r["state"]?.GetValue<string>() == "Completed" && !r["running"]!.GetValue<bool>(), "the results");
            await app.SendAsync("findKey", new JsonObject { ["key"] = "Escape" });
            CheckConnections("search");

            await app.SendAsync("hash", new JsonObject { ["action"] = "show" });
            await app.CommandAsync("Hash_Compute");
            await app.WaitUntilAsync(async () => !(await app.SendAsync("hash", new JsonObject { ["action"] = "state" }))["computing"]!.GetValue<bool>(),
                TimeSpan.FromSeconds(60), "the hash");
            CheckConnections("hash");

            await app.KeyAsync("S", ctrl: true);
            await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(30), "the save");
            await app.SendAsync("settingSet", new JsonObject { ["key"] = "ui.theme", ["value"] = round % 2 == 0 ? "light" : "dark" });
            CheckConnections("save and settings");

            // タブを閉じて開き直す。
            await app.SendAsync("selectTab", new JsonObject { ["index"] = 2 });
            await app.KeyAsync("W", ctrl: true);
            await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
            await app.KeyAsync("W", ctrl: true);
            await app.WaitForTabsAsync(1);
            await app.KeyAsync("T", ctrl: true, shift: true);
            await app.WaitForTabsAsync(2);
            await app.KeyAsync("W", ctrl: true);
            await app.WaitForTabsAsync(1);
            CheckConnections("tabs");
        }
        while (watch.Elapsed.TotalMinutes < minutes);

        // 2. アプリの通信の記録と TCP 接続にループバック以外がない。
        Assert.Empty((await app.SendAsync("networkLog"))["requests"]!.AsArray());
        Assert.True(outbound.Count == 0, "outbound connections:\n" + string.Join("\n", outbound.Distinct()));
        Assert.True(round > 0);
    });

    /// <summary>プロセスの TCP 接続の相手 (IPv4・IPv6)。</summary>
    private static class TcpConnections
    {
        private const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidAll = 5;

        public static IEnumerable<IPEndPoint> RemoteEndPoints(int pid) => Read(AfInet, pid).Concat(Read(AfInet6, pid));

        private static List<IPEndPoint> Read(int family, int pid)
        {
            var result = new List<IPEndPoint>();
            int size = 0;
            _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidAll, 0);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0) != 0)
                {
                    return result;
                }

                int count = Marshal.ReadInt32(buffer);
                int rowSize = family == AfInet ? 24 : 56;
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = buffer + 4 + (i * rowSize);
                    if (family == AfInet)
                    {
                        if (Marshal.ReadInt32(row + 20) != pid)
                        {
                            continue;
                        }

                        var address = new IPAddress((uint)Marshal.ReadInt32(row + 12));
                        int port = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt16(row + 16));
                        result.Add(new IPEndPoint(address, port));
                    }
                    else
                    {
                        if (Marshal.ReadInt32(row + 52) != pid)
                        {
                            continue;
                        }

                        byte[] bytes = new byte[16];
                        Marshal.Copy(row + 24, bytes, 0, 16);
                        int port = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt16(row + 44));
                        result.Add(new IPEndPoint(new IPAddress(bytes), port));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    }
}
