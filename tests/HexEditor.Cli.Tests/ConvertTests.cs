using System.Security.Cryptography;
using HexEditor.Cli.Tests.Support;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Cli.Tests;

/// <summary>形式の変換 (AUTO-44 の 1、TOOL-04)。</summary>
public sealed class ConvertTests
{
    [Fact]
    [Trait("TC", "TC-AUTO-44-01")]
    public void Intel_hex_to_bin_equals_importing_in_the_gui_and_saving()
    {
        using var dir = new TempDir();
        string hex = dir.Copy("TD-IHEX", "fw.hex");

        CliResult cli = Hexed.Run(dir.Path, "convert", "fw.hex", "fw.bin");
        Assert.Equal(0, cli.ExitCode);

        // GUI のインポート (形式 Intel HEX・既定のオプション・新しいドキュメント) と、名前を付けて保存と同じ部品で作ったファイル。
        string gui = dir.File("fw-gui.bin");
        using (ImportResult imported = Importer.DecodeFile(hex, TransferOptions.ToImportOptions(FormatIds.IntelHex,
            TransferOptions.Load(null, TransferOptions.ImportFields(FormatIds.IntelHex))), Path.Combine(dir.Path, "tmp")))
        {
            using var document = new Document(imported.TakeImage(), new DocumentOptions { TempDirectory = dir.File("recovery") });
            using FileByteSource saved = DocumentSaver.Save(document.Current, gui);
        }

        byte[] expected = File.ReadAllBytes(gui);
        byte[] actual = File.ReadAllBytes(dir.File("fw.bin"));
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
        Assert.Contains((byte)0xFF, actual);
    }

    [Theory]
    [InlineData("ihex", ".hex")]
    [InlineData("srec", ".s37")]
    [InlineData("base64", ".b64")]
    [Trait("TC", "TC-AUTO-44-02")]
    public void Converting_to_a_format_and_back_restores_the_original(string format, string extension)
    {
        using var dir = new TempDir();
        foreach ((string id, string name) in new[] { ("TD-RANDOM-16M", "fw.bin"), ("TD-BYTES-256", "b256.bin") })
        {
            dir.Copy(id, name);
            string encoded = Path.ChangeExtension(name, extension);
            CliResult toFormat = Hexed.Run(dir.Path, "convert", name, encoded, "--to", format);
            Assert.True(toFormat.ExitCode == 0, toFormat.Err);
            string back = "back-" + name;
            CliResult fromFormat = Hexed.Run(dir.Path, "convert", encoded, back);
            Assert.True(fromFormat.ExitCode == 0, fromFormat.Err);

            Assert.Equal(SHA256.HashData(File.ReadAllBytes(dir.File(name))), SHA256.HashData(File.ReadAllBytes(dir.File(back))));
            if (format == "ihex" && id == "TD-RANDOM-16M")
            {
                // 64 KB を超えるため、拡張線形アドレスレコード (04) を含む (I32HEX)。
                Assert.Contains(":02000004", File.ReadAllText(dir.File(encoded)));
            }
        }
    }

    [Fact]
    [Trait("TC", "TC-TOOL-04-05")]
    public void Export_dialog_hexed_convert_and_the_options_give_the_same_files()
    {
        using var dir = new TempDir();
        string firmware = TestDataCatalog.Get("TD-TOOL-FW-128K");
        (string Format, string[] Cli, Dictionary<string, string> Dialog)[] cases =
        [
            ("ihex", ["--record-length", "32", "--start-address", "0x08000000"], new() { ["recordLength"] = "32", ["startAddress"] = "0x08000000" }),
            ("srec", ["--record-length", "32", "--address-mode", "s3"], new() { ["recordLength"] = "32", ["srecMode"] = "s3" }),
            ("base64", ["--line-length", "64"], new() { ["lineLength"] = "64" }),
            ("hextext", ["--bytes-per-line", "32", "--separator", "commaSpace"], new() { ["bytesPerLine"] = "32", ["separator"] = "commaSpace" }),
            ("c", ["--element-size", "4"], new() { ["elementSize"] = "4" }),
        ];
        foreach ((string format, string[] cliOptions, Dictionary<string, string> dialog) in cases)
        {
            // エクスポートのダイアログと同じ設定で書き出したファイル。
            IReadOnlyList<TransferField> fields = TransferOptions.ExportFields(format, null, "TD-TOOL-FW-128K.bin");
            Dictionary<string, string> values = TransferOptions.Load(null, fields);
            foreach ((string key, string value) in dialog)
            {
                values[key] = value;
            }

            ExportOptions options = TransferOptions.ToExportOptions(format, values, text => Convert.ToInt64(text, 16), new DumpOptions());
            using var document = new Document(FileByteSource.Open(firmware), new DocumentOptions { TempDirectory = dir.File("recovery") });
            DocumentSnapshot snapshot = document.Current;
            string gui = dir.File("gui." + format);
            Exporter.WriteFile(gui, stream => Exporter.Write(new ExportSource
            {
                Read = (offset, destination) => snapshot.Read(offset, destination),
                Length = snapshot.Length,
                DataRanges = snapshot.DataRanges,
                FileName = "TD-TOOL-FW-128K.bin",
            }, [(0, snapshot.Length)], options, stream));

            string cli = dir.File("cli." + format);
            CliResult result = Hexed.Run(dir.Path, ["convert", firmware, cli, "--from", "bin", "--to", format, .. cliOptions]);
            Assert.True(result.ExitCode == 0, $"{format}: {result.Err}");
            Assert.Equal(File.ReadAllBytes(gui), File.ReadAllBytes(cli));
        }
    }

    [Fact(Skip = "needs scripting-core merge: doc.exportRange (AUTO-09) of the script host must produce the same files (TC-TOOL-04-05 step 3).")]
    [Trait("TC", "TC-TOOL-04-05")]
    public void Script_export_range_gives_the_same_files()
    {
    }

    [Fact]
    public void Checksum_errors_exit_with_8_unless_ignored()
    {
        using var dir = new TempDir();
        File.Copy(TestDataCatalog.Get("TD-TOOL-IHEX-BADSUM"), dir.File("bad.hex"));

        CliResult bad = Hexed.Run(dir.Path, "convert", "bad.hex", "bad.bin", "--lang", "en");
        Assert.Equal(8, bad.ExitCode);
        Assert.Contains("checksum", bad.Err);
        Assert.Contains("--ignore-checksum", bad.Err);
        Assert.False(File.Exists(dir.File("bad.bin")));

        CliResult ignored = Hexed.Run(dir.Path, "convert", "bad.hex", "ok.bin", "--ignore-checksum", "--lang", "en");
        Assert.Equal(0, ignored.ExitCode);
        Assert.True(File.Exists(dir.File("ok.bin")));
        Assert.Contains("ignored", ignored.Err);
    }

    [Fact]
    public void Format_options_fill_and_address_mode_aliases_and_flags_are_applied()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("in.bin"), [0xAA, 0xBB, 0xCC]);

        CliResult hex = Hexed.Run(dir.Path, "convert", "in.bin", "out.hex", "--to", "ihex", "--address-mode", "linear", "--no-upper-case", "--new-line", "lf");
        Assert.Equal(0, hex.ExitCode);
        string text = File.ReadAllText(dir.File("out.hex"));
        Assert.Contains(":03000000aabbcc", text);
        Assert.DoesNotContain('\r', text);

        // --fill はギャップを埋める値。隙間のある Intel HEX を読む。
        File.WriteAllText(dir.File("gap.hex"), ":0100000011EE\n:0100050022D8\n:00000001FF\n");
        Assert.Equal(0, Hexed.Run(dir.Path, "convert", "gap.hex", "gap.bin", "--fill", "55").ExitCode);
        Assert.Equal([0x11, 0x55, 0x55, 0x55, 0x55, 0x22], File.ReadAllBytes(dir.File("gap.bin")));

        CliResult wrong = Hexed.Run(dir.Path, "convert", "in.bin", "out.b64", "--to", "base64", "--record-length", "32");
        Assert.Equal(2, wrong.ExitCode);
        CliResult unknown = Hexed.Run(dir.Path, "convert", "in.bin", "out.x", "--to", "nonsense");
        Assert.Equal(2, unknown.ExitCode);
        CliResult same = Hexed.Run(dir.Path, "convert", "in.bin", "in.bin");
        Assert.Equal(2, same.ExitCode);
        CliResult missing = Hexed.Run(dir.Path, "convert", "none.bin", "x.hex");
        Assert.Equal(3, missing.ExitCode);
    }
}
