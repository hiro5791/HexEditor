using System.Text.Json.Nodes;
using HexEditor.App.Templates;
using HexEditor.Core.Inspector;
using HexEditor.Core.Plugins;

namespace HexEditor.App.Services;

/// <summary>
/// データインスペクタのユーザー定義型 (05 の INSP-22) のアプリ全体の一覧 (1 つだけ)。型の出どころは、設定フォルダの <c>inspector/</c>、設定
/// 「インスペクタ用テンプレート」(07 の TPL-29)、有効なプラグインのインスペクタの型 (08 の AUTO-31)。セーフモードではプラグインを読み込まないので
/// プラグインの型も出ない。読み込みと評価は <see cref="InspectorUserTypes"/> の 1 つの経路で行う。
/// </summary>
public static class InspectorUserTypeService
{
    private static InspectorUserTypes? s_instance;

    public static InspectorUserTypes Instance
    {
        get
        {
            if (s_instance is null)
            {
                var types = new InspectorUserTypes(Hosting.Program.Environment.Locations.Settings)
                {
                    PluginTypes = () => PluginIntegration.InspectorTypes(PluginManager.Current),
                };
                types.SetRegisteredTemplates(RegisteredPaths());
                if (types.RegisteredTemplates.Count == 0)
                {
                    types.Load();
                }

                types.Watch();
                if (App.Settings is { } settings)
                {
                    settings.Changed += keys =>
                    {
                        if (keys.Contains(TemplateService.InspectorKey))
                        {
                            types.SetRegisteredTemplates(RegisteredPaths());
                        }
                    };
                }

                // テンプレートを保存した (テンプレートエディタ)・一覧が変わった: 登録したテンプレートを読み込み直す。
                TemplateService.CatalogChanged += (_, _) => types.ReloadIfChanged();
                s_instance = types;
            }

            return s_instance;
        }
    }

    /// <summary>設定「インスペクタ用テンプレート」のパス。</summary>
    public static IReadOnlyList<string> RegisteredPaths()
    {
        List<string> paths = [];
        if (App.Settings?.GetNode(TemplateService.InspectorKey) is JsonArray a)
        {
            foreach (JsonNode? n in a)
            {
                if (n is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0)
                {
                    paths.Add(s);
                }
            }
        }

        return paths;
    }

    /// <summary>登録したテンプレートの型 (設定画面の一覧と登録時の警告。TPL-29 の仕様 4)。</summary>
    public static IEnumerable<InspectorUserType> RegisteredTypes(string path)
    {
        Instance.SetRegisteredTemplates(RegisteredPaths());
        return Instance.Types.Where(t => t.Source == InspectorUserTypeSource.Registered && string.Equals(t.File, path, StringComparison.OrdinalIgnoreCase));
    }
}
