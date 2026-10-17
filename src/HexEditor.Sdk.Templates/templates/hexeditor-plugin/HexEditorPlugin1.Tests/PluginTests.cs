using HexEditor.Sdk.Plugins;
using HexEditor.Sdk.Testing;
using Xunit;

namespace HexEditorPlugin1.Tests;

public sealed class PluginTests
{
    [Fact]
    public void Hello_command_writes_to_the_output()
    {
        var context = new FakePluginContext("hexeditorplugin1");
        new Plugin().Initialize(context);

        context.Execute("hexeditorplugin1.hello", new FakePluginDocument(new byte[16]));

        Assert.Contains("info: Hello from HexEditorPlugin1 (16 bytes)", context.Lines);
        Assert.IsType<HelloCommand>(Assert.Single(context.Extensions));
    }
}
