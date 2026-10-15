using HexEditor.Sdk.Plugins;

namespace Plugin.Sdk13;

/// <summary>コマンド「sdk13.ping」だけを登録する。読み込めないことを確かめるためのもの。</summary>
public sealed class Sdk13Plugin : IPlugin
{
    public void Initialize(IPluginContext context) => context.Register(new Ping(context));

    private sealed class Ping(IPluginContext context) : ICommand
    {
        public string Id => context.Id + ".ping";

        public string DisplayName => "sdk13: ping";

        public void Execute(ICommandContext command) => command.Log.Info("pong from sdk13");
    }
}
