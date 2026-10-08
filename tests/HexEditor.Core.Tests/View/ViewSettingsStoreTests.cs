using System.Text.Json.Nodes;
using HexEditor.Core.Files;
using HexEditor.Core.Settings;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.View;

/// <summary>表示設定の決まり方 (VIEW-42 の仕様 2): 組み込み → 全体の既定値 → データソースの種類ごとの既定値 → ドキュメントごと。</summary>
public sealed class ViewSettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-view").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Data_source_defaults_sit_between_global_defaults_and_document_settings()
    {
        using var settings = new SettingsStore(Path.Combine(_dir, "settings"));
        var documents = new DocumentDataStore(Path.Combine(_dir, "documents"));
        var store = new ViewSettingsStore(settings, documents);
        string file = Path.Combine(_dir, "disk.img");
        File.WriteAllBytes(file, new byte[16]);

        // 全体の既定値: 1 行 32 バイト、10 進。
        store.SaveDefaults(ViewSettings.Default with { BytesPerRow = 32, Radix = OffsetRadix.Decimal });

        // データソースの既定値: オフセットは 16 進 (全体の既定値より優先)。ほかの項目は全体の既定値のまま。
        var source = new JsonObject { ["radix"] = "hex" };
        ViewSettings defaults = store.DefaultsFor(source);
        Assert.Equal((32, OffsetRadix.Hex), (defaults.BytesPerRow, defaults.Radix));
        Assert.Equal(OffsetRadix.Decimal, store.DefaultsFor(null).Radix);

        // ドキュメントごとの設定が最も優先。保存するのはデータソースの既定値との差分だけ。
        store.Save(file, defaults with { GroupSize = 4 }, null, source);
        (ViewSettings loaded, _) = store.Load(file, source);
        Assert.Equal((32, OffsetRadix.Hex, 4), (loaded.BytesPerRow, loaded.Radix, loaded.GroupSize));

        // データソースの既定値と同じなら、ドキュメントごとの設定は残さない。
        store.Save(file, defaults, null, source);
        Assert.Null(documents.ReadObject(file, ViewSettingsStore.Kind));
    }
}
