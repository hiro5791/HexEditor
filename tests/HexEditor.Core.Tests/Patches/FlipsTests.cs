using System.Diagnostics;
using HexEditor.Core.Engine;
using HexEditor.Core.Patches;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Patches;

/// <summary>
/// Flips (別のツール) との相互運用を確かめるテストの Fact。環境変数 HEXEDITOR_FLIPS (Flips の実行ファイル) がないときはスキップする。
/// Flips は CI の結合テストの環境 (テストケースの「環境」: CI ランナーに Flips を入れる) だけで用意し、開発者の PC には入れない。
/// 実行ファイルはテストの一時フォルダに複製してから動かす (テストの一時フォルダの外の実行ファイルは動かさない)。
/// </summary>
public sealed class FlipsFactAttribute : FactAttribute
{
    public const string Variable = "HEXEDITOR_FLIPS";

    public FlipsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path || !File.Exists(path))
        {
            Skip = $"{Variable} がない (Flips を用意した CI の結合テストだけで動く)。";
        }
    }
}

/// <summary>EDIT-42・EDIT-43 の Flips との結合テスト (TC-EDIT-42-01・TC-EDIT-42-02・TC-EDIT-43-01)。</summary>
public sealed class FlipsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "flips-" + Guid.NewGuid().ToString("N"));
    private readonly string _flips;

    public FlipsTests()
    {
        Directory.CreateDirectory(_dir);
        _flips = Path.Combine(_dir, "flips.exe");
        if (Environment.GetEnvironmentVariable(FlipsFactAttribute.Variable) is { Length: > 0 } source && File.Exists(source))
        {
            File.Copy(source, _flips);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Flips(params string[] args)
    {
        var start = new ProcessStartInfo(_flips) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _dir };
        foreach (string a in args)
        {
            start.ArgumentList.Add(a);
        }

        using Process p = Process.Start(start)!;
        string stdout = p.StandardOutput.ReadToEnd(), stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000), "Flips が終わりません。");
        Assert.True(p.ExitCode == 0, $"Flips の終了コード {p.ExitCode}: {stdout} {stderr}");
    }

    private string Copy(string id, string name)
    {
        string path = Path.Combine(_dir, name);
        File.Copy(TestDataCatalog.Get(id), path);
        return path;
    }

    private static void Write(Document doc, PatchFormat format, string path)
    {
        using var output = File.Create(path);
        new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot)).Write(format, output);
    }

    [FlipsFact]
    [Trait(TC, "TC-EDIT-42-01")]
    public void IpsFromThreeOverwritesIsAppliedByFlips()
    {
        byte[] mod3 = File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-MOD3"));
        string ips = Path.Combine(_dir, "p.ips");
        using (var doc = new Document(FileByteSource.Open(Copy("TD-EDIT-PATCH-SRC", "src.bin")), Options()))
        {
            foreach (long at in TestDataCatalog.PatchMod3Offsets)
            {
                doc.Overwrite(at, [mod3[at]]);
            }

            Assert.Equal(3, new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot)).Estimate(PatchFormat.Ips).Records);
            Write(doc, PatchFormat.Ips, ips);
        }

        string target = Copy("TD-EDIT-PATCH-SRC", "copy.bin");
        string output = Path.Combine(_dir, "out.bin");
        Flips("--apply", ips, target, output);
        Assert.Equal(mod3, File.ReadAllBytes(output));
    }

    [FlipsFact]
    [Trait(TC, "TC-EDIT-42-02")]
    public void BpsOfMiddleInsertIsSmallAndAppliedByFlips()
    {
        string bps = Path.Combine(_dir, "p.bps");
        using (var doc = new Document(FileByteSource.Open(Copy("TD-EDIT-PATCH-SRC", "src.bin")), Options()))
        {
            doc.InsertPattern(TestDataCatalog.PatchInsertOffset, 100, [0xAA]);
            Assert.True(new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot)).Estimate(PatchFormat.Ips).Inefficient);
            Write(doc, PatchFormat.Bps, bps);
        }

        Assert.True(new FileInfo(bps).Length < 1024);
        string output = Path.Combine(_dir, "out.bin");
        Flips("--apply", bps, Copy("TD-EDIT-PATCH-SRC", "copy.bin"), output);
        Assert.Equal(File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-INS100")), File.ReadAllBytes(output));
    }

    [FlipsFact]
    [Trait(TC, "TC-EDIT-43-01")]
    public void PatchesMadeByFlipsAreApplied()
    {
        string src = Copy("TD-EDIT-PATCH-SRC", "src.bin");
        string ips = Path.Combine(_dir, "flips.ips"), bps = Path.Combine(_dir, "flips.bps");
        Flips("--create", "--ips", src, Copy("TD-EDIT-PATCH-MOD3", "mod3.bin"), ips);
        Flips("--create", "--bps-delta", src, Copy("TD-EDIT-PATCH-INS100", "ins100.bin"), bps);
        foreach ((string patch, string expected, PatchFormat format) in new[] { (ips, "TD-EDIT-PATCH-MOD3", PatchFormat.Ips), (bps, "TD-EDIT-PATCH-INS100", PatchFormat.Bps) })
        {
            using var doc = new Document(new MemoryByteSource(File.ReadAllBytes(src)), Options());
            using LoadedPatch p = PatchFiles.Load(patch);
            Assert.Equal(format, p.Format);
            Assert.Equal(format == PatchFormat.Bps ? PatchVerification.Match : PatchVerification.Unverifiable, p.Verify(doc.Current));
            p.ApplyTo(doc);
            Assert.Equal(File.ReadAllBytes(TestDataCatalog.Get(expected)), ReadAll(doc.Current));
        }
    }
}
