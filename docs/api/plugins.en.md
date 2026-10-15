# HexEditor plugin SDK

This guide describes how to write HexEditor plugins in C# (specification: 08-automation.md, AUTO-29 to AUTO-35).
The interfaces are documented in the XML comments of the `HexEditor.Sdk` package (IntelliSense shows the English and the
Japanese text). The automation API that scripts use is described in [reference.en.md](reference.en.md).

## Packages

| Package | Contents |
| --- | --- |
| `HexEditor.Sdk` | `IPlugin`, `IPluginContext` and the extension point interfaces (`HexEditor.Sdk.Plugins`), and the automation API types. No WinUI dependency. |
| `HexEditor.Sdk.UI` | `IPanelProvider` and `IPluginSettingsProvider` (WinUI `UIElement`). |
| `HexEditor.Sdk.Templates` | `dotnet new hexeditor-plugin`: a plugin that registers one command, its manifest, a test project and a launch profile. |
| `HexEditor.Sdk.Testing` | `FakePluginContext`, `FakePluginDocument` and `FakeCommandContext` for unit tests. |

## Quick start

```
dotnet new install HexEditor.Sdk.Templates
dotnet new hexeditor-plugin -n MyPlugin
cd MyPlugin
dotnet build
dotnet test
```

Press F5 in Visual Studio: the launch profile starts HexEditor with `--plugin-dev <build output>` so the plugin is loaded
directly from the build output (shown as "Development" in the plugin manager; you are asked to trust the folder once).
Set the MSBuild property `HexEditorExe` when HexEditor is not installed in `%LocalAppData%\HexEditor\current\`.

## Manifest (`plugin.json`)

```json
{
  "id": "example.crc-tools",
  "name": "CRC Tools",
  "name.ja": "CRC ツール",
  "version": "1.2.0",
  "publisher": "Example",
  "sdk": "1.0",
  "minAppVersion": "1.0.0",
  "entry": "Example.CrcTools.dll",
  "permissions": ["file-read"],
  "description": "Additional CRC algorithms."
}
```

- `id`: lowercase letters, digits and `-`, in parts joined by `.` (at most 100 characters).
- `sdk`: the SDK version the plugin is built with (`major.minor`). HexEditor loads plugins with the same major version and the
  same or a lower minor version. The version of the referenced `HexEditor.Sdk` assembly is checked too.
- `entry`: the file name of the assembly directly in the plugin folder. Exactly one public class must implement `IPlugin`.
- `permissions`: shown in the confirmation. Plugins are full-trust code: they can do anything you can.

## Packaging and installation

A `.hexplugin` file is the plugin folder in a zip file (the manifest at the root, or in one top-level folder). Add it in
Tools > Plugins > Manage Plugins (Add... or drag and drop). HexEditor validates the package (paths, sizes, manifest, signatures),
asks for trust and extracts it to `<settings folder>\plugins\<id>\<version>\`. A newer version of an installed plugin is an update.
Enabling, disabling, removing and updating take effect after a restart (plugins cannot be unloaded).

Do not include `HexEditor.Sdk.dll` or `HexEditor.Sdk.UI.dll`: the application's copy is always used. Other dependencies are loaded
from the plugin folder in a separate `AssemblyLoadContext`, so two plugins can use different versions of the same library.

## Extension points

Register extensions in `Initialize` with `context.Register(...)`. Extension IDs must start with the plugin ID
(`<plugin ID>.<name>`); a duplicate ID is disabled with a warning.

| Interface | Shown in | Notes |
| --- | --- | --- |
| `IInspectorType` | Data inspector (Plugins group), `inspector.types` | Called on the UI thread: finish within 10 ms. `Encode` makes the row editable. |
| `IHashAlgorithm` | Hash panel | Receives the data as chunks (`IHashComputation.Append`). |
| `IImportFormat` | Import dialog, format detection | Chunked input, output through `IByteSink`. |
| `IExportFormat` | Export dialog, Copy As (text formats) | Chunked input. |
| `IByteSourceProvider` | File > Open Plugin Data Source | Declare read-only and resizable. Reads are called from worker threads. |
| `IDataTransform` | Data > Plugin Operations | Chunked, in place; the length does not change. |
| `ICommand` | Command palette, shortcuts | `CanExecute` runs on the UI thread (10 ms). |
| `IMenuContribution` | Tools > Plugins, editor context menu > Plugins | Refers to a command of the same plugin. |
| `IPanelProvider` (SDK.UI) | View > Panels | Register only when `context.HasUi`. |
| `IPluginSettingsProvider` (SDK.UI) | Plugin manager > Plugin settings | |
| `ITemplateFunction` | Template language (`extern fn`) | |
| `IFileTypeDetector` | File type detection, automatic templates | Receives the first 64 KB. |
| `IColorRuleProvider` | Coloring rules | Rules are data; HexEditor evaluates them. |
| `IScriptApiProvider` | `app.plugins.get("id").api` | Public members of the object are visible to scripts. |
| `ITransformView`, `IDisassembler` | (registered; shown when the transform view and the disassembler arrive in phase 4) | |

Rules for all extension points:

- Every call is guarded. Exceptions are written to the output panel ("Plugin: name"). Ten exceptions within a minute stop the
  plugin until the next start, with a notice that can disable it.
- A UI thread call that takes more than 100 ms is warned; a second slow call in a row stops the plugin.
- Never keep the whole data in memory: data-proportional work receives `ReadOnlySpan<byte>` chunks, a `CancellationToken` and
  progress through `PluginOperation`.
- Translate your own strings: put `Strings/<language>/Resources.resw` (the same format as the application) in the plugin folder
  and use `context.Strings`.

## Signing

Sign every `.dll` with Authenticode. The plugin manager and the confirmation show the publisher. Unsigned plugins can be
loaded (the confirmation warns); plugins with an invalid signature (modified, expired certificate without a timestamp, revoked)
are not loaded. The setting "Load only signed plugins" and the organization policy `scripts.policy = signedPluginsOnly` refuse
unsigned plugins. Users can choose "Always trust plugins from this publisher".

## Compatibility

The SDK follows semantic versioning. Within a major version, plugins built with an older minor version keep working. A major
version is raised at most once a year, after the removed APIs were marked `[Obsolete]` in the previous major version.
