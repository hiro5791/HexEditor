namespace HexEditor.Core.Commands;

/// <summary>
/// コマンド登録 (UI-16)。アプリのすべての操作をここに 1 つずつ登録し、メニュー・ツールバー・コマンドパレット・
/// ショートカットはここから表示名・ショートカット・有効状態を取る。アプリ全体で 1 つ (ウィンドウごとの処理は App 側)。
/// </summary>
/// <remarks>
/// 組み込みのコマンドは <see cref="BuiltInCommands"/> に書く。プラグイン・スクリプトは実行時に <see cref="Register"/> する。
/// </remarks>
public sealed class CommandCatalog
{
    private readonly Dictionary<string, CommandDefinition> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _formerIds = new(StringComparer.Ordinal);
    private readonly List<CommandDefinition> _ordered = [];

    public CommandCatalog()
    {
    }

    public CommandCatalog(IEnumerable<CommandDefinition> commands)
    {
        foreach (CommandDefinition command in commands)
        {
            Register(command);
        }
    }

    /// <summary>組み込みのコマンドをすべて登録した登録。</summary>
    public static CommandCatalog CreateBuiltIn() => new(BuiltInCommands.All);

    /// <summary>登録の順 (メニューの順) の全コマンド。</summary>
    public IReadOnlyList<CommandDefinition> All => _ordered;

    /// <summary>コマンドが増えた・減った (プラグインの読み込みなど)。</summary>
    public event EventHandler? Changed;

    /// <summary>登録する。ID が重複していれば例外 (UI-16 の受け入れ基準 3)。</summary>
    public void Register(CommandDefinition command)
    {
        if (_byId.ContainsKey(command.Id) || _formerIds.ContainsKey(command.Id))
        {
            throw new InvalidOperationException($"コマンド ID が重複しています: {command.Id}");
        }

        _byId[command.Id] = command;
        _ordered.Add(command);
        foreach (string former in command.FormerIds)
        {
            _formerIds[former] = command.Id;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>実行時に登録したコマンドを外す (プラグインを無効にしたときなど)。</summary>
    public bool Unregister(string id)
    {
        if (!_byId.Remove(id, out CommandDefinition? command))
        {
            return false;
        }

        _ordered.Remove(command);
        foreach (string former in command.FormerIds)
        {
            _formerIds.Remove(former);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>ID (以前の ID を含む) からコマンドを探す。</summary>
    public CommandDefinition? Find(string id) =>
        _byId.TryGetValue(id, out CommandDefinition? c) ? c
        : _formerIds.TryGetValue(id, out string? current) ? _byId[current]
        : null;

    /// <summary>以前の ID を今の ID に直す。知らない ID はそのまま返す。</summary>
    public string Canonical(string id) => _formerIds.TryGetValue(id, out string? current) ? current : id;

    public bool Contains(string id) => Find(id) is not null;
}
