using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 暗号化と復号 (EDIT-40)、圧縮と展開 (EDIT-41)、派生ドキュメント (EDIT-46)、YARA スキャン (FIND-33)。ダイアログの欄はテスト用の命令
/// codecDialogSet で入れ (鍵の欄はパスワードの入力欄)、状態は codecDialog・derived・yaraPanel で読む。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class DataCodecUiTests
{
    private const long MiB = 1024 * 1024;

    // ---- 共通 ----

    private static async Task<JsonObject> OpenCodecDialogAsync(AppSession app, string command, string kind)
    {
        await app.CommandAsync(command);
        JsonObject state = null!;
        await app.WaitUntilAsync(async () => (state = await app.SendAsync("codecDialog"))["open"]!.GetValue<bool>()
            && state["kind"]!.GetValue<string>() == kind, UiTest.Scaled(TimeSpan.FromSeconds(15)), "the " + kind + " dialog");
        await app.WaitUntilAsync(async () => (await app.SendAsync("openDialogs"))["ready"]!.GetValue<int>() > 0, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the dialog buttons");
        return state;
    }

    private static async Task<JsonObject> SetAsync(AppSession app, string field, string value)
    {
        JsonObject state = await app.SendAsync("codecDialogSet", new JsonObject { ["field"] = field, ["value"] = value });
        await app.IdleAsync();
        return state;
    }

    private static async Task SetAllAsync(AppSession app, params (string Field, string Value)[] fields)
    {
        foreach ((string field, string value) in fields)
        {
            await SetAsync(app, field, value);
        }
    }

    /// <summary>閉じるときの確認 (処理の数え方の外のダイアログ) のボタンを、表示されたら押す。</summary>
    private static Task PressWhenReadyAsync(AppSession app, string button) => app.WaitUntilAsync(async () =>
    {
        try
        {
            await app.SendAsync("dialogButton", new JsonObject { ["name"] = button });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }, UiTest.Scaled(TimeSpan.FromSeconds(10)), button);

    private static async Task WaitIdleOperationsAsync(AppSession app, TimeSpan timeout) =>
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0, UiTest.Scaled(timeout), "the operations");

    private const string GcmKey = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    private const string GcmNonce = "000000000000000000000001";

    private static async Task GcmAsync(AppSession app, bool encrypt)
    {
        await OpenCodecDialogAsync(app, encrypt ? "Command_Encrypt" : "Command_Decrypt", encrypt ? "encrypt" : "decrypt");
        await SetAllAsync(app, ("algorithm", "Aes256"), ("mode", "Gcm"), ("key", GcmKey), ("iv", GcmNonce), ("tagPlacement", encrypt ? "append" : "end"),
            ("newTab", "false"));
        Assert.True((await app.SendAsync("codecDialog"))["runEnabled"]!.GetValue<bool>());
        await EditCommandTests.PressAsync(app);
        await WaitIdleOperationsAsync(app, TimeSpan.FromSeconds(30));
        await app.IdleAsync();
    }

    // ---- EDIT-40 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-40-02")]
    public Task Aes256_gcm_round_trip_and_tampering() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });

        // 1〜3. 0x1000 バイトを暗号化すると、タグが末尾に付いて長さは 1,048,576 + 16。
        await app.SelectAsync(0, 0x1000);
        await GcmAsync(app, encrypt: true);
        Assert.Equal(MiB + 16, (await app.DocumentAsync())["length"]!.GetValue<long>());

        // 4. 結果のオフセット 0x10 のバイトの 1 ビットを反転してから、同じ鍵・ナンスで復号する。
        byte original = (await app.BytesAsync(0x10, 1))[0];
        await app.GoToAsync(0x10);
        await app.TypeAsync(((byte)(original ^ 0x01)).ToString("X2"));
        await app.IdleAsync();
        await app.SelectAsync(0, 0x1010);
        await GcmAsync(app, encrypt: false);

        // 5. 認証に失敗した旨のエラー。データは改変した状態のまま。
        await app.WaitUntilAsync(() => Task.FromResult(app.NotificationsText().Contains("Authentication failed", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10), "the authentication error");
        Assert.Equal((byte)(original ^ 0x01), (await app.BytesAsync(0x10, 1))[0]);
        Assert.Equal(MiB + 16, (await app.DocumentAsync())["length"]!.GetValue<long>());

        // 6〜7. 改変を元に戻してから復号すると、元の内容と長さに戻る。
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        await app.SelectAsync(0, 0x1010);
        await GcmAsync(app, encrypt: false);
        Assert.Equal(MiB, (await app.DocumentAsync())["length"]!.GetValue<long>());
        byte[] expected = new byte[0x1000];
        TestDataCatalog.Sequence(0, expected);
        Assert.Equal(expected, await app.BytesAsync(0, 0x1000));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-40-04")]
    public Task Keys_and_ivs_are_not_saved() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")], Profile = profile });

        // 1. AES-128 CBC で 0x100 バイトを暗号化する。
        await app.SelectAsync(0, 0x100);
        await OpenCodecDialogAsync(app, "Command_Encrypt", "encrypt");
        await SetAllAsync(app, ("algorithm", "Aes128"), ("mode", "Cbc"), ("padding", "Pkcs7"), ("key", "13 57 9B DF 24 68 AC E0 13 57 9B DF 24 68 AC E0"),
            ("iv", "F0 E1 D2 C3 B4 A5 96 87 78 69 5A 4B 3C 2D 1E 0F"), ("newTab", "false"));
        await EditCommandTests.PressAsync(app);
        await WaitIdleOperationsAsync(app, TimeSpan.FromSeconds(30));

        // 2. 開き直すと鍵と IV は空、アルゴリズム・モード・パディングは前回のまま。
        JsonObject state = await OpenCodecDialogAsync(app, "Command_Encrypt", "encrypt");
        Assert.True(state["keyEmpty"]!.GetValue<bool>());
        Assert.True(state["ivEmpty"]!.GetValue<bool>());
        Assert.True(state["keyIsPassword"]!.GetValue<bool>());
        Assert.Equal("Aes128", state["algorithm"]!.GetValue<string>());
        Assert.Equal("Cbc", state["mode"]!.GetValue<string>());
        Assert.Equal("Pkcs7", state["padding"]!.GetValue<string>());

        // 3. パスフレーズを使う暗号化も 1 回行う。
        await SetAllAsync(app, ("passphrase", "Secret-Pass-123"), ("salt", "00 11 22 33"), ("iterations", "1000"),
            ("iv", "F0 E1 D2 C3 B4 A5 96 87 78 69 5A 4B 3C 2D 1E 0F"), ("newTab", "false"));
        await EditCommandTests.PressAsync(app);
        await WaitIdleOperationsAsync(app, TimeSpan.FromSeconds(30));

        // 4. 終了する (変更は保存しない)。
        await app.CommandAsync("Command_Exit");
        await PressWhenReadyAsync(app, "SecondaryButton");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(60));

        // 5. 設定フォルダのどのファイルにも鍵・IV・パスフレーズがない。
        byte[] key = Convert.FromHexString("13579BDF2468ACE013579BDF2468ACE0");
        byte[] iv = Convert.FromHexString("F0E1D2C3B4A5968778695A4B3C2D1E0F");
        string[] texts =
        [
            "13579BDF2468ACE0", "13 57 9B DF 24 68 AC E0", "13-57-9B-DF", "F0E1D2C3B4A59687", "F0 E1 D2 C3 B4 A5 96 87", "Secret-Pass-123",
        ];
        foreach (string file in Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories))
        {
            byte[] content;
            try
            {
                content = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                continue;
            }

            string latin = Encoding.Latin1.GetString(content);
            string utf16 = content.Length % 2 == 0 ? Encoding.Unicode.GetString(content) : string.Empty;
            foreach (string text in texts)
            {
                Assert.DoesNotContain(text, latin, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(text, utf16, StringComparison.OrdinalIgnoreCase);
            }

            Assert.True(content.AsSpan().IndexOf(key) < 0, file);
            Assert.True(content.AsSpan().IndexOf(iv) < 0, file);
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-40-05")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Ten_gigabytes_are_encrypted_with_aes_cbc() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SPARSE-100G")] });
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        byte[] iv = Enumerable.Range(0, 16).Select(i => (byte)(255 - i)).ToArray();
        async Task StartAsync()
        {
            await app.SelectAsync(0, 10L * 1024 * MiB);
            await OpenCodecDialogAsync(app, "Command_Encrypt", "encrypt");
            await SetAllAsync(app, ("algorithm", "Aes256"), ("mode", "Cbc"), ("padding", "None"), ("key", Convert.ToHexString(key)), ("iv", Convert.ToHexString(iv)),
                ("newTab", "false"));
            await EditCommandTests.PressAsync(app, waitForOperations: false);
        }

        // 1〜3. 進捗率が 10% 以上になったらキャンセルする。目印は変わらず、編集履歴は増えない。
        await StartAsync();
        await app.WaitUntilAsync(async () => (await app.StateAsync())["operations"]!.AsArray().Select(o => o!.AsObject())
            .Any(o => o["state"]!.GetValue<string>() == "Running" && o["total"]?.GetValue<long>() is long total && total > 0
                && o["processed"]!.GetValue<long>() >= total / 10), TimeSpan.FromMinutes(20), "10 % progress");
        await app.UiaInvokeAsync("Status_Operations");
        await app.UiaInvokeAsync("Operations_Cancel");
        await WaitIdleOperationsAsync(app, TimeSpan.FromMinutes(2));
        await app.IdleAsync();
        Assert.Equal(Encoding.ASCII.GetBytes("@0000000000000000"), await app.BytesAsync(0, 17));
        Assert.Equal(1, (await app.DocumentAsync())["historyCount"]!.GetValue<int>());

        // 4〜5. 最後まで実行すると長さは変わらず、先頭 1 MiB は .NET の Aes の結果と一致する。
        await StartAsync();
        await WaitIdleOperationsAsync(app, TimeSpan.FromMinutes(60));
        Assert.Equal(100L * 1024 * 1024 * 1024, (await app.DocumentAsync())["length"]!.GetValue<long>());
        byte[] plain = new byte[MiB];
        using (FileStream f = File.OpenRead(ctx.TestData("TD-SPARSE-100G")))
        {
            f.ReadExactly(plain);
        }

        using var aes = Aes.Create();
        aes.Key = key;
        byte[] expected = aes.EncryptCbc(plain, iv, PaddingMode.None);
        Assert.Equal(expected, await app.BytesAsync(0, MiB));
    }, TimeSpan.FromMinutes(120));

    // ---- EDIT-41 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-41-03")]
    public Task Select_compressed_data_after_a_zip_local_header() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-EDIT-ZIP-1M");
        long compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(path).AsSpan(0x12));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.GoToAsync(0x28);
        await app.CommandAsync("Command_SelectCompressed");
        await app.WaitUntilAsync(async () => (await app.SendAsync("selectCompressed"))["format"] is not null, UiTest.Scaled(TimeSpan.FromSeconds(30)), "the range");
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(0x28, doc["selectionStart"]!.GetValue<long>());
        Assert.Equal(compressedSize, doc["selectionLength"]!.GetValue<long>());
        Assert.Equal(MiB, (await app.SendAsync("selectCompressed"))["output"]!.GetValue<long>());
        Assert.False(doc["modified"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-41-04")]
    public Task Compression_bomb_asks_before_continuing() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-EDIT-ZBOMB")] });
        long length = (await app.DocumentAsync())["length"]!.GetValue<long>();
        await app.KeyAsync("A", ctrl: true);
        await OpenCodecDialogAsync(app, "Command_Decompress", "decompress");
        await SetAsync(app, "newTab", "false");
        await EditCommandTests.PressAsync(app, waitForOperations: false);

        // 2. 確認ダイアログ (4 GiB を超えた旨、「続ける」「中止する」)。
        await app.WaitForAsync("DecompressLimitDialog", UiTest.Scaled(TimeSpan.FromMinutes(5)));
        await app.WaitUntilAsync(async () => (await app.SendAsync("openDialogs"))["ready"]!.GetValue<int>() > 0, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the buttons");
        string text = AppSession.AllText(await app.WaitForAsync("DecompressLimitDialog"));
        Assert.Contains("exceeded 4 GiB", text, StringComparison.Ordinal);
        Assert.Contains("Continue", text, StringComparison.Ordinal);
        Assert.Contains("Abort", text, StringComparison.Ordinal);

        // 3〜4. 中止すると、長さ・先頭 2 バイト・編集履歴は変わらず、展開用の一時ファイルは消える。
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
        await WaitIdleOperationsAsync(app, TimeSpan.FromSeconds(60));
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(length, doc["length"]!.GetValue<long>());
        Assert.Equal(new byte[] { 0x78, 0xDA }, await app.BytesAsync(0, 2));
        Assert.Equal(1, doc["historyCount"]!.GetValue<int>());
        Assert.DoesNotContain((await app.SendAsync("documentTempFiles"))["files"]!.AsArray(),
            f => f!.GetValue<string>().StartsWith("transform-", StringComparison.Ordinal));
    }, TimeSpan.FromMinutes(15));

    // ---- EDIT-46 ----

    private static (long Start, long Length) Idat(byte[] png)
    {
        long at = 8;
        while (at + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan((int)at));
            if (Encoding.ASCII.GetString(png, (int)at + 4, 4) == "IDAT")
            {
                return (at + 8, length);
            }

            at += 12 + length;
        }

        throw new InvalidDataException("IDAT");
    }

    private static async Task<AppSession> OpenIdatDerivedAsync(UiTestContext ctx, string path)
    {
        (long start, long length) = Idat(File.ReadAllBytes(path));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });
        await app.SelectAsync(start, length);
        JsonObject state = await OpenCodecDialogAsync(app, "Command_Decompress", "decompress");
        Assert.True(state["newTab"]!.GetValue<bool>());
        Assert.Equal("Auto", state["format"]!.GetValue<string>());
        await EditCommandTests.PressAsync(app);
        await WaitIdleOperationsAsync(app, TimeSpan.FromSeconds(30));
        await app.WaitUntilAsync(async () => (await app.SendAsync("derived"))["derived"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the derived tab");
        return app;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-46-01")]
    public Task Idat_is_edited_in_a_derived_document_and_written_back() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-PNG");
        byte[] png = File.ReadAllBytes(path);
        (long start, long length) = Idat(png);
        AppSession app = await OpenIdatDerivedAsync(ctx, path);

        // 3. 見出しは「<ファイル名> [0x…-0x… decompressed]」、帯に元のドキュメント・範囲・変換 (zlib の展開)。
        JsonObject derived = await app.SendAsync("derived");
        string name = Path.GetFileName(path);
        Assert.Equal($"{name} [0x{start:X}-0x{start + length - 1:X} decompressed]", derived["title"]!.GetValue<string>());
        string band = derived["band"]!.GetValue<string>();
        Assert.Contains(name, band, StringComparison.Ordinal);
        Assert.Contains("zlib decompression", band, StringComparison.Ordinal);
        Assert.True(derived["canWriteBack"]!.GetValue<bool>());
        await app.WaitForAsync("Derived_WriteBack");

        // 4〜5. 0x10 を FF に書き換え、「元に書き戻す」を押す。
        await app.GoToAsync(0x10);
        await app.TypeAsync("FF");
        await app.IdleAsync();
        long derivedLength = (await app.DocumentAsync())["length"]!.GetValue<long>();
        byte[] edited = await app.BytesAsync(0, derivedLength);
        await app.UiaInvokeAsync("Derived_WriteBack");
        await WaitIdleOperationsAsync(app, TimeSpan.FromSeconds(30));
        await app.WaitUntilAsync(async () => (await app.SendAsync("derived"))["length"]!.GetValue<long>() != length
            || app.NotificationsText().Contains("Written back", StringComparison.Ordinal), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the write-back");
        long newLength = (await app.SendAsync("derived"))["length"]!.GetValue<long>();

        // 6. 元のタブの S からの zlib データを展開すると、派生ドキュメントの内容と一致する。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        byte[] written = await app.BytesAsync(start, newLength);
        using (var z = new ZLibStream(new MemoryStream(written), CompressionMode.Decompress))
        {
            var ms = new MemoryStream();
            z.CopyTo(ms);
            Assert.Equal(edited, ms.ToArray());
        }

        // 7〜8. Ctrl+Z 1 回で TD-PNG と一致する。
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        JsonObject doc = await app.DocumentAsync();
        Assert.Equal(png.Length, doc["length"]!.GetValue<long>());
        Assert.Equal(png, await app.BytesAsync(0, png.Length));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-46-02")]
    public Task Closing_the_source_disables_write_back() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenIdatDerivedAsync(ctx, ctx.CopyTestData("TD-PNG"));

        // 1. 元の TD-PNG のタブを閉じる。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        await app.KeyAsync("W", ctrl: true);
        await app.WaitForTabsAsync(1);
        await app.IdleAsync();

        // 2. ボタンは無効、書き戻せなくなった旨の InfoBar。
        JsonObject derived = await app.SendAsync("derived");
        Assert.True(derived["derived"]!.GetValue<bool>());
        Assert.False(derived["canWriteBack"]!.GetValue<bool>());
        Assert.False((await app.WaitForAsync("Derived_WriteBack")).IsEnabled);
        await app.WaitUntilAsync(() => Task.FromResult(app.NotificationsText().Contains("source document was closed", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10), "the InfoBar");

        // 3. コマンドパレットの「元に書き戻す」も無効 (理由が出る)。
        JsonObject palette = await app.SendAsync("palette", new JsonObject { ["text"] = ">Write back to source" });
        JsonObject entry = palette["entries"]!.AsArray().Select(e => e!.AsObject()).First(e => e["key"]!.GetValue<string>() == "command:data.derived.writeBack");
        Assert.False(string.IsNullOrEmpty(entry["reason"]?.GetValue<string>()));
        await app.SendAsync("paletteClose");
    });

    // ---- FIND-33 ----

    private static string RulesDir(UiTestContext ctx) => YaraRuleFiles.Write(Path.Combine(ctx.Root, "rules"));

    private static async Task<JsonObject> ScanAsync(AppSession app, params string[] files)
    {
        await app.CommandAsync("Command_Yara");
        await app.WaitForAsync("Yara_Scan");
        await app.SendAsync("yaraSet", new JsonObject { ["files"] = new JsonArray([.. files.Select(f => (JsonNode?)f)]) });
        await app.SendAsync("yaraScan");
        JsonObject state = null!;
        await app.WaitUntilAsync(async () =>
        {
            state = await app.SendAsync("yaraPanel", new JsonObject { ["count"] = 100 });
            return !state["running"]!.GetValue<bool>() && (state["scanned"]!.GetValue<bool>() || state["errors"]!.AsArray().Count > 0);
        }, UiTest.Scaled(TimeSpan.FromSeconds(60)), "the scan");
        await app.IdleAsync();
        return await app.SendAsync("yaraPanel", new JsonObject { ["count"] = 100 });
    }

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-33-02")]
    public Task Syntax_error_shows_the_line_and_does_not_scan() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-PE-X64")] });
        JsonObject state = await ScanAsync(app, Path.Combine(RulesDir(ctx), "bad.yar"));
        JsonObject error = Assert.Single(state["errors"]!.AsArray())!.AsObject();
        Assert.Equal("bad.yar", error["file"]!.GetValue<string>());
        Assert.Equal(3, error["line"]!.GetValue<int>());
        Assert.Contains("syntax error", error["message"]!.GetValue<string>(), StringComparison.Ordinal);

        // スキャンは始まらない (処理センターに項目がなく、結果一覧は空)。
        Assert.False(state["scanned"]!.GetValue<bool>());
        Assert.Equal(0, state["results"]!["count"]!.GetValue<long>());
        Assert.Empty((await app.StateAsync())["operations"]!.AsArray());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-33-04")]
    public Task Matches_are_highlighted_and_the_tooltip_has_the_rule() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-PE-X64")] });
        JsonObject state = await ScanAsync(app, Path.Combine(RulesDir(ctx), "mz.yar"));
        JsonObject rule = Assert.Single(state["rules"]!.AsArray())!.AsObject();
        Assert.Equal("mz", rule["rule"]!.GetValue<string>());

        // TC-FIND-33-03 の手順 3 (画面の部分): ブロックをまたぐ一致の制限の説明がスキャンの画面に常に出ている。
        Assert.Contains("blocks of 64 MiB", state["limitation"]!.GetValue<string>(), StringComparison.Ordinal);

        // 1. オフセット 0〜1 に注釈 (層 8) の強調表示があり、2 にはない。
        await app.GoToAsync(0);
        await app.IdleAsync();
        JsonArray segments = (await app.SendAsync("highlights"))["segments"]!.AsArray();
        var annotations = segments.Where(s => s!["layer"]!.GetValue<int>() == 8 && s["column"]!.GetValue<string>() == "hex").Select(s => s!.AsObject()).ToList();
        Assert.Contains(annotations, s => s["first"]!.GetValue<long>() <= 0 && s["last"]!.GetValue<long>() >= 1);
        Assert.DoesNotContain(annotations, s => s["first"]!.GetValue<long>() <= 2 && s["last"]!.GetValue<long>() >= 2);

        // 2〜3. オフセット 0 のツールチップにルール名と文字列の識別子。
        JsonObject render = await app.RenderAsync();
        await ViewOps.PointerAsync(app, "move", ViewOps.CellPoint(render, 0));
        await app.WaitUntilAsync(async () => (await app.RenderAsync())["toolTip"]!["open"]!.GetValue<bool>(), TimeSpan.FromSeconds(5), "the tooltip");
        string text = string.Join("\n", (await app.RenderAsync())["toolTip"]!["rich"]!.AsArray().Select(r => r!["text"]!.GetValue<string>()));
        Assert.Contains("mz", text, StringComparison.Ordinal);
        Assert.Contains("$a", text, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FIND-33-05")]
    public Task Unsaved_edits_are_scanned() => UiTestContext.RunAsync(async ctx =>
    {
        string path = ctx.CopyTestData("TD-ZERO-1M");
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path] });

        // 1. テキスト列の 0x1000 に ADDED-BY-EDIT を入力する (保存しない)。
        await app.GoToAsync(0x1000);
        await app.KeyAsync("Tab");
        await app.TypeAsync("ADDED-BY-EDIT");
        await app.IdleAsync();

        // 2〜3. added.yar でスキャンすると、0x1000 に長さ 13 の $s。
        JsonObject state = await ScanAsync(app, Path.Combine(RulesDir(ctx), "added.yar"));
        JsonObject rule = Assert.Single(state["rules"]!.AsArray())!.AsObject();
        Assert.Equal("added", rule["rule"]!.GetValue<string>());
        JsonObject s = Assert.Single(rule["strings"]!.AsArray())!.AsObject();
        Assert.Equal("$s", s["id"]!.GetValue<string>());
        Assert.Equal(0x1000, s["offset"]!.GetValue<long>());
        Assert.Equal(13, s["length"]!.GetValue<int>());
        JsonObject row = Assert.Single(state["results"]!["rows"]!.AsArray())!.AsObject();
        Assert.Equal(0x1000, row["offset"]!.GetValue<long>());

        // 4. ディスク上のファイルの 0x1000 は 00 のまま。
        using FileStream f = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        f.Position = 0x1000;
        Assert.Equal(0, f.ReadByte());
    });

    // ---- INSP-32 (YARA の一致の注釈) ----

    /// <summary>層 8 (注釈) の Hex 列の強調のうち、<paramref name="at"/> を含むもの。</summary>
    private static List<JsonObject> AnnotationsAt(JsonObject highlights, long at) =>
        [.. highlights["segments"]!.AsArray().Select(s => s!.AsObject())
            .Where(s => s["layer"]!.GetValue<int>() == 8 && s["column"]!.GetValue<string>() == "hex" && s["first"]!.GetValue<long>() <= at && s["last"]!.GetValue<long>() >= at)];

    private static async Task<AppSession> ScanZipAsync(UiTestContext ctx)
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ZIP")] });
        JsonObject state = await ScanAsync(app, YaraRuleFiles.WriteZipLocalHeader(Path.Combine(ctx.Root, "rules")));
        Assert.Contains(state["rules"]!.AsArray(), r => r!["rule"]!.GetValue<string>() == "zip_local_header");
        await app.GoToAsync(0);
        await app.IdleAsync();
        return app;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-32-01")]
    public Task Yara_matches_are_bordered_annotations_with_the_rule_in_the_tooltip() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ScanZipAsync(ctx);

        // 2. 0〜3 に注釈の枠線があり、背景は塗られていない (YARA の既定は枠線)。
        JsonObject highlights = await app.SendAsync("highlights");
        for (long at = 0; at < 4; at++)
        {
            List<JsonObject> segments = AnnotationsAt(highlights, at);
            Assert.True(segments.Count > 0, highlights.ToJsonString());
            Assert.All(segments, s =>
            {
                Assert.Null(s["background"]);
                Assert.NotNull(s["border"]);
            });
        }

        // 3. オフセット 1 のツールチップに出どころ「YARA」とルール名。
        JsonObject render = await app.RenderAsync();
        await ViewOps.PointerAsync(app, "move", ViewOps.CellPoint(render, 1));
        await app.WaitUntilAsync(async () => (await app.RenderAsync())["toolTip"]!["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(5)), "the tooltip");
        string text = string.Join("\n", (await app.RenderAsync())["toolTip"]!["rich"]!.AsArray().Select(r => r!["text"]!.GetValue<string>()));
        Assert.Contains("YARA", text, StringComparison.Ordinal);
        Assert.Contains("zip_local_header", text, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-32-03")]
    public Task Hiding_the_yara_origin_keeps_bookmarks() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ScanZipAsync(ctx);
        await app.GoToAsync(0x10);
        await app.KeyAsync("F2", ctrl: true);
        await app.GoToAsync(0);
        await app.IdleAsync();
        Assert.NotEmpty(AnnotationsAt(await app.SendAsync("highlights"), 0));

        // 1. 表示 > 注釈 で「YARA」のチェックを外す。
        await app.CommandAsync("Command_AnnotationsYara");
        await app.IdleAsync();

        // 2. 0〜3 の YARA の注釈が消え、0x10 のブックマーク (層 7) は表示されたまま。
        JsonObject highlights = null!;
        await app.WaitUntilAsync(async () => AnnotationsAt(highlights = await app.SendAsync("highlights"), 0).Count == 0,
            UiTest.Scaled(TimeSpan.FromSeconds(5)), "the YARA annotations to be hidden");
        for (long at = 1; at < 4; at++)
        {
            Assert.Empty(AnnotationsAt(highlights, at));
        }

        Assert.Contains(highlights["segments"]!.AsArray(), s => s!["layer"]!.GetValue<int>() == 7 && s["first"]!.GetValue<long>() <= 0x10 && s["last"]!.GetValue<long>() >= 0x10);
    });
}
