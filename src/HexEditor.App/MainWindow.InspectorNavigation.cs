using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Inspector;
using HexEditor.Core.View;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// インスペクタからの移動 (INSP-20 の「ポインタとして移動」、INSP-21 の配列モード) のつなぎ込み: 範囲外の InfoBar、コマンドパレットの
/// 「ポインタとして移動」、Hex ビューの要素の境界線。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>ユーザー定義型の定義 (INSP-22。アプリ全体で 1 つ。最初のウィンドウで読み込み、ファイルの変更を監視する)。</summary>
    private static InspectorUserTypes? s_userTypes;

    private static InspectorUserTypes UserTypes
    {
        get
        {
            if (s_userTypes is null)
            {
                s_userTypes = new InspectorUserTypes(Program.Environment.Locations.Settings);
                s_userTypes.Load();
                s_userTypes.Watch();
            }

            return s_userTypes;
        }
    }

    /// <summary><see cref="InitializeAnnotations"/> から呼ぶ。</summary>
    private void InitializeInspectorNavigation()
    {
        _inspectorVm.NavigationFailed += (_, message) => ShowNotice(message, InfoBarSeverity.Warning, Vm.Selected);

        // ユーザー定義型 (INSP-22): 定義ファイルを保存し直すと、再起動せずに行を更新する。読み込みのエラーは InfoBar に出す。
        _inspectorVm.UserTypes = UserTypes;
        EventHandler reloaded = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_closingConfirmed)
            {
                return;
            }

            _inspectorVm.UserTypesReloaded();
            ShowUserTypeErrors();
        });
        UserTypes.Reloaded += reloaded;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                UserTypes.Reloaded -= reloaded;
            }
        };
        if (WindowManager.Windows.Count == 0)
        {
            DispatcherQueue.TryEnqueue(ShowUserTypeErrors);
        }
    }

    /// <summary>定義ファイルの読み込みのエラーを InfoBar に出す (ファイル名・行番号・理由。INSP-22 の「エラー」)。作業中のウィンドウだけに出す。</summary>
    private void ShowUserTypeErrors()
    {
        if (WindowManager.Windows.Count > 0 && WindowManager.Current != this)
        {
            return;
        }

        foreach (InspectorUserTypeError error in UserTypes.Errors.Take(3))
        {
            ShowNotice(Loc.Format("Inspector_UserTypeLoadError", error.File, error.Line, Templates.TemplateDiagnosticText.Message(error.Diagnostic)),
                InfoBarSeverity.Error);
        }
    }

    /// <summary>コマンド (INSP-20 の「呼び出し」のコマンドパレット「ポインタとして移動」)。</summary>
    private void RegisterInspectorNavigationCommands()
    {
        Commands.Register("inspector.followPointer", FollowPointerFromCommand,
            () => Editor is null ? CommandState.Unavailable(Loc.Get("Command_NoDocument")) : CommandState.Available);
    }

    /// <summary>
    /// コマンドパレットから: インスペクタで選んでいる整数の行 (なければ uint32) の値を、最後に使った方法で位置として移動する。
    /// パネルを閉じていれば開いてから行う。
    /// </summary>
    private void FollowPointerFromCommand()
    {
        if (!InspectorVisible)
        {
            ShowPanel(InspectorPanelId);
        }

        _inspectorVm.Refresh();
        InspectorItemViewModel? row = InspectorViewModel.CanFollowPointer(_inspectorVm.Selected) ? _inspectorVm.Selected
            : _inspectorVm.Items.FirstOrDefault(i => i.Kind == InspectorItemKind.Row && i.TypeId == InspectorTypes.UInt32 && !i.Opposite);
        if (row is null)
        {
            ShowNotice(Loc.Get("Inspector_PointerNoRow"), InfoBarSeverity.Informational, Vm.Selected);
            return;
        }

        if (InspectorViewModel.LastPointerMode == PointerMode.RelativeToBase && InspectorView is { } panel)
        {
            panel.ShowBaseFlyout(row);
            return;
        }

        _inspectorVm.FollowPointer(row, InspectorViewModel.LastPointerMode);
    }

    /// <summary>配列モードの要素の境界 (表示範囲だけ。INSP-21 の仕様 5): 長さ 0 の強調 (細い縦線)。</summary>
    private IEnumerable<HexHighlight> InspectorArrayBoundaries(HexView view, long start, long end)
    {
        if (view.Editor != Vm.Selected?.Editor || !_inspectorVm.IsActive || _inspectorVm.ArrayMode is not { } array)
        {
            yield break;
        }

        // 表示範囲の要素が多すぎる (間隔 1 など) と線で埋まるので、表示範囲で 4,096 本までにする。
        int count = 0;
        Microsoft.UI.Xaml.Media.Brush border = AnnotationBrushes.Get("InspectorTargetBorderBrush", view, view.IsHighContrast);
        foreach (long at in array.Boundaries(start, end))
        {
            if (++count > 4096)
            {
                yield break;
            }

            yield return new HexHighlight(at, 0, CellLayer.Focus, null, border, null, "inspectorArray");
        }
    }
}
