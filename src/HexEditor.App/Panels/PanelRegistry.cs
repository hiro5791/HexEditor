using HexEditor.App.ViewModels;
using HexEditor.Core.Panels;
using Microsoft.UI.Xaml;

namespace HexEditor.App.Panels;

/// <summary>
/// パネルの中身に渡すもの (UI-05 の仕様 6)。パネルは作業中の文書 (アクティブなタブ) に追従して内容を切り替える。
/// ウィンドウごとに 1 つ。
/// </summary>
public sealed class PanelContext
{
    internal PanelContext(Window window, MainViewModel vm)
    {
        Window = window;
        Vm = vm;
    }

    public Window Window { get; }

    public MainViewModel Vm { get; }

    /// <summary>作業中の文書。文書がなければ null。</summary>
    public DocumentViewModel? ActiveDocument => Vm.Selected;

    /// <summary>作業中の文書が変わった (タブの切り替え、開く・閉じる)。</summary>
    public event EventHandler? ActiveDocumentChanged;

    internal void RaiseActiveDocumentChanged() => ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// パネルの中身が F6 の領域の移動 (UI-52) でフォーカスを受ける要素を決める (一覧のあるパネルは一覧に移す)。実装しなければ、
/// 最初にフォーカスできる要素に移す。
/// </summary>
public interface IPanelContent
{
    bool FocusContent();
}

/// <summary>
/// パネルの登録 (UI-05)。<paramref name="TitleKey"/> は見出しのリソースのキー、<paramref name="Factory"/> はウィンドウごとに 1 度だけ
/// 呼んで中身を作る。<see cref="ToggleCommand"/> (既定 <c>view.panel.&lt;ID&gt;</c>) が登録済みのコマンドなら、
/// その処理 (表示の切り替え) を自動でつなぐ。<see cref="RequiresDocument"/> なら、文書がないとき「文書が開かれていません」を出す。
/// </summary>
public sealed record PanelRegistration(string Id, string TitleKey, PanelDock DefaultDock, Func<PanelContext, FrameworkElement> Factory)
{
    public string ToggleCommand { get; init; } = "view.panel." + Id;

    public bool RequiresDocument { get; init; } = true;

    /// <summary>見出しの文字列 (リソースを持たないプラグインのパネル用。設定すると <see cref="TitleKey"/> より優先する)。</summary>
    public string? Title { get; init; }
}

/// <summary>
/// アプリ全体のパネルの一覧。他の機能 (データインスペクタ、ブックマーク、結果一覧など) はアプリの起動時
/// (<see cref="App"/> の OnLaunched の前、またはウィンドウを作る前) に <see cref="Register"/> する。
/// </summary>
public static class PanelRegistry
{
    private static readonly List<PanelRegistration> Panels = [];

    public static IReadOnlyList<PanelRegistration> All => Panels;

    /// <summary>パネルが増えた (開いているウィンドウは表示切り替えのコマンドの処理をつなぐ)。</summary>
    public static event Action<PanelRegistration>? Registered;

    public static void Register(PanelRegistration panel)
    {
        if (Panels.Any(p => p.Id == panel.Id))
        {
            throw new InvalidOperationException($"パネル ID が重複しています: {panel.Id}");
        }

        Panels.Add(panel);
        Registered?.Invoke(panel);
    }

    public static PanelRegistration? Find(string id) => Panels.FirstOrDefault(p => p.Id == id);

    public static IReadOnlyDictionary<string, PanelDock> Defaults => Panels.ToDictionary(p => p.Id, p => p.DefaultDock);
}
