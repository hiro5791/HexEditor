using HexEditor.Sdk.Plugins;

namespace HexEditorPlugin1;

/// <summary>The plugin entry point. Registers the extensions in <see cref="Initialize"/>.</summary>
public sealed class Plugin : IPlugin
{
    /// <inheritdoc />
    public void Initialize(IPluginContext context) => context.Register(new HelloCommand(context.Id));
}

/// <summary>A command shown in the command palette as "HexEditorPlugin1: Hello".</summary>
public sealed class HelloCommand(string pluginId) : ICommand
{
    /// <inheritdoc />
    public string Id { get; } = pluginId + ".hello";

    /// <inheritdoc />
    public string DisplayName => "HexEditorPlugin1: Hello";

    /// <inheritdoc />
    public void Execute(ICommandContext context)
    {
        string length = context.Document is { } doc ? doc.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) : "no";
        context.Log.Info($"Hello from HexEditorPlugin1 ({length} bytes)");
    }
}
