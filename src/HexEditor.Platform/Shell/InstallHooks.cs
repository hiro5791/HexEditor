using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Platform.Shell;

/// <summary>
/// インストーラ版のフックの処理 (PKG-08、PKG-09)。Velopack のフック (VelopackBootstrap) から呼ぶ。UI を出さず、XAML を使わない。
/// 失敗はデータフォルダの <c>logs\install.log</c> に書き、インストール自体は失敗にしない (例外を外に出さない)。
/// 全体を <see cref="Budget"/> (5 秒) 以内に終える (仕様 4)。
/// </summary>
public sealed class InstallHooks
{
    public const string LogFileName = "install.log";
    public const string StateFileName = "state.json";
    public const string RemoveUserDataKey = "uninstall.removeUserData";

    /// <summary>フックの制限時間 (PKG-08 の仕様 4)。</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly IUserRegistry _registry;
    private readonly string _dataRoot;
    private readonly string _exePath;
    private readonly Action _notifyAssociationsChanged;
    private readonly Stopwatch _clock = new();
    private readonly TimeSpan _budget;
    private readonly ShellLabels _labels;
    private bool _dataDeleted;

    /// <param name="dataRoot">データフォルダ (%LocalAppData%\HexEditorData)。</param>
    /// <param name="exePath">登録する exe (<c>current\HexEditor.exe</c>)。</param>
    /// <param name="notifyAssociationsChanged">SHChangeNotify(SHCNE_ASSOCCHANGED) の呼び出し (テストでは差し替える)。</param>
    /// <param name="labels">Explorer に出す文字列 (登録時の表示言語。09 の UI-54 の仕様 1)。null なら英語。</param>
    public InstallHooks(IUserRegistry registry, string dataRoot, string exePath, Action? notifyAssociationsChanged = null, TimeSpan? budget = null,
        ShellLabels? labels = null)
    {
        _labels = labels ?? ShellLabels.English;
        _registry = registry;
        _dataRoot = dataRoot;
        _exePath = exePath;
        _notifyAssociationsChanged = notifyAssociationsChanged ?? NotifyAssociationsChanged;
        _budget = budget ?? Budget;
    }

    public string LogPath => Path.Combine(_dataRoot, "logs", LogFileName);

    /// <summary>インストール後: 一覧を登録し、Explorer に知らせる。</summary>
    public void AfterInstall(string version) => Run("after-install", version, () => RegisterAll());

    /// <summary>更新後: 新しい版の内容で登録し直す (冪等。利用者が解除した項目は登録しない)。</summary>
    public void AfterUpdate(string version) => Run("after-update", version, () => RegisterAll());

    /// <summary>
    /// アンインストール前: 一覧の登録をすべて消す。設定 <c>uninstall.removeUserData</c> が true ならデータフォルダを消す (PKG-09 の仕様 1)。
    /// </summary>
    public void BeforeUninstall(string version) => Run("before-uninstall", version, () =>
    {
        IReadOnlyList<string> failures = ShellRegistration.Unregister(_registry, TimeUp);
        Log(failures.Count == 0 ? "Unregistered all entries." : "Unregister failed: " + string.Join(", ", failures));
        _notifyAssociationsChanged();
        if (ReadRemoveUserData())
        {
            Log("uninstall.removeUserData = true: deleting the data folder.");
            DeleteDataFolder();
        }
    });

    /// <summary>設定ファイル (平らな JSON。09 の UI-23) の <c>uninstall.removeUserData</c>。読めなければ false (データを残す)。</summary>
    public bool ReadRemoveUserData()
    {
        try
        {
            string path = Path.Combine(_dataRoot, "settings.json");
            if (!File.Exists(path))
            {
                return false;
            }

            JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
            return node?[RemoveUserDataKey] is JsonValue value && value.TryGetValue(out bool remove) && remove;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>利用者が設定で解除した項目 (state.json の <c>shellRegistration.disabled</c>。PKG-08 の仕様 5)。</summary>
    public IReadOnlySet<string> ReadDisabled()
    {
        try
        {
            string path = Path.Combine(_dataRoot, StateFileName);
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["shellRegistration"]?["disabled"] is JsonArray array)
            {
                return array.Select(n => n?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            Log($"state.json could not be read ({ex.GetType().Name}); registering everything.");
        }

        return new HashSet<string>();
    }

    /// <summary>設定の「プログラムから開く」の候補の拡張子 (09 の UI-56 の仕様 2)。読めなければ既定の一覧。</summary>
    public IReadOnlyList<string> ReadOpenWithExtensions()
    {
        try
        {
            string path = Path.Combine(_dataRoot, "settings.json");
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?[ShellRegistration.OpenWithExtensionsKey] is JsonValue value
                && value.TryGetValue(out string? text))
            {
                return ShellRegistration.ParseExtensions(text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }

        return ShellRegistration.DefaultOpenWithExtensions;
    }

    private void RegisterAll()
    {
        var context = new ShellRegistrationContext(_exePath, _labels, ReadOpenWithExtensions());
        IReadOnlyList<string> failures = ShellRegistration.Register(_registry, context, ReadDisabled(), null, TimeUp);
        Log(failures.Count == 0 ? "Registered all entries." : "Register failed: " + string.Join(", ", failures));
        _notifyAssociationsChanged();
    }

    private bool TimeUp() => _clock.Elapsed > _budget;

    private void Run(string hook, string version, Action action)
    {
        _clock.Restart();
        Log($"{hook} {version}");
        try
        {
            action();
        }
        catch (Exception ex)
        {
            // インストール自体は失敗にしない (PKG-08 の「エラー」)。
            Log($"{hook} failed: {Redaction.RedactPaths(ex.ToString())}");
        }

        Log($"{hook} finished in {_clock.ElapsedMilliseconds} ms" + (TimeUp() ? " (over the time limit)" : string.Empty));
    }

    private void DeleteDataFolder()
    {
        try
        {
            if (Directory.Exists(_dataRoot))
            {
                Directory.Delete(_dataRoot, recursive: true);
            }

            // ログもデータフォルダの中なので、消した後は書かない (フォルダを作り直さない)。
            _dataDeleted = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"Deleting the data folder failed: {ex.GetType().Name}");
        }
    }

    private void Log(string message)
    {
        if (_dataDeleted)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void NotifyAssociationsChanged()
    {
        const int ShcneAssocChanged = 0x08000000;
        SHChangeNotify(ShcneAssocChanged, 0, 0, 0);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
