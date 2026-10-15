using HexEditor.Core.Scripting;
using HexEditor.Sdk.Plugins;

namespace HexEditor.Core.Plugins;

/// <summary>登録した拡張ポイントの状態。</summary>
public enum ExtensionStatus
{
    Active,

    /// <summary>ID が重複したため無効にした (後から読み込んだもの。AUTO-31 の仕様 3)。</summary>
    Duplicate,

    /// <summary>ID の形式が正しくない、アプリ本体の ID と重なる、など。</summary>
    Invalid,
}

/// <summary>登録した拡張ポイント 1 つ。</summary>
public sealed class RegisteredExtension
{
    internal RegisteredExtension(PluginRecord plugin, IExtension extension, string id, string kind, string displayName)
    {
        Plugin = plugin;
        Extension = extension;
        Id = id;
        Kind = kind;
        DisplayName = displayName;
    }

    public PluginRecord Plugin { get; }

    public IExtension Extension { get; }

    public string Id { get; }

    /// <summary>種類 (<see cref="PluginExtensionKinds"/>)。</summary>
    public string Kind { get; }

    public string DisplayName { get; }

    public ExtensionStatus Status { get; internal set; } = ExtensionStatus.Active;

    public string? Problem { get; internal set; }

    /// <summary>使える (有効で、プラグインが止まっていない)。</summary>
    public bool IsUsable => Status == ExtensionStatus.Active && Plugin.State == PluginState.Enabled;

    public PluginGuard Guard => Plugin.Guard;

    public override string ToString() => $"{Kind} {Id} ({Status})";
}

/// <summary>拡張ポイントの種類の名前 (プラグインマネージャの一覧とテストに使う)。</summary>
public static class PluginExtensionKinds
{
    public const string InspectorType = "inspectorType";
    public const string Hash = "hash";
    public const string ImportFormat = "importFormat";
    public const string ExportFormat = "exportFormat";
    public const string ByteSource = "byteSource";
    public const string TransformView = "transformView";
    public const string DataTransform = "dataTransform";
    public const string Command = "command";
    public const string Menu = "menu";
    public const string Panel = "panel";
    public const string Settings = "settings";
    public const string TemplateFunction = "templateFunction";
    public const string FileTypeDetector = "fileTypeDetector";
    public const string Disassembler = "disassembler";
    public const string ColorRules = "colorRules";
    public const string ScriptApi = "scriptApi";

    /// <summary>拡張ポイントの種類 (1 つのオブジェクトが複数のインターフェイスを実装していれば、最初に当たったもの)。</summary>
    public static string Of(IExtension extension) => extension switch
    {
        IInspectorType => InspectorType,
        IHashAlgorithm => Hash,
        IImportFormat => ImportFormat,
        IExportFormat => ExportFormat,
        IByteSourceProvider => ByteSource,
        ITransformView => TransformView,
        IDataTransform => DataTransform,
        ICommand => Command,
        IMenuContribution => Menu,
        ITemplateFunction => TemplateFunction,
        IFileTypeDetector => FileTypeDetector,
        IDisassembler => Disassembler,
        IColorRuleProvider => ColorRules,
        IScriptApiProvider => ScriptApi,
        _ => UiKind(extension),
    };

    /// <summary>SDK.UI の型 (Core は WinUI を参照しないので名前で判定する)。</summary>
    private static string UiKind(IExtension extension)
    {
        foreach (Type i in extension.GetType().GetInterfaces())
        {
            if (i.FullName == "HexEditor.Sdk.UI.IPanelProvider")
            {
                return Panel;
            }

            if (i.FullName == "HexEditor.Sdk.UI.IPluginSettingsProvider")
            {
                return Settings;
            }
        }

        return "unknown";
    }
}

/// <summary>
/// 拡張ポイントの登録簿 (アプリ全体で 1 つ。AUTO-31)。ID の重複は後から読み込んだものを無効にして警告する (仕様 3)。
/// アプリ本体の機能と同じ一覧に出すのは <see cref="Usable{T}"/> のもの。
/// </summary>
public sealed class PluginRegistry
{
    private readonly List<RegisteredExtension> _all = [];
    private readonly Lock _lock = new();

    /// <summary>アプリ本体が使っている ID か (登録を拒否する。例: 本体のハッシュのアルゴリズム ID)。</summary>
    public Func<string, string, bool>? IsBuiltInId { get; set; }

    public event EventHandler? Changed;

    public IReadOnlyList<RegisteredExtension> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _all];
            }
        }
    }

    /// <summary>使える拡張ポイント (登録の順)。</summary>
    public IReadOnlyList<RegisteredExtension> Usable(string kind) => [.. All.Where(e => e.Kind == kind && e.IsUsable)];

    /// <summary>使える拡張ポイントのうち、型 <typeparamref name="T"/> のもの。</summary>
    public IReadOnlyList<(RegisteredExtension Registration, T Extension)> Usable<T>() where T : class =>
        [.. All.Where(e => e.IsUsable && e.Extension is T).Select(e => (e, (T)e.Extension))];

    /// <summary>ID で探す (使えるものだけ)。</summary>
    public RegisteredExtension? Find(string id) => All.FirstOrDefault(e => e.IsUsable && e.Id == id);

    /// <summary>登録する。重複・不正な ID は登録したうえで無効にし、警告の文を返す (問題がなければ null)。</summary>
    internal string? Add(PluginRecord plugin, IExtension extension)
    {
        string id;
        string name;
        try
        {
            id = extension.Id ?? string.Empty;
            name = extension.DisplayName ?? id;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            id = string.Empty;
            name = string.Empty;
            plugin.Guard.Fault("Id", ex);
        }

        string kind = PluginExtensionKinds.Of(extension);
        var registration = new RegisteredExtension(plugin, extension, id, kind, name);
        string? warning = null;
        lock (_lock)
        {
            if (!PluginManifest.IsValidId(id))
            {
                registration.Status = ExtensionStatus.Invalid;
                warning = Msg.T($"拡張ポイントの ID の形式が正しくないため無効にしました: {id}", $"Disabled an extension with an invalid ID: {id}");
            }
            else if (_all.Any(e => e.Id == id && e.Status == ExtensionStatus.Active))
            {
                RegisteredExtension first = _all.First(e => e.Id == id && e.Status == ExtensionStatus.Active);
                registration.Status = ExtensionStatus.Duplicate;
                warning = Msg.T($"拡張ポイントの ID {id} がプラグイン「{first.Plugin.Id}」と重複しているため、後から読み込んだものを無効にしました。",
                    $"The extension ID {id} duplicates plugin \"{first.Plugin.Id}\"; the one loaded later was disabled.");
            }
            else if (IsBuiltInId?.Invoke(kind, id) == true)
            {
                registration.Status = ExtensionStatus.Duplicate;
                warning = Msg.T($"拡張ポイントの ID {id} がアプリ本体と重複しているため無効にしました。", $"The extension ID {id} duplicates a built-in one and was disabled.");
            }
            else if (!id.StartsWith(plugin.Id + ".", StringComparison.Ordinal))
            {
                registration.Status = ExtensionStatus.Invalid;
                warning = Msg.T($"拡張ポイントの ID は「{plugin.Id}.」で始めてください。無効にしました: {id}", $"Extension IDs must start with \"{plugin.Id}.\"; disabled: {id}");
            }
            else if (kind == "unknown")
            {
                registration.Status = ExtensionStatus.Invalid;
                warning = Msg.T($"対応していない拡張ポイントです: {id}", $"Unsupported extension point: {id}");
            }

            registration.Problem = warning;
            _all.Add(registration);
        }

        plugin.AddExtension(registration);
        return warning;
    }

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
