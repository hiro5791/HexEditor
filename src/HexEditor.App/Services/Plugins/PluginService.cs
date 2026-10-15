using System.Text;
using HexEditor.App.Commands;
using HexEditor.App.Services.Scripting;
using HexEditor.Core.Commands;
using HexEditor.Core.Plugins;
using HexEditor.Core.Scripting;
using HexEditor.Platform;
using HexEditor.Sdk.Plugins;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.Services.Plugins;

/// <summary>
/// プラグインの管理 (アプリ全体で 1 つ。08 の AUTO-30〜AUTO-35)。Core の <see cref="PluginManager"/> にアプリの環境 (設定・確認のダイアログ・
/// 署名の確認 (WinVerifyTrust)・出力パネル) をつなぐ。
/// <list type="bullet">
/// <item>起動を遅らせない: 最初のウィンドウを表示した後にバックグラウンドで読み込み、終わったものから拡張ポイントを有効にする (AUTO-30 の仕様 4)。</item>
/// <item>セーフモードでは 1 つも読み込まない (仕様 8)。</item>
/// <item>コマンド (<c>plugin.&lt;拡張ポイントの ID&gt;</c>)・パネル・テンプレートの関数を本体の登録簿に入れる (AUTO-31 の仕様 2)。</item>
/// </list>
/// </summary>
internal static class PluginService
{
    public const string SignedOnlyKey = "plugins.signedOnly";
    public const string DevFoldersKey = "plugins.devFolders";

    private static DispatcherQueue? _queue;
    private static readonly Dictionary<string, RegisteredExtension> RegisteredCommands = new(StringComparer.Ordinal);
    private static readonly HashSet<string> RegisteredPanels = new(StringComparer.Ordinal);

    private static IAppEnvironment? _env;
    private static PluginManager? _manager;
    private static bool _creationFailed;

    /// <summary>
    /// プラグインの管理。最初に使うとき (起動後の読み込み、プラグインマネージャを開いたとき) に作る (起動の経路で信頼の一覧を読まないため)。
    /// 作れなかったら null。
    /// </summary>
    public static PluginManager? Manager => _manager ?? (_env is not null && !_creationFailed ? Create(_env) : null);

    /// <summary>表示言語 (プラグインの名前・説明・文字列)。</summary>
    public static string UiLanguage => System.Globalization.CultureInfo.CurrentUICulture.Name;

    /// <summary>セーフモード (コマンドラインの <c>--safe-mode</c> か、起動時に Shift キーを押し続けた。09 の UI-30 の仕様 4)。</summary>
    public static bool SafeMode => Hosting.Program.CommandLine.SafeMode;

    /// <summary>一覧・状態・拡張ポイントが変わった (UI スレッドで発生する)。</summary>
    public static event EventHandler? Changed;

    /// <summary>エラーが続いたためプラグインを止めた (UI スレッドで発生する。InfoBar を出す。AUTO-30 の仕様 7)。</summary>
    public static event EventHandler<PluginRecord>? Suspended;

    /// <summary>コマンドの登録が変わった (ウィンドウはコマンドの処理をつなぎ直す)。</summary>
    public static event EventHandler? CommandsChanged;

    /// <summary>前回の異常終了の原因と考えられるプラグイン (次の起動で無効にするか確認する。AUTO-30 の仕様 7)。</summary>
    public static string? CrashSuspect { get; private set; }

    /// <summary>テスト用: 「プラグインのフォルダを開く」で開こうとしたフォルダ (テスト用のビルドでは Explorer を開かない)。</summary>
    public static string? LastOpenedFolder { get; set; }

    /// <summary>起動時に呼ぶ (ウィンドウを作る前)。ここではファイルを読まない。</summary>
    public static void Initialize(IAppEnvironment env, DispatcherQueue queue)
    {
        _queue = queue;
        _env = env;
    }

    private static PluginManager? Create(IAppEnvironment env)
    {
        try
        {
            var devFolders = new List<string>(DevFolders);
            if (Hosting.Program.CommandLine.PluginDev is { Length: > 0 } dev)
            {
                devFolders.Add(Path.GetFullPath(dev));
            }

            var manager = new PluginManager(new PluginHostOptions
            {
                PluginsRoot = Path.Combine(env.Locations.Settings, "plugins"),
                AppVersion = env.AppVersion,
                Trust = ScriptingService.Trust,
                Verifier = new WinVerifyTrustVerifier(),
                Prompts = new AppPluginPrompts(_queue!),
                Policy = () => App.Settings.GetString(ScriptSettingKeys.Policy, "none"),
                SignedOnly = () => App.Settings.GetBool(SignedOnlyKey, false),
                SafeMode = SafeMode,
                IsElevated = TestHooks.ElevationOverride ?? env.IsElevated,
                DevFolders = devFolders,
                HasUi = true,
                UiLanguage = UiLanguage,
                Output = ScriptingService.Output,
            });
            _manager = manager;
            PluginManager.Current = manager;
            PluginIntegration.Attach(manager);
            manager.Changed += (_, _) =>
            {
                if (manager.Plugins.Count > 0)
                {
                    Post(RaiseChanged);
                }
            };
            manager.Registry.Changed += (_, _) => Post(OnRegistryChanged);
            manager.Suspended += (_, p) => Post(() => Suspended?.Invoke(null, p));
            return manager;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            AppLog.Warning($"Plugins unavailable: {ex.GetType().Name}");
            _creationFailed = true;
            return null;
        }
    }

    /// <summary>開発用のフォルダ (設定 plugins.devFolders。AUTO-33)。</summary>
    public static IReadOnlyList<string> DevFolders =>
        App.Settings.GetNode(DevFoldersKey) is System.Text.Json.Nodes.JsonArray a
            ? [.. a.Where(n => n?.GetValueKind() == System.Text.Json.JsonValueKind.String).Select(n => n!.GetValue<string>()).Where(s => s.Length > 0)]
            : [];

    private static void SetDevFolders(IEnumerable<string> folders) =>
        App.Settings.SetNode(DevFoldersKey, new System.Text.Json.Nodes.JsonArray([.. folders.Select(f => (System.Text.Json.Nodes.JsonNode?)f)]), new System.Text.Json.Nodes.JsonArray());

    /// <summary>
    /// 最初のウィンドウを表示した後に呼ぶ: バックグラウンドで見つけて読み込む。前回の異常終了の原因と考えられるプラグインは先に取り出す。
    /// </summary>
    public static void StartLoading()
    {
        if (Manager is not { } m)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                CrashSuspect = m.TakeCrashSuspect();
                if (m.Discover().Count > 0)
                {
                    Post(RaiseChanged);
                }
                await m.LoadAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Warning($"Plugin loading failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Post(() =>
                {
                    // プラグインが 1 つもなければ、ウィンドウの一覧・メニューを作り直さない (起動の直後の UI スレッドの仕事を増やさない)。
                    if (m.Plugins.Count == 0 && m.Registry.All.Count == 0)
                    {
                        return;
                    }

                    OnRegistryChanged();
                    RaiseChanged();
                    if (CrashSuspect is not null && WindowManager.Windows.Count > 0)
                    {
                        _ = WindowManager.Current.AskAboutCrashedPluginAsync();
                    }
                });
            }
        });
    }

    /// <summary>異常終了の呼び出し履歴にプラグインがあれば記録する (CrashReporter から呼ぶ。次の起動で確認する)。</summary>
    public static void RecordCrash(Exception exception)
    {
        try
        {
            Manager?.RecordCrash(exception.ToString());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning($"Plugin crash record failed: {ex.GetType().Name}");
        }
    }

    private static void Post(Action action)
    {
        if (_queue is null || _queue.HasThreadAccess)
        {
            action();
            return;
        }

        _queue.TryEnqueue(() => action());
    }

    private static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    // ---- 拡張ポイントを本体の登録簿へ (UI スレッド) ----

    private static void OnRegistryChanged()
    {
        if (Manager is not { } m)
        {
            return;
        }

        SyncCommands(m);
        SyncPanels(m);
        PluginIntegration.RegisterTemplateFunctions(m, ScriptingService.Templates.Extensions);
        GlobalColoringRules.SetPluginRules(PluginColorRules(m));
        RaiseChanged();
    }

    /// <summary>コマンドの ID (<c>plugin.</c> + 拡張ポイントの ID。コマンドの ID の形式に合わせて <c>-x</c> を <c>X</c> にする)。</summary>
    public static string CommandIdOf(string extensionId)
    {
        var sb = new StringBuilder("plugin.");
        bool upper = false;
        foreach (char c in extensionId)
        {
            if (c == '-')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }

    /// <summary>コマンドの ID からプラグインのコマンドを探す。</summary>
    public static RegisteredExtension? CommandOf(string commandId) => RegisteredCommands.GetValueOrDefault(commandId);

    public static IReadOnlyDictionary<string, RegisteredExtension> Commands => RegisteredCommands;

    private static void SyncCommands(PluginManager m)
    {
        CommandCatalog catalog = CommandService.Catalog;
        var wanted = new Dictionary<string, RegisteredExtension>(StringComparer.Ordinal);
        foreach ((RegisteredExtension registration, ICommand _) in m.Registry.Usable<ICommand>())
        {
            string id = CommandIdOf(registration.Id);
            if (CommandDefinition.IsValidId(id) && catalog.Find(id) is null or { Category: "plugins" })
            {
                wanted.TryAdd(id, registration);
            }
        }

        foreach ((RegisteredExtension registration, IByteSourceProvider _) in m.Registry.Usable<IByteSourceProvider>())
        {
            string id = CommandIdOf(registration.Id) + ".open";
            if (CommandDefinition.IsValidId(id))
            {
                wanted.TryAdd(id, registration);
            }
        }

        foreach ((RegisteredExtension registration, IExportFormat _) in m.Registry.Usable<IExportFormat>())
        {
            string id = CommandIdOf(registration.Id) + ".copyAs";
            if (CommandDefinition.IsValidId(id))
            {
                wanted.TryAdd(id, registration);
            }
        }

        foreach ((RegisteredExtension registration, IDataTransform _) in m.Registry.Usable<IDataTransform>())
        {
            string id = CommandIdOf(registration.Id) + ".apply";
            if (CommandDefinition.IsValidId(id))
            {
                wanted.TryAdd(id, registration);
            }
        }

        foreach (string id in RegisteredCommands.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            catalog.Unregister(id);
            RegisteredCommands.Remove(id);
        }

        foreach ((string id, RegisteredExtension registration) in wanted)
        {
            if (RegisteredCommands.ContainsKey(id))
            {
                continue;
            }

            RegisteredCommands[id] = registration;
            catalog.Register(new CommandDefinition(id, "plugins") { Text = CommandText(id, registration) });
        }

        CommandsChanged?.Invoke(null, EventArgs.Empty);
    }

    private static string CommandText(string id, RegisteredExtension registration) =>
        id.EndsWith(".open", StringComparison.Ordinal) && registration.Extension is IByteSourceProvider
            ? Loc.Format("Plugin_OpenSourceCommand", registration.DisplayName)
            : id.EndsWith(".copyAs", StringComparison.Ordinal) && registration.Extension is IExportFormat
                ? Loc.Format("Plugin_CopyAsCommand", registration.DisplayName)
                : id.EndsWith(".apply", StringComparison.Ordinal) && registration.Extension is IDataTransform
                    ? Loc.Format("Plugin_DataTransformCommand", registration.DisplayName)
                    : registration.DisplayName;

    /// <summary>プラグインのパネルを登録する (アプリ全体。登録は取り消さない: 再起動で反映する)。</summary>
    private static void SyncPanels(PluginManager m)
    {
        foreach (RegisteredExtension registration in m.Registry.Usable(PluginExtensionKinds.Panel))
        {
            string id = PanelIdOf(registration.Id);
            if (!RegisteredPanels.Add(id) || Panels.PanelRegistry.Find(id) is not null)
            {
                continue;
            }

            if (CommandService.Catalog.Find("view.panel." + id) is null)
            {
                CommandService.Catalog.Register(new CommandDefinition("view.panel." + id, "plugins") { Text = registration.DisplayName });
            }

            Panels.PanelRegistry.Register(new Panels.PanelRegistration(id, "Plugin_PanelTitle", Core.Panels.PanelDock.Right, _ => PluginPanels.Create(registration))
            {
                Title = registration.DisplayName,
                RequiresDocument = false,
            });
        }
    }

    /// <summary>パネルの ID (<c>plugin</c> + 拡張ポイントの ID の英数字)。</summary>
    public static string PanelIdOf(string extensionId) =>
        "plugin" + string.Concat(CommandIdOf(extensionId)["plugin.".Length..].Split('.').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p[1..]));

    private static IReadOnlyList<Core.Coloring.ColoringRule> PluginColorRules(PluginManager m)
    {
        var rules = new List<Core.Coloring.ColoringRule>();
        foreach ((RegisteredExtension registration, IColorRuleProvider provider) in m.Registry.Usable<IColorRuleProvider>())
        {
            IReadOnlyList<PluginColorRule> items = registration.Guard.Invoke("Rules", () => provider.Rules, []);
            int index = 0;
            foreach (PluginColorRule r in items.Take(64))
            {
                Core.Coloring.ColoringConditionKind? kind = r.Kind switch
                {
                    "byteValues" => Core.Coloring.ColoringConditionKind.ByteValues,
                    "hexPattern" => Core.Coloring.ColoringConditionKind.HexPattern,
                    "text" => Core.Coloring.ColoringConditionKind.Text,
                    "regex" => Core.Coloring.ColoringConditionKind.Regex,
                    _ => null,
                };
                if (kind is null)
                {
                    continue;
                }

                rules.Add(new Core.Coloring.ColoringRule
                {
                    Id = registration.Id + "#" + index++,
                    Name = $"{r.Name} ({PluginIntegration.PluginNameOf(m, registration)})",
                    Kind = kind.Value,
                    Pattern = r.Pattern ?? string.Empty,
                    Foreground = r.Foreground is { } f ? (uint)(f & 0xFFFFFF) : null,
                    Background = r.Background is { } b ? (uint)(b & 0xFFFFFF) : null,
                });
            }
        }

        return rules;
    }

    // ---- 管理の操作 ----

    /// <summary>次の起動で反映する変更がある (「今すぐ再起動」を出す)。</summary>
    public static bool RestartRequired => Manager?.RestartRequired == true;

    /// <summary>開発用のフォルダを加える (設定に記録し、次の起動から読み込む。信頼の確認は読み込むときに 1 回。AUTO-33)。</summary>
    public static void AddDevFolder(string folder)
    {
        List<string> list = [.. DevFolders];
        if (!list.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(folder);
            SetDevFolders(list);
        }
    }

    public static void RemoveDevFolder(string folder) => SetDevFolders(DevFolders.Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)));

    /// <summary>プラグインの置き場所 (<c>&lt;設定フォルダ&gt;\plugins</c>)。</summary>
    public static string PluginsFolder => Manager?.Options.PluginsRoot ?? Path.Combine(Hosting.Program.Environment.Locations.Settings, "plugins");

    /// <summary>WinVerifyTrust による確認 (AUTO-35)。<c>network.offline</c> なら失効の確認はキャッシュだけ。</summary>
    private sealed class WinVerifyTrustVerifier : IPluginSignatureVerifier
    {
        public FileSignature Verify(string path)
        {
            Platform.Security.AuthenticodeVerifier.SignatureResult r = Platform.Security.AuthenticodeVerifier.VerifyPluginFile(path, AppUpdates.Policy.Offline);
            return r.Status switch
            {
                Platform.Security.AuthenticodeVerifier.SignatureStatus.Valid => new FileSignature(PluginSignatureState.Valid, r.Publisher, r.Thumbprint, null, r.RevocationUnchecked),
                Platform.Security.AuthenticodeVerifier.SignatureStatus.Unsigned => FileSignature.Unsigned,
                _ => new FileSignature(PluginSignatureState.Invalid, Problem: $"0x{r.Code:X8}"),
            };
        }
    }
}
