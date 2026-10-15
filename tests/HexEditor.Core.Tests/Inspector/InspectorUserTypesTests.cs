using HexEditor.Core.Inspector;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Inspector;

/// <summary>ユーザー定義型 (INSP-22) の定義ファイルの読み込み・監視と解釈。インスペクタの行は UI テスト (TC-INSP-22-01、TC-INSP-22-03)。</summary>
public sealed class InspectorUserTypesTests : IDisposable
{
    private readonly string _settings = Path.Combine(Path.GetTempPath(), "HexEditorTests", "inspector-types", Guid.NewGuid().ToString("N"));

    public InspectorUserTypesTests() => Directory.CreateDirectory(Path.Combine(_settings, InspectorUserTypes.FolderName));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_settings, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_settings, InspectorUserTypes.FolderName, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Template_struct_is_evaluated_with_its_fields()
    {
        Write("header6.hxt", TestDataCatalog.InspectorTemplateType);
        using var types = new InspectorUserTypes(_settings);
        types.Load();
        InspectorUserType type = Assert.Single(types.Types);
        Assert.Equal("Header6", type.DisplayName);

        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x00, 0x00, 0xFF];
        InspectorUserValue value = await types.EvaluateAsync(type, data, Endianness.Little);
        Assert.Null(value.Error);
        Assert.False(value.TimedOut);
        Assert.Equal(6, value.Size);
        Assert.Equal(["id", "size"], value.Fields.Select(f => f.Name));
        Assert.Equal("513", value.Fields[0].Value);
        Assert.Equal("1027", value.Fields[1].Value);

        // インスペクタの表示形式 (桁区切り・16 進)。
        var grouped = new InspectorOptions { Culture = System.Globalization.CultureInfo.GetCultureInfo("en-US") };
        Assert.Equal("1,027", (await types.EvaluateAsync(type, data, Endianness.Little, grouped)).Fields[1].Value);
        Assert.Equal("0x00000403", (await types.EvaluateAsync(type, data, Endianness.Little, grouped with { IntegerBase = IntegerBase.Hexadecimal })).Fields[1].Value);

        // データが足りなければその行だけのエラー。
        InspectorUserValue shortData = await types.EvaluateAsync(type, [0x01], Endianness.Little);
        Assert.NotNull(shortData.Error);
    }

    [Fact]
    public async Task Infinite_loop_times_out_without_blocking()
    {
        Write("loop.hxt", "struct Loop { while (true) {} } [[inspector(\"Loop\")]];\n");
        using var types = new InspectorUserTypes(_settings);
        types.Load();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        InspectorUserValue value = await types.EvaluateAsync(Assert.Single(types.Types), new byte[16], Endianness.Little);
        Assert.True(value.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Syntax_errors_report_file_and_line()
    {
        Write("bad.hxt", "struct Bad {\n    le u16 id\n} [[inspector(\"Bad\")]];\n");
        using var types = new InspectorUserTypes(_settings);
        types.Load();
        Assert.Empty(types.Types);
        InspectorUserTypeError error = Assert.Single(types.Errors.Take(1));
        Assert.Equal("bad.hxt", error.File);
        Assert.True(error.Line >= 2, $"line {error.Line}");
    }

    [Fact]
    public async Task Saving_the_file_again_reloads_the_types()
    {
        string path = Write("header6.hxt", TestDataCatalog.InspectorTemplateType);
        using var types = new InspectorUserTypes(_settings);
        types.Load();
        var reloaded = new TaskCompletionSource();
        types.Reloaded += (_, _) =>
        {
            if (types.Types.Any(t => t.DisplayName == "Header6B"))
            {
                reloaded.TrySetResult();
            }
        };
        types.Watch();
        File.WriteAllText(path, TestDataCatalog.InspectorTemplateType.Replace("Header6", "Header6B", StringComparison.Ordinal));
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Header6B", Assert.Single(types.Types).DisplayName);
    }
}
