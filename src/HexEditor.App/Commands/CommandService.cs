using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.Core.Commands;
using HexEditor.Core.Settings;

namespace HexEditor.App.Commands;

/// <summary>
/// アプリ全体で 1 つのコマンドの仕組み (UI-16〜UI-21): コマンド登録、キー割り当て (keybindings.json)、最近使ったコマンド
/// (state.json)、設定項目の登録 (UI-22)。ウィンドウごとの処理は <see cref="CommandHost"/>。UI スレッドから使う。
/// </summary>
public static class CommandService
{
    private static Timer? _saveTimer;
    private static bool _loading;

    /// <summary>コマンド登録。組み込みのコマンドは Core の BuiltInCommands。</summary>
    public static CommandCatalog Catalog { get; } = CommandCatalog.CreateBuiltIn();

    /// <summary>キー割り当て。変更は即座に全ウィンドウのメニュー・ツールチップ・パレットに反映する (UI-18 の仕様 10)。</summary>
    public static KeyMap Keys { get; } = new(Catalog);

    /// <summary>設定項目の登録 (UI-22)。</summary>
    public static SettingsCatalog Settings { get; } = SettingsCatalog.CreateBuiltIn();

    /// <summary>state.json (最近使ったコマンド、パネルの配置など)。</summary>
    public static StateStore State { get; private set; } = null!;

    public static RecentCommands Recent { get; private set; } = new();

    public static string Folder { get; private set; } = string.Empty;

    public static string KeybindingsPath => Path.Combine(Folder, KeyBindingsDocument.FileName);

    /// <summary>keybindings.json の誤り (InfoBar で知らせる。UI-18 の「エラー」)。行番号 (0 は不明) と理由 (表示言語の文)。</summary>
    public static (int Line, string Message)? KeybindingsError { get; private set; }

    /// <summary>
    /// keybindings.json に書き込まない (新しい版のアプリが書いたファイル、または読めなかったファイル。UI-18 の「エラー」)。
    /// 割り当ての変更はセッション中だけ有効。
    /// </summary>
    public static bool KeybindingsReadOnly { get; private set; }

    /// <summary>keybindings.json が新しい版のアプリで書かれている (InfoBar で知らせる)。</summary>
    public static bool KeybindingsTooNew { get; private set; }

    /// <summary>
    /// 読んだ keybindings.json に不正な部分がある。次に書くとき、元のファイルを <c>keybindings.json.broken-&lt;日時&gt;</c> として残す
    /// (利用者が書いた内容を黙って失わない。UI-18 の「エラー」)。
    /// </summary>
    private static bool _keepInvalidFile;

    /// <summary>keybindings.json を書けなかった (InfoBar で知らせる。UI-22 の「エラー」と同じ)。引数は理由。書き込みのスレッドから呼ばれることがある。</summary>
    public static event Action<string>? SaveFailed;

    private static bool _saveFailing;

    /// <summary>キー割り当てか配列が変わった (メニュー・ツールバー・一覧の表示を作り直す)。</summary>
    public static event Action? BindingsChanged;

    /// <summary>コマンドを実行した (マクロの記録の対象。UI-16 の仕様 4、08 の AUTO-27)。引数はコマンド ID。</summary>
    public static event Action<string>? Executed;

    public static void Initialize(string folder)
    {
        Folder = folder;
        State = new StateStore(folder);
        State.Load();
        Recent = new RecentCommands(State.Get(RecentCommands.StateKey) is JsonArray a
            ? a.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null).OfType<string>()
            : null);
        LoadKeybindings();
        Keys.Changed += (_, _) =>
        {
            if (!_loading)
            {
                ScheduleSave();
            }

            BindingsChanged?.Invoke();
        };
        KeyboardLayout.Changed += () => BindingsChanged?.Invoke();
    }

    /// <summary>keybindings.json を読む。読めない場合は既定の割り当てで動く。</summary>
    public static void LoadKeybindings()
    {
        KeybindingsError = null;
        KeybindingsReadOnly = false;
        KeybindingsTooNew = false;
        _keepInvalidFile = false;
        _loading = true;
        try
        {
            if (!File.Exists(KeybindingsPath))
            {
                Keys.Load(new KeyBindingsDocument());
                return;
            }

            KeyBindingsDocument doc = KeyBindingsDocument.Parse(File.ReadAllText(KeybindingsPath));
            Keys.Load(doc);
            if (doc.IsTooNew)
            {
                // 新しい版のアプリが書いたファイル: 読める割り当てだけを使い、書き込みを止める (UI-23 の仕様 6 と同じ)。
                KeybindingsTooNew = true;
                KeybindingsReadOnly = true;
            }
            else if (doc.Errors.Count > 0)
            {
                _keepInvalidFile = true;
                KeybindingsError = (doc.Errors[0].Line, LineErrorText(doc.Errors[0]));
            }
            else if (Keys.PresetError is { } presetError)
            {
                KeybindingsError = (0, PresetErrorText(presetError));
            }
        }
        catch (JsonException ex)
        {
            Keys.Load(new KeyBindingsDocument());
            _keepInvalidFile = true;
            KeybindingsError = ((int)(ex.LineNumber ?? 0) + 1, Loc.Get("Keys_InvalidJson"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 読めないファイルを既定の割り当てで上書きしない。
            KeybindingsReadOnly = true;
            KeybindingsError = (0, ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>keybindings.json の行の誤りの表示 (UI-18 の「エラー」、UI-21 のインポートの差分)。</summary>
    public static string LineErrorText(KeyBindingsError error) =>
        Loc.Format("KeysError_" + error.Kind, error.Value ?? string.Empty);

    /// <summary>同梱のプリセットが読めない理由の表示 (UI-19 の「エラー」)。</summary>
    public static string PresetErrorText(KeyPresetError error) =>
        Loc.Format("KeyPresetError_" + error.Kind, error.Preset, error.Line);

    private static void ScheduleSave()
    {
        if (KeybindingsReadOnly)
        {
            return;
        }

        _saveTimer ??= new Timer(_ => SaveKeybindingsNow(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(SettingsStore.WriteDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// keybindings.json を書く (一時ファイルに書いてから置き換える)。書けなければ <see cref="SaveFailed"/> で知らせ、割り当てはセッション中だけ
    /// 有効にする (例外は投げない。タイマーのスレッドから呼ばれるため)。書けたら true。
    /// </summary>
    public static bool SaveKeybindingsNow()
    {
        if (KeybindingsReadOnly)
        {
            return false;
        }

        try
        {
            string json = App.DispatcherQueue is { } q && !q.HasThreadAccess
                ? RunOnUi(() => Keys.ToDocument().ToJson())
                : Keys.ToDocument().ToJson();
            if (_keepInvalidFile && File.Exists(KeybindingsPath))
            {
                // 不正だったファイルは上書きする前に別名で残す (UI-18 の「エラー」)。
                File.Copy(KeybindingsPath, KeybindingsPath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
            }

            _keepInvalidFile = false;
            WriteAtomic(KeybindingsPath, json);
            _saveFailing = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"keybindings.json not saved: {ex.Message}");
            if (!_saveFailing)
            {
                _saveFailing = true;
                SaveFailed?.Invoke(ex.Message);
            }

            return false;
        }
    }

    private static string RunOnUi(Func<string> f)
    {
        string result = string.Empty;
        using var done = new ManualResetEventSlim();
        if (!App.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                result = f();
            }
            finally
            {
                done.Set();
            }
        }))
        {
            return f();
        }

        done.Wait(TimeSpan.FromSeconds(5));
        return result;
    }

    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>終了時: まだ書いていない変更を書く。</summary>
    public static void Flush()
    {
        if (_saveTimer is not null)
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            SaveKeybindingsNow();
        }

        State?.Flush();
    }

    // ---- 表示 ----

    /// <summary>表示名 (表示言語。未翻訳は英語)。</summary>
    public static string DisplayName(CommandDefinition command) => command.Text ?? Loc.Get(NameKey(command.Id));

    public static string DisplayName(string id) => Catalog.Find(id) is { } c ? DisplayName(c) : id;

    /// <summary>英語名 (検索用。UI-16 の仕様 1)。</summary>
    public static string EnglishName(CommandDefinition command) => command.Text ?? Loc.English(NameKey(command.Id)) ?? DisplayName(command);

    /// <summary>検索用の別名 (表示しない。UI-16 の仕様 1)。</summary>
    public static string Aliases(CommandDefinition command) => command.Text is null ? Loc.TryGet("CmdAlias_" + CommandDefinition.KeyPart(command.Id)) ?? string.Empty : string.Empty;

    public static string CategoryName(string category) => Loc.TryGet("CmdCategory_" + category) ?? category;

    /// <summary>「カテゴリ: 表示名」(コマンドパレット、InfoBar)。</summary>
    public static string Title(CommandDefinition command) => Loc.Format("Command_Title", CategoryName(command.Category), DisplayName(command));

    private static string NameKey(string id) => "Cmd_" + CommandDefinition.KeyPart(id);

    /// <summary>コマンドの今のショートカットの表示 (例: <c>Ctrl+G, Ctrl+J</c>)。メニューの右側とツールチップに出す。</summary>
    public static string ShortcutText(string id) =>
        KeyboardLayout.Format(Keys.BindingsFor(id).Select(b => b.Binding.Chord).Distinct());

    public static string ScopeName(KeyScope scope) => Loc.Get("KeyScope_" + KeyScopes.Name(scope));

    /// <summary>実行を知らせる。コマンドパレットから実行したものは最近使ったものに記録する (UI-17 の仕様 6)。</summary>
    public static void RecordExecuted(string id, bool fromPalette)
    {
        if (fromPalette)
        {
            Recent.Add(id);
            State.Set(RecentCommands.StateKey, new JsonArray([.. Recent.Ids.Select(i => (JsonNode?)i)]));
        }

        Executed?.Invoke(id);
    }
}
