using HexEditor.Sdk.Plugins;

namespace Plugin.Dep;

/// <summary>コマンド「&lt;id&gt;: version」で、依存している TestLib のバージョンを出力する (TC-AUTO-30-01)。</summary>
public sealed class DepPlugin : IPlugin
{
    public void Initialize(IPluginContext context) => context.Register(new VersionCommand(context.Id));

    private sealed class VersionCommand(string pluginId) : ICommand
    {
        public string Id => pluginId + ".version";

        public string DisplayName => pluginId + ": version";

        public void Execute(ICommandContext context) => context.Log.Info(TestLib.TestLibInfo.Version);
    }
}
