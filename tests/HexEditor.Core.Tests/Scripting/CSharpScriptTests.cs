using System.IO.Compression;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.CSharp;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// テスト用の C# スクリプト コンポーネントの配布元 (ローカルのフォルダ。通信しない)。テストの出力の <c>csharp-component\</c> (ビルドした
/// コンポーネント) を zip にし、検証用の一覧を作る。
/// </summary>
public sealed class ComponentFeedFixture : IDisposable
{
    public const string Version = "test-1";

    public ComponentFeedFixture()
    {
        Folder = Path.Combine(Path.GetTempPath(), "HexEditor", "component-feed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        string source = Path.Combine(AppContext.BaseDirectory, "csharp-component");
        Assert.True(File.Exists(Path.Combine(source, CSharpComponentManager.EntryAssembly)), "テストの出力にコンポーネントがありません: " + source);
        ZipPath = Path.Combine(Folder, "hexeditor-csharp-" + Version + ".zip");
        ZipFile.CreateFromDirectory(source, ZipPath);
        Manifest = CSharpComponentManifest.Create(Version, source, ZipPath);

        // 1 バイト書き換えた zip (TD-AUTO-CSX-COMPONENT-BAD)。
        BadZipPath = Path.Combine(Folder, "bad.zip");
        File.Copy(ZipPath, BadZipPath);
        using (var stream = new FileStream(BadZipPath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length / 2;
            int b = stream.ReadByte();
            stream.Position = stream.Length / 2;
            stream.WriteByte((byte)(b ^ 0xFF));
        }
    }

    public string Folder { get; }

    public string ZipPath { get; }

    public string BadZipPath { get; }

    public CSharpComponentManifest Manifest { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>ローカルの zip を返す配布元。ダウンロードした回数を数える。</summary>
public sealed class LocalComponentFeed(string zipPath) : ICSharpComponentFeed
{
    public string Host => "localhost.test";

    public int Downloads { get; private set; }

    public async Task DownloadAsync(CSharpComponentManifest manifest, Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Downloads++;
        await using FileStream source = File.OpenRead(zipPath);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
    }
}

/// <summary>署名の確認の偽物 (実際の WinVerifyTrust は Platform のテスト)。</summary>
public sealed class FakeVerifier(bool result = true) : IAuthenticodeVerifier
{
    public List<string> Checked { get; } = [];

    public bool IsMicrosoftSigned(string path, out string? error)
    {
        Checked.Add(Path.GetFileName(path));
        error = result ? null : "署名がありません";
        return result;
    }
}

/// <summary>C# スクリプト (AUTO-03) とコンポーネントの追加。</summary>
public sealed class CSharpScriptTests(ComponentFeedFixture feed) : IClassFixture<ComponentFeedFixture>
{
    private (ScriptTestHost Host, CSharpComponentManager Manager, LocalComponentFeed Feed) Create(bool downloads = true, bool install = true,
        bool signed = true, string? zip = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "HexEditor", "csx-tests", Guid.NewGuid().ToString("N"));
        var localFeed = new LocalComponentFeed(zip ?? feed.ZipPath);
        var manager = new CSharpComponentManager(Path.Combine(root, "components"), feed.Manifest, localFeed, new FakeVerifier(signed), () => downloads);
        if (install)
        {
            manager.InstallFromZipAsync(feed.ZipPath, CancellationToken.None).GetAwaiter().GetResult();
        }

        return (new ScriptTestHost(csharp: manager), manager, localFeed);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-03-02")]
    public async Task First_csx_run_offers_the_component_and_runs_after_installing()
    {
        (ScriptTestHost t, CSharpComponentManager manager, LocalComponentFeed localFeed) = Create(install: false);
        using (t)
        {
            Assert.False(manager.IsInstalled);
            (long size, string destination, string host) = manager.DownloadInfo;
            Assert.Equal(new FileInfo(feed.ZipPath).Length, size);
            Assert.EndsWith(Path.Combine("components", "csharp", ComponentFeedFixture.Version), destination);
            Assert.Equal("localhost.test", host);
            int offers = 0;
            t.Ui.OfferComponent = () =>
            {
                offers++;
                Task.Run(() => manager.InstallAsync(null, CancellationToken.None)).GetAwaiter().GetResult();
                return true;
            };
            string path = t.WriteScript("hello.csx", "Console.WriteLine(\"hello from csx\");");
            t.TrustScript(path);
            ScriptRunResult r = await t.RunFileAsync(path);
            ScriptTestHost.AssertSucceeded(r);
            Assert.Equal(1, offers);
            Assert.Equal(1, localFeed.Downloads);
            Assert.Contains("hello from csx", t.OutputText);
            Assert.True(File.Exists(Path.Combine(destination, CSharpComponentManager.EntryAssembly)));
        }
    }

    [Fact]
    public async Task Canceling_the_offer_does_not_run_the_script()
    {
        (ScriptTestHost t, _, LocalComponentFeed localFeed) = Create(install: false);
        using (t)
        {
            t.Ui.OfferComponent = () => false;
            string path = t.WriteScript("hello.csx", "Console.WriteLine(\"hello from csx\");");
            t.TrustScript(path);
            ScriptRunResult r = await t.RunFileAsync(path);
            Assert.Equal(ScriptFailure.Io, r.Failure);
            Assert.Equal(0, localFeed.Downloads);
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-03-03")]
    public async Task Manually_extracted_component_is_recognized_offline()
    {
        (ScriptTestHost t, CSharpComponentManager manager, LocalComponentFeed localFeed) = Create(downloads: false, install: false);
        using (t)
        {
            // オフラインではダウンロードせず、手動の手順を案内する。
            CSharpComponentException offline = await Assert.ThrowsAsync<CSharpComponentException>(() => manager.InstallAsync(null, CancellationToken.None));
            Assert.True(offline.ManualInstructions);
            Assert.Equal(0, localFeed.Downloads);

            ZipFile.ExtractToDirectory(feed.ZipPath, manager.VersionFolder!);
            Assert.True(manager.IsInstalled);
            Assert.Contains(ComponentFeedFixture.Version, manager.InstalledVersions);
            t.Ui.OfferComponent = () => throw new InvalidOperationException("案内を出してはいけない");
            string path = t.WriteScript("hello.csx", "Console.WriteLine(\"hello from csx\");");
            t.Ui.TrustAnswer = TrustAnswer.RunOnce;
            ScriptRunResult r = await t.RunFileAsync(path);
            ScriptTestHost.AssertSucceeded(r);
            Assert.Contains("hello from csx", t.OutputText);
            Assert.Equal(0, localFeed.Downloads);
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-03-07")]
    public async Task Tampered_component_zip_is_rejected()
    {
        (ScriptTestHost t, CSharpComponentManager manager, _) = Create(install: false, zip: feed.BadZipPath);
        using (t)
        {
            CSharpComponentException ex = await Assert.ThrowsAsync<CSharpComponentException>(() => manager.InstallAsync(null, CancellationToken.None));
            Assert.Contains("hash", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(ex.ManualInstructions);
            Assert.False(manager.IsInstalled);
            Assert.True(!Directory.Exists(manager.Folder) || !Directory.EnumerateFiles(manager.Folder, "*", SearchOption.AllDirectories).Any());
        }
    }

    [Fact]
    public async Task Tampered_files_in_an_extracted_component_are_rejected_when_loading()
    {
        (ScriptTestHost t, CSharpComponentManager manager, _) = Create(install: false);
        using (t)
        {
            ZipFile.ExtractToDirectory(feed.ZipPath, manager.VersionFolder!);
            string dll = Path.Combine(manager.VersionFolder!, CSharpComponentManager.EntryAssembly);
            byte[] bytes = await File.ReadAllBytesAsync(dll);
            bytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(dll, bytes);
            string path = t.WriteScript("hello.csx", "Console.WriteLine(1);");
            t.TrustScript(path);
            ScriptRunResult r = await t.RunFileAsync(path);
            Assert.Equal(ScriptFailure.Io, r.Failure);
            Assert.Contains("改ざん", r.Message);
        }
    }

    [Fact]
    public async Task Unsigned_roslyn_assemblies_are_rejected()
    {
        (ScriptTestHost t, CSharpComponentManager manager, _) = Create(install: false, signed: false);
        using (t)
        {
            CSharpComponentException ex = await Assert.ThrowsAsync<CSharpComponentException>(() => manager.InstallFromZipAsync(feed.ZipPath, CancellationToken.None));
            Assert.Contains("sign", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(manager.IsInstalled);
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-03-05")]
    public async Task Allowed_assemblies_can_be_called()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            t.Open([1, 2, 3, 4]);
            string path = t.WriteScript("apis.csx", """
                using System.Text.Json;
                using System.Text.RegularExpressions;
                using System.Security.Cryptography;
                using System.Numerics;
                using System.Collections.Generic;
                Console.WriteLine("runtime:" + Math.Max(1, 2));
                Console.WriteLine("collections:" + new SortedSet<int> { 3, 1, 2 }.Min);
                Console.WriteLine("linq:" + Enumerable.Range(1, 4).Sum());
                Console.WriteLine("regex:" + Regex.Match("xxabcxx", "a.c").Value);
                Console.WriteLine("json:" + JsonSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1 }));
                Console.WriteLine("memory:" + "hello".AsSpan(1, 3).ToString());
                Console.WriteLine("biginteger:" + BigInteger.Pow(2, 128));
                Console.WriteLine("sha256:" + Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 })));
                Console.WriteLine("io:" + Path.GetExtension("a/b.bin"));
                Console.WriteLine("sdk:" + Doc!.Length + "," + App.ApiVersion + "," + Doc.ReadByte(2));
                """);
            t.TrustScript(path);
            ScriptRunResult r = await t.RunFileAsync(path);
            ScriptTestHost.AssertSucceeded(r);
            Assert.Contains("runtime:2", t.OutputText);
            Assert.Contains("collections:1", t.OutputText);
            Assert.Contains("linq:10", t.OutputText);
            Assert.Contains("regex:abc", t.OutputText);
            Assert.Contains("json:{\"a\":1}", t.OutputText);
            Assert.Contains("memory:ell", t.OutputText);
            Assert.Contains("biginteger:340282366920938463463374607431768211456", t.OutputText);
            Assert.Contains("sha256:039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81", t.OutputText);
            Assert.Contains("io:.bin", t.OutputText);
            Assert.Contains("sdk:4,1.0,3", t.OutputText);
        }
    }

    [Fact]
    public async Task References_outside_the_allowed_list_and_nuget_are_compile_errors()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            string path = t.WriteScript("refs.csx", "#r \"nuget: Newtonsoft.Json, 13.0.1\"\n#r \"System.Net.Http\"\nConsole.WriteLine(1);");
            t.TrustScript(path);
            ScriptRunResult r = await t.RunFileAsync(path);
            Assert.Equal(ScriptRunStatus.Failed, r.Status);
            Assert.Equal(4, r.ProcessExitCode);
            Assert.True(r.Diagnostics.Count >= 1);
            Assert.All(r.Diagnostics, d => Assert.True(d.Line is 1 or 2, d.ToString()));
        }
    }

    [Fact]
    public async Task Load_reads_only_the_script_folder_and_library()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            t.WriteScript("lib/common.csx", "static int Twice(int x) => x * 2;");
            t.WriteScript("tools/local.csx", "static string Local() => \"local\";");
            string outside = Path.Combine(t.Root, "outside.csx");
            File.WriteAllText(outside, "static int Bad() => 1;");
            string ok = t.WriteScript("tools/main.csx", "#load \"common.csx\"\n#load \"local.csx\"\nConsole.WriteLine(Twice(21) + Local());");
            t.TrustScript(ok);
            ScriptTestHost.AssertSucceeded(await t.RunFileAsync(ok));
            Assert.Contains("42local", t.OutputText);

            string bad = t.WriteScript("tools/bad.csx", $"#load \"{outside.Replace("\\", "\\\\")}\"\nConsole.WriteLine(Bad());");
            t.TrustScript(bad);
            ScriptRunResult r = await t.RunFileAsync(bad);
            Assert.Equal(ScriptRunStatus.Failed, r.Status);
        }
    }

    [Fact]
    public async Task Exception_stack_trace_has_the_csx_line()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            string path = t.WriteScript("throw.csx", "int x = 1;\nvoid Boom() => throw new InvalidOperationException(\"boom\");\n\nBoom();\n");
            t.TrustScript(path);
            ScriptRunResult r = await t.RunFileAsync(path);
            Assert.Equal(ScriptRunStatus.Failed, r.Status);
            Assert.Contains("boom", r.Message);
            Assert.Contains("throw.csx", r.StackTrace);
            Assert.Contains(r.Diagnostics, d => d.Line is 2 or 4);
        }
    }

    [Fact]
    public async Task Compile_errors_are_listed_with_line_column_and_code()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            string path = t.WriteScript("err.csx", "int a = 1;\nint b = undefinedName;\n");
            t.TrustScript(path);
            ScriptRunResult r = await t.RunFileAsync(path);
            Assert.Equal(ScriptRunStatus.Failed, r.Status);
            ScriptDiagnostic d = Assert.Single(r.Diagnostics);
            Assert.Equal("CS0103", d.Code);
            Assert.Equal(2, d.Line);
            Assert.True(d.Column > 1);
            Assert.Contains(t.OutputLines, l => l.Line == 2 && l.Text.Contains("CS0103"));
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-03-06")]
    public async Task Ct_checking_loop_stops_with_the_stop_button()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            string path = t.WriteScript("ctloop.csx", "Console.WriteLine(\"started\");\nwhile (!Ct.IsCancellationRequested) { }\nConsole.WriteLine(\"stopped\");");
            t.TrustScript(path);
            ScriptRun run = t.Start(new ScriptRunRequest { Path = path });
            await JsRuntimeTests.WaitUntilAsync(() => t.OutputText.Contains("started"));
            DateTimeOffset stop = DateTimeOffset.UtcNow;
            run.Stop();
            ScriptRunResult r = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(ScriptRunStatus.Stopped, r.Status);
            Assert.False(run.IsNotResponding);
            Assert.Contains("stopped", t.OutputText);
            ScriptOutputLine stopped = t.OutputLines.First(l => l.Text == "stopped");
            Support.TimeBudget.Limit(stopped.Time - stop < TimeSpan.FromSeconds(3), $"{stopped.Time - stop}");
        }
    }

    [Fact]
    public async Task Loop_ignoring_ct_is_marked_not_responding()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            // Ct を見ないループ (3 秒で自分で終わる)。強制中断できないため「応答しません」になる。
            string path = t.WriteScript("busy.csx", "var until = DateTime.UtcNow.AddSeconds(3); Console.WriteLine(\"started\"); while (DateTime.UtcNow < until) { }");
            t.TrustScript(path);
            ScriptRun run = t.Start(new ScriptRunRequest { Path = path });
            await JsRuntimeTests.WaitUntilAsync(() => t.OutputText.Contains("started"));
            run.Stop();
            await JsRuntimeTests.WaitUntilAsync(() => run.IsNotResponding || run.Completion.IsCompleted);
            Assert.True(run.IsNotResponding);
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-03-04")]
    [Trait("Category", "Performance")]
    public async Task Second_run_starts_from_the_cache_within_200_ms()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            string path = t.WriteScript("timestamp.csx", "Console.WriteLine(\"t\");");
            t.TrustScript(path);
            ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
            int cached = Directory.GetFiles(t.Folders.CSharpCache).Length;
            Assert.True(cached > 0);
            var delays = new List<TimeSpan>();
            for (int i = 0; i < 10; i++)
            {
                t.Env.Output.Clear();
                ScriptRun run = t.Start(new ScriptRunRequest { Path = path });
                ScriptRunResult r = await run.Completion;
                ScriptTestHost.AssertSucceeded(r);
                ScriptOutputLine first = t.OutputLines.First(l => l.Text == "t");
                delays.Add(first.Time - run.StartedAt);
            }

            delays.Sort();
            Assert.Equal(cached, Directory.GetFiles(t.Folders.CSharpCache).Length);
            Support.TimeBudget.Limit(delays[5] <= TimeSpan.FromSeconds(0.2), $"中央値 {delays[5].TotalMilliseconds:0} ms");
        }
    }

    [Fact]
    public async Task Editing_a_loaded_file_invalidates_the_cache()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            string lib = t.WriteScript("lib/value.csx", "static int Value() => 1;");
            string path = t.WriteScript("use.csx", "#load \"value.csx\"\nConsole.WriteLine(\"v\" + Value());");
            t.TrustScript(path);
            ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
            File.WriteAllText(lib, "static int Value() => 2;");
            ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
            Assert.Contains("v1", t.OutputText);
            Assert.Contains("v2", t.OutputText);
        }
    }

    [Fact]
    public async Task Csx_edits_are_one_undo_unit_and_use_the_same_api()
    {
        (ScriptTestHost t, _, _) = Create();
        using (t)
        {
            CoreDocumentHost doc = t.Open(new byte[64]);
            string path = t.WriteScript("edit.csx", "Doc!.WriteBytes(0, new byte[] { 0xAA });\nDoc.Fill(\"0x10:0x10\", \"FF\");\nawait Doc.TransformAsync(\"0:4\", \"xor\", 0x0F);");
            t.TrustScript(path);
            ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
            byte[] after = t.OnUi(() => ReadAll(doc));
            Assert.Equal(0xA5, after[0]);
            Assert.Equal(0x0F, after[1]);
            Assert.Equal(0xFF, after[0x10]);
            t.OnUi(() => doc.Document.Undo());
            Assert.All(t.OnUi(() => ReadAll(doc)), b => Assert.Equal(0, b));
        }
    }

    internal static byte[] ReadAll(CoreDocumentHost doc)
    {
        byte[] data = new byte[doc.Document.Length];
        doc.Document.Current.Read(0, data);
        return data;
    }
}
