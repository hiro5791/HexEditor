using System.Security.Cryptography;
using System.Text;
using HexEditor.Cli.Tests.Support;
using HexEditor.TestData;

namespace HexEditor.Cli.Tests;

/// <summary>ハッシュの計算と検証 (AUTO-38)。</summary>
public sealed class HashTests
{
    [Fact]
    [Trait("TC", "TC-AUTO-38-01")]
    public void Gnu_format_matches_sha256sum()
    {
        using var dir = new TempDir();
        dir.Copy("TD-RANDOM-16M", "a.bin");
        dir.Copy("TD-EMPTY", "empty.bin");
        foreach (string name in new[] { "a.bin", "empty.bin" })
        {
            CliResult cli = Hexed.Run(dir.Path, "hash", name, "-a", "sha256", "--format", "gnu");
            Assert.Equal(0, cli.ExitCode);
            string expected = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(dir.File(name)))) + "  " + name + "\n";
            Assert.Equal(expected, cli.Out.Replace("\r\n", "\n"));

            // Git for Windows に同梱の sha256sum があれば、実物の出力ともバイト単位で比べる。
            if (TestDataCatalog.FindSha256Sum() is { } sha256sum)
            {
                // Git for Windows の sha256sum は既定がバイナリモード (「値 *パス」)。Linux の既定と同じ「値  パス」にするため --text を付ける。
                var info = new System.Diagnostics.ProcessStartInfo(sha256sum) { WorkingDirectory = dir.Path, RedirectStandardOutput = true, UseShellExecute = false };
                info.ArgumentList.Add("--text");
                info.ArgumentList.Add(name);
                using var process = System.Diagnostics.Process.Start(info)!;
                string real = process.StandardOutput.ReadToEnd().Replace("\r\n", "\n");
                process.WaitForExit();
                Assert.Equal(real, cli.Out.Replace("\r\n", "\n"));
            }
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-38-02")]
    public void Check_reports_failed_files_with_exit_code_1()
    {
        using var dir = new TempDir();
        string sums = TestDataCatalog.Get("TD-AUTO-SUMS");
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(sums)!))
        {
            File.Copy(file, dir.File(Path.GetFileName(file)));
        }

        byte[] original = File.ReadAllBytes(dir.File("f2.bin"));
        byte[] tampered = (byte[])original.Clone();
        tampered[100] ^= 0xFF;
        File.WriteAllBytes(dir.File("f2.bin"), tampered);

        CliResult bad = Hexed.Run(dir.Path, "hash", "--check", "SHA256SUMS", "--lang", "en");
        Assert.Equal(1, bad.ExitCode);
        Assert.Equal(["f1.bin: OK", "f2.bin: FAILED", "f3.bin: OK"], bad.OutLines);
        Assert.Contains("did NOT match", bad.Err);

        File.WriteAllBytes(dir.File("f2.bin"), original);
        CliResult good = Hexed.Run(dir.Path, "hash", "--check", "SHA256SUMS");
        Assert.Equal(0, good.ExitCode);
        Assert.Equal(["f1.bin: OK", "f2.bin: OK", "f3.bin: OK"], good.OutLines);

        File.Delete(dir.File("f3.bin"));
        CliResult missing = Hexed.Run(dir.Path, "hash", "--check", "SHA256SUMS");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("f3.bin: MISSING", missing.OutLines);
    }

    [Fact]
    public void Text_and_bsd_and_json_formats_list_every_algorithm_in_one_pass()
    {
        using var dir = new TempDir();
        string path = dir.Copy("TD-BYTES-256", "b.bin");
        byte[] data = File.ReadAllBytes(path);
        string sha1 = Convert.ToHexStringLower(SHA1.HashData(data));
        string md5 = Convert.ToHexStringLower(MD5.HashData(data));

        CliResult text = Hexed.Run(dir.Path, "hash", "b.bin", "-a", "md5,sha1");
        Assert.Equal([$"md5  {md5}  b.bin", $"sha1  {sha1}  b.bin"], text.OutLines);

        CliResult bsd = Hexed.Run(dir.Path, "hash", "b.bin", "-a", "md5", "--format", "bsd");
        Assert.Equal($"MD5 (b.bin) = {md5}", bsd.OutLines[0]);

        CliResult crc = Hexed.Run(dir.Path, "hash", "b.bin", "-a", "crc32");
        Assert.Equal($"crc32  {Support.Crc.Crc32(data):x8}  b.bin", crc.OutLines[0]);

        CliResult gnuTwo = Hexed.Run(dir.Path, "hash", "b.bin", "-a", "md5,sha1", "--format", "gnu");
        Assert.Equal(2, gnuTwo.ExitCode);
    }

    [Fact]
    public void Range_wildcards_folders_and_standard_input_are_supported()
    {
        using var dir = new TempDir();
        byte[] data = File.ReadAllBytes(dir.Copy("TD-BYTES-256", "b.bin"));
        File.WriteAllBytes(dir.File("c.bin"), data[..16]);
        Directory.CreateDirectory(dir.File("sub"));
        File.WriteAllBytes(dir.File("sub\\d.dat"), data[..8]);

        CliResult range = Hexed.Run(dir.Path, "hash", "b.bin", "--range", "16:16", "--format", "gnu");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data[16..32])) + "  b.bin", range.OutLines[0]);

        CliResult wildcard = Hexed.Run(dir.Path, "hash", "*.bin", "--format", "gnu");
        Assert.Equal(2, wildcard.OutLines.Length);
        Assert.EndsWith("  b.bin", wildcard.OutLines[0]);
        Assert.EndsWith("  c.bin", wildcard.OutLines[1]);

        CliResult flat = Hexed.Run(dir.Path, "hash", ".", "--format", "gnu");
        Assert.Equal(2, flat.OutLines.Length);
        CliResult recursive = Hexed.Run(dir.Path, "hash", ".", "--recurse", "--format", "gnu");
        Assert.Equal(3, recursive.OutLines.Length);

        CliResult stdin = Hexed.Run(dir.Path, data[..16], TimeSpan.FromSeconds(60), "hash", "-", "--format", "gnu");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data[..16])) + "  -", stdin.OutLines[0]);
    }

    [Fact]
    public void Unreadable_files_are_reported_on_their_line_and_the_rest_continue_with_exit_code_3()
    {
        using var dir = new TempDir();
        dir.Copy("TD-BYTES-256", "good.bin");
        string locked = dir.Copy("TD-BYTES-256", "locked.bin");
        using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        CliResult result = Hexed.Run(dir.Path, "hash", "good.bin", "locked.bin", "none.bin", "--lang", "en", "--format", "json");
        Assert.Equal(3, result.ExitCode);
        using var json = System.Text.Json.JsonDocument.Parse(result.OutBytes);
        var files = json.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(3, files.Length);
        Assert.True(files[0].TryGetProperty("hashes", out _));
        Assert.True(files[1].TryGetProperty("error", out _));
        Assert.True(files[2].TryGetProperty("error", out _));
        Assert.Contains("locked.bin", result.Err);
    }

    [Fact]
    public void Algorithm_list_prints_the_ids_and_an_unknown_id_is_a_usage_error()
    {
        using var dir = new TempDir();
        CliResult list = Hexed.Run(dir.Path, "hash", "-a", "list");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains(list.OutLines, l => l.StartsWith("sha256  ", StringComparison.Ordinal));
        Assert.Contains(list.OutLines, l => l.StartsWith("crc32  ", StringComparison.Ordinal));

        CliResult unknown = Hexed.Run(dir.Path, "hash", dir.Copy("TD-EMPTY", "e.bin"), "-a", "nosuchalgorithm", "--lang", "en");
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("nosuchalgorithm", unknown.Err);
    }

    [Fact]
    public void Check_file_formats_are_detected_by_extension_and_length()
    {
        using var dir = new TempDir();
        byte[] data = File.ReadAllBytes(dir.Copy("TD-BYTES-256", "x.bin"));
        File.WriteAllText(dir.File("x.bin.md5"), $"{Convert.ToHexStringLower(MD5.HashData(data))} *x.bin\n", new UTF8Encoding(false));
        Assert.Equal(0, Hexed.Run(dir.Path, "hash", "--check", "x.bin.md5").ExitCode);

        File.WriteAllText(dir.File("x.sfv"), $"; comment\nx.bin {Support.Crc.Crc32(data):X8}\n", new UTF8Encoding(false));
        CliResult sfv = Hexed.Run(dir.Path, "hash", "--check", "x.sfv");
        Assert.Equal(0, sfv.ExitCode);
        Assert.Equal(["x.bin: OK"], sfv.OutLines);

        File.WriteAllText(dir.File("empty.sha256"), "no checksums here\n");
        Assert.Equal(8, Hexed.Run(dir.Path, "hash", "--check", "empty.sha256").ExitCode);
    }
}
