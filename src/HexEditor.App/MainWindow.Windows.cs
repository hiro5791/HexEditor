using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Files;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace HexEditor.App;

/// <summary>
/// 複数ウィンドウ (UI-14): 新しいウィンドウ、次のウィンドウ、ウィンドウを 1 つ閉じる、アプリの終了 (全ウィンドウの文書の確認。UI-13)。
/// アプリ全体の一覧と振り分けは <see cref="WindowManager"/>。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>新しいウィンドウを開いたときのずらし幅 (前のウィンドウに重ならないように。論理ピクセル)。</summary>
    private const int CascadeOffset = 32;

    /// <summary>
    /// 2 つ目以降のウィンドウの準備 (<see cref="WindowManager.CreateWindow"/> から呼ぶ)。スタートページ、パネルの配置 (最後にアクティブ
    /// だったウィンドウを引き継ぐ。UI-14 の仕様 1)、位置と大きさ、テーマ。
    /// </summary>
    internal void PrepareSecondaryWindow(MainWindow? layoutFrom, SessionWindow? bounds)
    {
        InitializeStartPage();
        if (layoutFrom is not null)
        {
            RestorePanelLayout(System.Text.Json.Nodes.JsonNode.Parse(layoutFrom.PanelLayoutJson.ToJsonString()));
        }

        if (bounds is not null)
        {
            RestoreBounds(bounds);
        }
        else if (layoutFrom is not null)
        {
            // 元のウィンドウと同じ大きさで、少しずらして開く。
            int offset = (int)(CascadeOffset * GetDpiForWindow(Microsoft.UI.Win32Interop.GetWindowFromWindowId(layoutFrom.AppWindow.Id)) / 96.0);
            PointInt32 p = layoutFrom.AppWindow.Position;
            SizeInt32 size = layoutFrom.AppWindow.Size;
            RestoreBounds(new SessionWindow { X = p.X + offset, Y = p.Y + offset, Width = size.Width, Height = size.Height });
        }

        ApplyAppearance();
        StringKeyTips.Apply(this, MainMenu, Root);
        UpdatePackagingMenu();
        ShowPendingUpdateMessage();
        Action<IReadOnlyCollection<string>> settingsChanged = _ => DispatcherQueue.TryEnqueue(RefreshCommandUi);
        App.Settings.Changed += settingsChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                App.Settings.Changed -= settingsChanged;
            }
        };
        TestHooks.OnWindowCreated(this);
    }

    private void RegisterWindowCommands()
    {
        Commands.Register("window.new", () => WindowManager.CreateWindow(this));
        Commands.Register("window.next", () => WindowManager.Next(this),
            () => WindowManager.Windows.Count > 1 ? CommandState.Available : CommandState.Unavailable(Loc.Get("Command_NoOtherWindow")));
    }

    /// <summary>
    /// このウィンドウだけを閉じる (他のウィンドウが残っている。UI-13 の仕様 5)。このウィンドウの文書だけを確かめる。パネルの配置は
    /// 次に開くウィンドウが引き継ぐ (UI-05 の仕様 7)。
    /// </summary>
    private async Task CloseThisWindowAsync()
    {
        if (!await CloseAsync(Vm.Documents.ToList()))
        {
            return;
        }

        _closingConfirmed = true;
        SavePanelLayout();
        StopWindowTimers();
        WindowManager.Unregister(this);
        AppLog.Info($"Window closed ({WindowManager.Windows.Count} window(s))");

        // Closed の処理の中で Close を呼んでも無視されるため、処理を抜けてから閉じる。
        DispatcherQueue.TryEnqueue(Close);
    }

    /// <summary>
    /// アプリの終了の確認 (UI-13 の仕様 2・3): すべてのウィンドウの文書を対象に、処理中の文書、未保存の文書 (複数なら 1 つの一覧)、
    /// 書き出すコピーの量を確かめ、すべての文書を閉じる。キャンセルされたら false (文書は残る)。
    /// </summary>
    internal async Task<bool> ConfirmExitAsync()
    {
        var all = WindowManager.Windows.SelectMany(w => w.Vm.Documents.Select(d => (Window: w, Doc: d))).ToList();

        // 1. 長時間処理の実行中の文書 (その文書のウィンドウで確かめる)。
        foreach ((MainWindow w, DocumentViewModel d) in all.Where(x => x.Window.Vm.Operations.ActiveFor(x.Doc.Document).Count > 0).ToList())
        {
            w.Vm.Selected = d;
            if (!await w.ConfirmBusyAsync(d))
            {
                return false;
            }
        }

        // 2. 未保存の文書。
        var modified = all.Where(x => x.Doc.Document.IsModified && x.Window.Vm.Documents.Contains(x.Doc)).ToList();
        if (modified.Count == 1)
        {
            (MainWindow w, DocumentViewModel d) = modified[0];
            w.Vm.Selected = d;
            if (!await w.ConfirmSaveOneAsync(d))
            {
                return false;
            }
        }
        else if (modified.Count > 1 && !await ConfirmSaveManyAsync([.. modified.Select(x => x.Doc)]))
        {
            return false;
        }

        // 3. コピーした範囲を保持するために書き出す量 (EDIT-24 の仕様 6)。
        long pending = all.Sum(x => x.Doc.Document.PendingReferenceBytes);
        if (pending > MaterializeConfirmBytes && !await ConfirmMaterializeAsync(pending))
        {
            return false;
        }

        foreach (MainWindow w in WindowManager.Windows.ToList())
        {
            foreach (DocumentViewModel doc in w.Vm.Documents.ToList())
            {
                w.Vm.Close(doc);
            }

            w.UpdateTitle();
        }

        return true;
    }

    /// <summary>終了の確認が済んだ: このウィンドウを閉じる。</summary>
    internal void CloseForExit()
    {
        _closingConfirmed = true;
        StopWindowTimers();
        DispatcherQueue.TryEnqueue(Close);
    }

    /// <summary>閉じたウィンドウのタイマーを止める (他のウィンドウが残っている間も動き続けないように)。</summary>
    private void StopWindowTimers()
    {
        _noticeTimer.Stop();
        _pollTimer?.Stop();
        _mruTimer?.Stop();
    }
}
