using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Panels;
using HexEditor.Core.View;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 画像表示 (VIEW-36) のパネルのつなぎ込み: パネルの登録、作業中の文書・カーソルへの追従、データの変更から 200 ms 後の描き直し (仕様 8)、
/// PNG での保存 (仕様 7)。
/// </summary>
public sealed partial class MainWindow
{
    public const string VisualizerPanelId = "visualizer";

    /// <summary>データの変更から描き直すまでの時間 (仕様 8)。</summary>
    private static readonly TimeSpan VisualizerDelay = TimeSpan.FromMilliseconds(200);

    private VisualizerViewModel? _visualizerVm;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _visualizerTimer;
    private bool _visualizerQueued;

    private VisualizerPanel? VisualizerView => ShownPanelContent(VisualizerPanelId) as VisualizerPanel;

    /// <summary>パネルの一覧に登録する (<see cref="RegisterAnnotationPanels"/> から呼ぶ)。</summary>
    private static void RegisterVisualizerPanel()
    {
        if (PanelRegistry.Find(VisualizerPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(VisualizerPanelId, "Visualizer_PanelName", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateVisualizerPanel()));
        }
    }

    private VisualizerViewModel VisualizerVm()
    {
        if (_visualizerVm is null)
        {
            _visualizerVm = new VisualizerViewModel();
            _visualizerTimer = DispatcherQueue.CreateTimer();
            _visualizerTimer.Interval = VisualizerDelay;
            _visualizerTimer.IsRepeating = false;
            _visualizerTimer.Tick += (_, _) => _visualizerVm.Render();
            _visualizerVm.Attach(Vm.Selected);
        }

        return _visualizerVm;
    }

    private VisualizerPanel CreateVisualizerPanel()
    {
        var panel = new VisualizerPanel(VisualizerVm());
        panel.SaveRequested += (_, _) => _ = SaveVisualizerImageAsync(null);
        panel.FramesSource = VisualizerFrames;
        return panel;
    }

    /// <summary>パネルの表示の変化を反映する (<see cref="SyncAnnotationPanels"/> から呼ぶ)。閉じている間は描かない。</summary>
    private void SyncVisualizer()
    {
        bool shown = IsPanelShown(VisualizerPanelId) && Vm.Selected is not null;
        if (_visualizerVm is { } vm && vm.IsActive != shown)
        {
            vm.IsActive = shown;
            vm.Render();
        }
        else if (shown && _visualizerVm is null)
        {
            VisualizerVm().IsActive = true;
            _visualizerVm!.Render();
        }
    }

    /// <summary>作業中の文書が変わった (<see cref="AttachAnnotationsToSelected"/> から呼ぶ)。</summary>
    private void AttachVisualizer(DocumentViewModel? doc) => _visualizerVm?.Attach(doc);

    /// <summary>文書のデータの変更とカーソルの移動を受ける (<see cref="HookAnnotations"/> から呼ぶ)。外す処理を返す。</summary>
    private Action HookVisualizer(DocumentViewModel doc)
    {
        EventHandler<DocumentChangedEventArgs> changed = (_, e) =>
        {
            if (doc == Vm.Selected && _visualizerVm is { IsActive: true } && _visualizerTimer is { } timer && e.Kind != DocumentChangeKind.Saved)
            {
                // データが変わったら 200 ms 待って描き直す (仕様 8)。
                timer.Stop();
                timer.Start();
            }
        };
        EventHandler loaded = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (doc == Vm.Selected && _visualizerVm is { IsActive: true } vm)
            {
                vm.Render();
            }
        });
        EventHandler editor = (_, _) =>
        {
            if (doc != Vm.Selected || _visualizerVm is not { IsActive: true } vm || _visualizerQueued)
            {
                return;
            }

            // カーソル・選択範囲の移動: 開始位置が追従するなら描き直し、固定なら枠だけを描き直す (同じフレームの変更はまとめる)。
            _visualizerQueued = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _visualizerQueued = false;
                if (vm.StartMode == ByteImageStart.Fixed)
                {
                    VisualizerView?.DrawFrames();
                }
                else
                {
                    vm.Render();
                }
            });
        };
        doc.Document.Changed += changed;
        doc.Document.DataLoaded += loaded;
        doc.EditorChanged += editor;
        return () =>
        {
            doc.Document.Changed -= changed;
            doc.Document.DataLoaded -= loaded;
            doc.EditorChanged -= editor;
        };
    }

    /// <summary>カーソルの画素と、選択範囲の画素の行ごとの枠 (表示範囲だけ、512 個まで)。</summary>
    private IReadOnlyList<(int X, int Y, int Width, int Height, bool Cursor)> VisualizerFrames()
    {
        var frames = new List<(int, int, int, int, bool)>();
        if (_visualizerVm?.Layout is not { Rows: > 0 } layout || _visualizerVm.Document is not { } doc)
        {
            return frames;
        }

        EditorState editor = doc.Editor;
        if (editor.HasSelection)
        {
            int bits = ByteImage.BitsPerPixel(layout.Format);
            long selStart = Math.Max(editor.SelectionStart, layout.Start);
            long selEnd = Math.Min(editor.SelectionStart + editor.SelectionLength, layout.Start + layout.ByteLength);
            long firstRow = selStart >= selEnd ? 0 : (selStart - layout.Start) / layout.Stride;
            long lastRow = selStart >= selEnd ? -1 : (selEnd - 1 - layout.Start) / layout.Stride;
            for (long row = firstRow; row <= lastRow && frames.Count < 512; row++)
            {
                long rowStart = layout.Start + row * layout.Stride;
                long from = Math.Max(selStart, rowStart) - rowStart;
                long to = Math.Min(selEnd, rowStart + layout.Stride) - rowStart;
                int x0 = (int)Math.Min(layout.Width, from * 8 / bits);
                int x1 = (int)Math.Min(layout.Width, (to * 8 + bits - 1) / bits);
                if (x1 > x0)
                {
                    int y = layout.BottomUp ? layout.Rows - 1 - (int)row : (int)row;
                    frames.Add((x0, y, x1 - x0, 1, false));
                }
            }
        }

        if (layout.PixelOf(editor.Cursor) is { } cursor)
        {
            frames.Add((cursor.X, cursor.Y, 1, 1, true));
        }

        return frames;
    }

    /// <summary>
    /// 表示中の画像を PNG で保存する (仕様 7)。<paramref name="path"/> が null なら保存先を選ぶ。保存した画素は拡大前の画素 (表示範囲の幅 × 行数)。
    /// </summary>
    internal async Task<bool> SaveVisualizerImageAsync(string? path)
    {
        if (_visualizerVm?.Layout is not { Rows: > 0 } layout)
        {
            ShowNotice(Loc.Get("Visualizer_NothingToSave"), InfoBarSeverity.Informational, Vm.Selected);
            return false;
        }

        uint[] pixels = _visualizerVm.Pixels;
        path ??= await PickVisualizerPathAsync();
        if (path is null)
        {
            return false;
        }

        try
        {
            await Task.Run(() =>
            {
                using FileStream stream = File.Create(path);
                PngEncoder.Write(stream, layout.Width, layout.Rows, pixels);
            });
            ShowNotice(Loc.Format("Visualizer_Saved", Path.GetFileName(path)), InfoBarSeverity.Success, Vm.Selected);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("Visualizer_SaveError", Path.GetFileName(path), ex.Message), InfoBarSeverity.Error, Vm.Selected);
            return false;
        }
    }

    private async Task<string?> PickVisualizerPathAsync()
    {
        const string Suggested = "image.png";
        if (TestHooks.TrySavePicker(Suggested, out string? chosen))
        {
            return chosen;
        }

        var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(WindowId) { SuggestedFileName = Suggested, SettingsIdentifier = "HexEditor.Visualizer" };
        picker.FileTypeChoices.Add(Loc.Get("Visualizer_PngFiles"), [".png"]);
        return (await picker.PickSaveFileAsync())?.Path;
    }
}
