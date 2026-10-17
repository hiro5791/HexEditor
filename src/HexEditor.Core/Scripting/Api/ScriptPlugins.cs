using System.Runtime.CompilerServices;
using HexEditor.Core.Plugins;
using HexEditor.Sdk;
using HexEditor.Sdk.Plugins;

namespace HexEditor.Core.Scripting.Api;

/// <summary><c>app.plugins</c> (AUTO-31 の <c>IScriptApiProvider</c>)。プラグインの管理 (<see cref="PluginManager.Current"/>) を読むだけ。</summary>
internal sealed class ScriptPlugins : IPlugins
{
    public IReadOnlyList<PluginInfo> List() => PluginManager.Current is { } m
        ? [.. m.Plugins.Select(p => new PluginInfo(p.Id, p.NameFor(m.Options.UiLanguage), p.Version, StateName(p.State), p.Reason))]
        : [];

    public IPluginHandle? Get(string id) => PluginManager.Current is { } m && m.Find(id) is { } p ? new ScriptPluginHandle(m, p) : null;

    internal static string StateName(PluginState state) => state switch
    {
        PluginState.Enabled => "enabled",
        PluginState.Disabled => "disabled",
        PluginState.Failed => "failed",
        PluginState.Suspended => "suspended",
        _ => "notLoaded",
    };

    private sealed class ScriptPluginHandle(PluginManager manager, PluginRecord plugin) : IPluginHandle
    {
        public string Id => plugin.Id;

        public string Name => plugin.NameFor(manager.Options.UiLanguage);

        public string Version => plugin.Version;

        public bool Enabled => plugin.State == PluginState.Enabled;

        public object? Api
        {
            get
            {
                (RegisteredExtension Registration, IScriptApiProvider Provider) entry = manager.Registry.Usable<IScriptApiProvider>()
                    .FirstOrDefault(x => x.Registration.Plugin == plugin);
                if (entry.Registration is null)
                {
                    return null;
                }

                object? api = PluginApiObjects.GetOrCreate(entry.Registration, entry.Provider);
                return api;
            }
        }
    }
}

/// <summary>
/// プラグインがスクリプトに提供するオブジェクト。JavaScript の実行環境は、ここで作ったオブジェクトだけを公開のメンバーを持つ生きたオブジェクトとして包む
/// (それ以外の .NET のオブジェクトは文字列にする。AUTO-02 のサンドボックス)。プラグインごとに 1 つ作って使い回す。
/// </summary>
internal static class PluginApiObjects
{
    private static readonly ConditionalWeakTable<RegisteredExtension, object> Created = new();
    private static readonly ConditionalWeakTable<object, object> Known = new();

    public static object? GetOrCreate(RegisteredExtension registration, IScriptApiProvider provider)
    {
        if (Created.TryGetValue(registration, out object? existing))
        {
            return existing;
        }

        object? api = registration.Guard.Invoke("CreateApi", provider.CreateApi, null);
        if (api is null)
        {
            return null;
        }

        Created.AddOrUpdate(registration, api);
        Known.AddOrUpdate(api, registration);
        return api;
    }

    public static bool IsApi(object value) => Known.TryGetValue(value, out _);
}
