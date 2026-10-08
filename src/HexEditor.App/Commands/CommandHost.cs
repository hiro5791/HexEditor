using HexEditor.App.Services;
using HexEditor.Core.Commands;

namespace HexEditor.App.Commands;

/// <summary>
/// コマンドの今の状態。<see cref="Reason"/> は使えない理由 (表示言語の文。例: 「文書が開かれていません」)。
/// <see cref="Checked"/> は切り替えのコマンド (表示・非表示など) の今の状態 (切り替えでなければ null)。
/// </summary>
public readonly record struct CommandState(bool Enabled, string? Reason = null, bool? Checked = null)
{
    public static readonly CommandState Available = new(true);

    public static CommandState Unavailable(string reason) => new(false, reason);
}

/// <summary>コマンドの処理 (UI-16 の仕様 1 の「処理」)。<c>argument</c> は引数を尋ねるコマンドの入力 (なければ null)。</summary>
public sealed record CommandHandler(Func<string?, Task> Execute, Func<CommandState>? State = null);

/// <summary>
/// ウィンドウごとのコマンドの処理 (UI-16)。コマンドの定義 (<see cref="CommandService.Catalog"/>) はアプリ全体で 1 つ、処理と有効条件は
/// ウィンドウごと (タブ・複数ウィンドウ UI-09、UI-14 でもウィンドウの文書を対象にする)。
/// </summary>
/// <remarks>
/// 他の機能は、ウィンドウの初期化 (MainWindow.Commands.cs の <c>RegisterCommandHandlers</c>) か、パネルの登録
/// (<see cref="Panels.PanelRegistry"/>) から <see cref="Register(string, Action, Func{CommandState}?)"/> を呼ぶ。
/// </remarks>
public sealed class CommandHost
{
    private readonly Dictionary<string, CommandHandler> _handlers = new(StringComparer.Ordinal);

    /// <summary>実行できなかったことを知らせる (InfoBar。UI-16 の「エラー」)。</summary>
    public Action<string>? ShowError { get; set; }

    /// <summary>状態が変わったかもしれない (メニューの有効状態を更新する)。</summary>
    public event Action? StatesChanged;

    public void Register(string id, CommandHandler handler)
    {
        if (!CommandService.Catalog.Contains(id))
        {
            throw new InvalidOperationException($"登録されていないコマンドの処理です: {id}");
        }

        _handlers[CommandService.Catalog.Canonical(id)] = handler;
    }

    public void Register(string id, Action execute, Func<CommandState>? state = null) =>
        Register(id, new CommandHandler(_ => { execute(); return Task.CompletedTask; }, state));

    public void Register(string id, Func<Task> execute, Func<CommandState>? state = null) =>
        Register(id, new CommandHandler(_ => execute(), state));

    public bool HasHandler(string id) => _handlers.ContainsKey(CommandService.Catalog.Canonical(id));

    /// <summary>今の状態。処理が登録されていないコマンドは使えない。</summary>
    public CommandState StateOf(string id)
    {
        if (!_handlers.TryGetValue(CommandService.Catalog.Canonical(id), out CommandHandler? handler))
        {
            return CommandState.Unavailable(Loc.Get("Command_NotAvailableHere"));
        }

        try
        {
            return handler.State?.Invoke() ?? CommandState.Available;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            return CommandState.Unavailable(ex.Message);
        }
    }

    /// <summary>
    /// 実行する。有効条件を満たさない場合は何もせず「&lt;コマンド名&gt; は今は使えません: &lt;理由&gt;」を出す (UI-16 の「エラー」)。
    /// コマンドパレットから実行したものは最近使ったコマンドに記録する (UI-17 の仕様 6)。実行したら true。
    /// </summary>
    public async Task<bool> ExecuteAsync(string id, string? argument = null, bool fromPalette = false)
    {
        id = CommandService.Catalog.Canonical(id);
        CommandState state = StateOf(id);
        if (!state.Enabled)
        {
            string name = CommandService.Catalog.Find(id) is { } c ? CommandService.DisplayName(c) : id;
            ShowError?.Invoke(Loc.Format("Command_Unavailable", name, state.Reason ?? string.Empty));
            return false;
        }

        CommandService.RecordExecuted(id, fromPalette);
        AppLog.Info($"Command: {id}");
        await _handlers[id].Execute(argument);
        StatesChanged?.Invoke();
        return true;
    }

    /// <summary>メニューなどの状態を作り直す。</summary>
    public void NotifyStatesChanged() => StatesChanged?.Invoke();
}
