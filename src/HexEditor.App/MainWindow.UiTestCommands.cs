#if HEX_TEST_HOOKS
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の追加の命令 (テスト方針 7.2、8.4): ドロップ・ウィンドウの大きさ・フォーカス・メニューの文字列・
/// 文字列の切れと要素の重なりの検出 (UI-46、UI-47) など。命令の一覧は <see cref="HandleMoreTestCommandsAsync"/>。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>MainWindow.TestMenu.cs の命令の一覧にない命令。知らない命令なら null。</summary>
    private async Task<JsonObject?> HandleMoreTestCommandsAsync(string cmd, JsonObject request) => cmd switch
    {
        // ファイル > 開く と同じ処理 (開けない理由を通知で知らせる。ENG-11 の「エラー」)。
        "uiOpen" => new JsonObject { ["opened"] = TryOpen(request["path"]!.GetValue<string>()) is not null },
        "drop" => await TestDropAsync(request),
        "resizePhysical" => TestResizePhysical(request),
        "focus" => TestFocus(request),
        "region" => Run(() => MoveToRegion(request["forward"]?.GetValue<bool>() ?? true)),
        "menuTexts" => TestMenuTexts(),
        "textCheck" => TestTextCheck(),
        "findKey" => TestFindKey(request),
        "sourceStats" => TestSourceStats(),
        "addProbe" => TestAddProbe(request),
        "nonClientRegions" => TestNonClientRegions(),
        "hash" => await TestHashAsync(request),

        // ファイル・セッションの命令 (MainWindow.FilesTestCommands.cs)、コマンド・パネル・設定の命令 (MainWindow.FrameworkTestCommands.cs)。
        _ => await HandleFilesTestCommandAsync(cmd, request) ?? await HandleFrameworkTestCommandAsync(cmd, request)
            ?? HandleInspectorTestCommand(cmd, request),
    };

    /// <summary>状態の表示の追加の項目。</summary>
    private void AddTestStateExtras(JsonObject state)
    {
        state["operations"] = new JsonArray([.. Vm.Operations.Active.Concat(Vm.Operations.History).Select(op => (JsonNode?)new JsonObject
        {
            ["name"] = op.Name,
            ["state"] = op.State.ToString(),
            ["shouldShow"] = op.ShouldShow,
            ["elapsedMs"] = op.Elapsed.TotalMilliseconds,
            ["processed"] = op.ProcessedBytes,
            ["total"] = op.TotalBytes,
            ["matches"] = op.Matches,
        })]);
        state["statusOperationsVisible"] = StatusOperations.Visibility == Visibility.Visible;
        state["statusOperationsText"] = StatusOperationsText.Text;
        state["statusOverflow"] = new JsonArray([.. StatusOverflowIds.Select(id => (JsonNode?)id)]);
        state["findBarProgress"] = FindBar.IsProgressVisible;
        state["backdrop"] = Appearance.AppliedBackdrop;
        state["culture"] = CultureInfo.CurrentCulture.Name;
        state["uiCulture"] = CultureInfo.CurrentUICulture.Name;
        state["flowDirection"] = Root.FlowDirection.ToString();
        state["actualTheme"] = Root.ActualTheme.ToString();
        state["windowSize"] = $"{AppWindow.Size.Width}x{AppWindow.Size.Height}";
        state["scale"] = Root.XamlRoot?.RasterizationScale ?? 1;

        // タイトルバーのウィンドウ操作ボタンの場所 (左右の余白。物理ピクセル。UI-02 の仕様 2、UI-44)。
        state["titleBarLeftInset"] = AppWindow.TitleBar.LeftInset;
        state["titleBarRightInset"] = AppWindow.TitleBar.RightInset;
    }

    /// <summary>要素の状態の追加の項目: ウィンドウの中の位置 (エピクセル)、書字方向、表示されているか。</summary>
    private void AddElementExtras(FrameworkElement e, JsonObject result)
    {
        if (e.XamlRoot is not null && e.ActualWidth > 0)
        {
            Rect bounds = e.TransformToVisual(null).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            result["left"] = bounds.X;
            result["top"] = bounds.Y;
            result["right"] = bounds.X + bounds.Width;
            result["bottom"] = bounds.Y + bounds.Height;
        }

        result["flowDirection"] = e.FlowDirection.ToString();
        result["effectivelyVisible"] = IsEffectivelyVisible(e);
        switch (e)
        {
            case TextBlock t:
                result["isTextTrimmed"] = t.IsTextTrimmed;
                break;
            case TextBox t:
                result["cautionBorder"] = t.BorderBrush is SolidColorBrush border
                    && Application.Current.Resources["SystemFillColorCautionBrush"] is SolidColorBrush caution
                    && border.Color == caution.Color;
                break;
            case ContentControl c when c.Content is string text:
                result["text"] = text;
                break;
        }
    }

    // ---- ドロップ (UI-34、ENG-12) ----

    /// <summary>
    /// ドロップと同じ処理を呼ぶ。<c>paths</c> はファイル・フォルダのパス、<c>virtual</c> はパスを持たない項目
    /// (<c>{"name", "base64"}</c>。ZIP の中のファイルなど)、<c>insertAt</c> はタブ列へのドロップの位置。
    /// 確認のダイアログを待たずに答える (テストは状態の表示で結果を待つ)。
    /// </summary>
    private async Task<JsonObject> TestDropAsync(JsonObject request)
    {
        var items = new List<IStorageItem>();
        foreach (JsonNode? node in request["paths"]?.AsArray() ?? [])
        {
            string path = node!.GetValue<string>();
            items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
        }

        foreach (JsonNode? node in request["virtual"]?.AsArray() ?? [])
        {
            byte[] content = Convert.FromBase64String(node!["base64"]!.GetValue<string>());
            items.Add(await StorageFile.CreateStreamedFileAsync(node["name"]!.GetValue<string>(), async request =>
            {
                // 書き終えたら出力のストリームを閉じてから要求を終える (先に要求を終えると、ストリームを閉じるときに例外になる)。
                try
                {
                    using (Stream output = request.AsStreamForWrite())
                    {
                        await output.WriteAsync(content);
                        await output.FlushAsync();
                    }

                    request.Dispose();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
                {
                    AppLog.Warning($"Test hooks: streamed file failed: {ex.Message}");
                    request.FailAndClose(StreamedFileFailureMode.Failed);
                }
            }, null));
        }

        int? insertAt = request["insertAt"] is { } at ? at.GetValue<int>() : null;
        _ = DropItemsAsync(items, insertAt);
        return new JsonObject { ["items"] = items.Count };
    }

    // ---- ウィンドウ・フォーカス ----

    /// <summary>ウィンドウの大きさ (物理ピクセル) を変える。前面には出さない。</summary>
    private JsonObject TestResizePhysical(JsonObject request)
    {
        AppWindow.Resize(new SizeInt32((int)TestHookSettings.ReadLong(request["width"], 1280), (int)TestHookSettings.ReadLong(request["height"], 800)));
        return new JsonObject { ["size"] = $"{AppWindow.Size.Width}x{AppWindow.Size.Height}" };
    }

    /// <summary>
    /// フォーカスを動かす。<c>target</c>: "editor" (Hex ビュー)、"next" / "previous" (Tab / Shift+Tab と同じ移動)、
    /// それ以外は AutomationId。<c>keyboard</c> が true (既定) ならキーボードで移したときと同じ扱い (フォーカスの枠を出す)。
    /// </summary>
    private JsonObject TestFocus(JsonObject request)
    {
        string target = request["target"]!.GetValue<string>();
        FocusState state = request["keyboard"]?.GetValue<bool>() == false ? FocusState.Programmatic : FocusState.Keyboard;
        bool moved = target switch
        {
            "editor" => CurrentView()?.Focus(state) ?? false,
            "next" or "previous" => FocusManager.TryMoveFocus(
                target == "next" ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous,
                new FindNextElementOptions { SearchRoot = Root.XamlRoot.Content }),
            _ => FindElement(target) is Control c && c.Focus(state),
        };
        object? focused = FocusManager.GetFocusedElement(Root.XamlRoot);
        var result = new JsonObject
        {
            ["moved"] = moved,
            ["focused"] = focused is FrameworkElement fe ? $"{fe.GetType().Name}:{AutomationProperties.GetAutomationId(fe)}" : focused?.GetType().Name,
        };
        if (focused is FrameworkElement element && element.ActualWidth > 0)
        {
            // フォーカスのある要素の表示範囲 (ウィンドウの中のエピクセル。フォーカスの枠の確認用。UI-52)。
            Rect bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            result["left"] = bounds.X;
            result["top"] = bounds.Y;
            result["right"] = bounds.X + bounds.Width;
            result["bottom"] = bounds.Y + bounds.Height;
            result["where"] = Where(element);
            result["focusState"] = (element as Control)?.FocusState.ToString();
        }

        return result;
    }

    /// <summary>
    /// タイトルバーの非クライアント領域 (UI-02 の仕様 5): ドラッグ領域 (Caption) と、入力を通す領域 (Passthrough) の矩形
    /// (クライアント領域の物理ピクセル)。
    /// </summary>
    private JsonObject TestNonClientRegions()
    {
        var source = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        JsonArray Rects(Microsoft.UI.Input.NonClientRegionKind kind) => new([.. source.GetRegionRects(kind).Select(r => (JsonNode?)new JsonObject
        {
            ["left"] = r.X,
            ["top"] = r.Y,
            ["right"] = r.X + r.Width,
            ["bottom"] = r.Y + r.Height,
        })]);
        return new JsonObject
        {
            ["caption"] = Rects(Microsoft.UI.Input.NonClientRegionKind.Caption),
            ["passthrough"] = Rects(Microsoft.UI.Input.NonClientRegionKind.Passthrough),
        };
    }

    // ---- 文字列 (UI-42、UI-46、UI-47) ----

    /// <summary>メニューバーのすべてのメニューと項目の表示名とアクセスキー (テスト用のメニューを除く)。</summary>
    private JsonObject TestMenuTexts()
    {
        var items = new JsonArray();
        void Add(MenuFlyoutItemBase item, string path)
        {
            switch (item)
            {
                case MenuFlyoutSubItem sub:
                    items.Add(new JsonObject { ["path"] = path + "/" + sub.Text, ["id"] = AutomationProperties.GetAutomationId(sub), ["text"] = sub.Text, ["accessKey"] = sub.AccessKey });
                    foreach (MenuFlyoutItemBase child in sub.Items)
                    {
                        Add(child, path + "/" + sub.Text);
                    }

                    break;
                case MenuFlyoutItem flyoutItem:
                    items.Add(new JsonObject
                    {
                        ["path"] = path + "/" + flyoutItem.Text,
                        ["id"] = AutomationProperties.GetAutomationId(flyoutItem),
                        ["text"] = flyoutItem.Text,
                        ["accessKey"] = flyoutItem.AccessKey,
                    });
                    break;
            }
        }

        foreach (MenuBarItem menu in MainMenu.Items.Where(m => AutomationProperties.GetAutomationId(m) != "TestMenu"))
        {
            items.Add(new JsonObject { ["path"] = menu.Title, ["id"] = AutomationProperties.GetAutomationId(menu), ["text"] = menu.Title, ["accessKey"] = menu.AccessKey, ["menu"] = true });
            foreach (MenuFlyoutItemBase item in menu.Items)
            {
                Add(item, menu.Title);
            }
        }

        return new JsonObject { ["items"] = items };
    }

    /// <summary>
    /// 表示中の文字列の切れと要素の重なり (UI-46 の受け入れ基準 2、UI-47 の仕様 3)。ウィンドウと開いているポップアップ
    /// (ダイアログ・フライアウト) を調べる。Hex ビューの中 (データの表示) は除く。
    /// <list type="bullet">
    /// <item>trimmed: <see cref="TextBlock.IsTextTrimmed"/> が true (省略記号で切れた)。</item>
    /// <item>clipped: 省略記号を使わずに、文字列が要素の幅 (折り返す場合は高さ) に収まっていない。</item>
    /// <item>overlaps: 同じパネルの兄弟の要素 (別のセル) の表示範囲が重なっている。</item>
    /// </list>
    /// 見つけたものはログにも記録する (開発版のログから切れた文字列の一覧を作る。UI-47 の仕様 3)。
    /// </summary>
    private JsonObject TestTextCheck()
    {
        var trimmed = new JsonArray();
        var clipped = new JsonArray();
        var overlaps = new JsonArray();
        var roots = new List<DependencyObject> { Root };
        roots.AddRange(VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Select(p => p.Child).Where(c => c is not null));
        foreach (DependencyObject root in roots)
        {
            Walk(root);
        }

        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed } or HexView)
            {
                return;
            }

            if (node is TextBlock text && text.Text.Length > 0 && text.ActualWidth > 0 && IsEffectivelyVisible(text))
            {
                if (text.IsTextTrimmed)
                {
                    trimmed.Add(Describe(text));
                    AppLog.Warning($"Text trimmed: \"{text.Text}\" ({Where(text)})");
                }
                else if (IsClipped(text))
                {
                    clipped.Add(Describe(text));
                    AppLog.Warning($"Text clipped: \"{text.Text}\" ({Where(text)})");
                }
            }

            if (node is Panel panel and (StackPanel or Grid or WrapPanel) && IsEffectivelyVisible(panel))
            {
                FindOverlaps(panel, overlaps);
            }

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }

        return new JsonObject { ["trimmed"] = trimmed, ["clipped"] = clipped, ["overlaps"] = overlaps };
    }

    private static JsonObject Describe(TextBlock text)
    {
        Rect bounds = text.TransformToVisual(null).TransformBounds(new Rect(0, 0, text.ActualWidth, text.ActualHeight));
        return new JsonObject
        {
            ["text"] = text.Text,
            ["where"] = Where(text),
            ["width"] = text.ActualWidth,
            ["naturalWidth"] = NaturalSize(text, double.PositiveInfinity).Width,
            ["slots"] = string.Join(" < ", SlotChain(text)),
            ["left"] = bounds.X,
            ["top"] = bounds.Y,
        };
    }

    /// <summary>要素の場所: AutomationId のある祖先をたどった道筋。</summary>
    private static string Where(DependencyObject element)
    {
        var path = new List<string>();
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement fe && AutomationProperties.GetAutomationId(fe) is { Length: > 0 } id)
            {
                path.Add(id);
            }
        }

        path.Reverse();
        return string.Join("/", path);
    }

    /// <summary>
    /// 文字列が表示しきれていないか: 本来の大きさ (同じ書式の新しい TextBlock で測る) が表示している大きさに収まらない、
    /// または割り当てられた領域で切り取られている。
    /// </summary>
    private static bool IsClipped(TextBlock text)
    {
        if (text.TextWrapping == TextWrapping.NoWrap)
        {
            if (NaturalSize(text, double.PositiveInfinity).Width > text.ActualWidth + 2)
            {
                return true;
            }
        }
        else if (NaturalSize(text, text.ActualWidth + 1).Height > text.ActualHeight + 2)
        {
            return true;
        }

        // 割り当てられた領域 (レイアウトのスロット) より文字列が長いため切り取られた (幅を固定したボタンの中など)。
        // 文字列から、それを含む最も近いコントロールまでの要素を調べる。折り返す文字列は高さで調べた。
        if (text.TextWrapping != TextWrapping.NoWrap)
        {
            return false;
        }

        double natural = NaturalSize(text, double.PositiveInfinity).Width;
        for (DependencyObject? node = text; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement element && LayoutInformation.GetLayoutSlot(element) is { Width: > 0 } slot
                && natural > slot.Width - element.Margin.Left - element.Margin.Right + 2)
            {
                return true;
            }

            if (node is Control)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>文字列から最も近いコントロールまでの要素の、レイアウトのスロットの幅 (切れの報告の手がかり)。</summary>
    private static IEnumerable<string> SlotChain(TextBlock text)
    {
        for (DependencyObject? node = text; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement element)
            {
                yield return $"{element.GetType().Name}:{LayoutInformation.GetLayoutSlot(element).Width:F0}";
            }

            if (node is Control)
            {
                yield break;
            }
        }
    }

    /// <summary>同じ書式の新しい TextBlock で測った文字列の大きさ。</summary>
    private static Size NaturalSize(TextBlock text, double width)
    {
        var probe = new TextBlock
        {
            Text = text.Text,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontWeight = text.FontWeight,
            FontStyle = text.FontStyle,
            FontStretch = text.FontStretch,
            CharacterSpacing = text.CharacterSpacing,
            TextWrapping = text.TextWrapping,
            LineHeight = text.LineHeight,
            LineStackingStrategy = text.LineStackingStrategy,
            OpticalMarginAlignment = text.OpticalMarginAlignment,
            TextLineBounds = text.TextLineBounds,
            Padding = text.Padding,
        };
        probe.Measure(new Size(width, double.PositiveInfinity));
        return probe.DesiredSize;
    }

    /// <summary>パネルの兄弟の要素 (グリッドでは別のセル) の表示範囲の重なり。</summary>
    private static void FindOverlaps(Panel panel, JsonArray overlaps)
    {
        var children = panel.Children.OfType<FrameworkElement>()
            .Where(c => c.Visibility == Visibility.Visible && c.ActualWidth > 1 && c.ActualHeight > 1 && c.Opacity > 0)
            .Select(c => (Element: c, Bounds: c.TransformToVisual(panel).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight))))
            .ToList();
        for (int i = 0; i < children.Count; i++)
        {
            for (int j = i + 1; j < children.Count; j++)
            {
                (FrameworkElement a, Rect ra) = children[i];
                (FrameworkElement b, Rect rb) = children[j];
                if (panel is Grid && (Grid.GetRow(a) == Grid.GetRow(b) && Grid.GetColumn(a) == Grid.GetColumn(b)
                    || Grid.GetRowSpan(a) > 1 || Grid.GetColumnSpan(a) > 1 || Grid.GetRowSpan(b) > 1 || Grid.GetColumnSpan(b) > 1))
                {
                    continue; // 同じセルに重ねるのは意図した配置。
                }

                Rect overlap = ra;
                overlap.Intersect(rb);
                if (!overlap.IsEmpty && overlap.Width > 1 && overlap.Height > 1)
                {
                    overlaps.Add(new JsonObject
                    {
                        ["a"] = Where(a) + ":" + a.GetType().Name + ":" + TextOf(a),
                        ["b"] = Where(b) + ":" + b.GetType().Name + ":" + TextOf(b),
                        ["width"] = overlap.Width,
                        ["height"] = overlap.Height,
                    });
                    AppLog.Warning($"Overlap: {Where(a)} {a.GetType().Name} / {Where(b)} {b.GetType().Name}");
                }
            }
        }
    }

    private static string TextOf(FrameworkElement e) => e switch
    {
        TextBlock t => t.Text,
        ContentControl { Content: string s } => s,
        _ => string.Empty,
    };

    /// <summary>要素と祖先がすべて表示されているか。</summary>
    private static bool IsEffectivelyVisible(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: 0 })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 切れの検出の確認用 (UI-47 の受け入れ基準 2): 幅を固定したボタンをスタートページに置く。文字列はリソースのキーで指定する。
    /// </summary>
    private JsonObject TestAddProbe(JsonObject request)
    {
        string key = request["key"]?.GetValue<string>() ?? "Startup_ExportSettings";
        var button = new Button { Content = Loc.Get(key), Width = TestHookSettings.ReadLong(request["width"], 60) };
        AutomationProperties.SetAutomationId(button, "TestProbe");
        StartPage.AddExtra(button);

        return new JsonObject { ["text"] = (string)button.Content };
    }

    // ---- 検索バー・データソース (FIND-02) ----

    /// <summary>検索欄のキー (Enter / Shift+Enter / Esc)。検索の完了を待たずに答える。</summary>
    private JsonObject TestFindKey(JsonObject request)
    {
        var key = Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true);
        _ = FindBar.HandleQueryKeyAsync(key, request["shift"]?.GetValue<bool>() ?? false);
        return new JsonObject();
    }

    /// <summary>選択中のタブのデータソースの読み込みの記録 (異常を再現するデータソースのときだけ)。</summary>
    private JsonObject TestSourceStats()
    {
        IByteSource? source = Vm.Selected?.Document.Source;
        if (source is not FaultyByteSource faulty)
        {
            return new JsonObject { ["faulty"] = false };
        }

        long last = faulty.LastReadStartedTimestamp;
        return new JsonObject
        {
            ["faulty"] = true,
            ["readCount"] = faulty.ReadCount,
            ["msSinceLastRead"] = last == 0 ? null : Stopwatch.GetElapsedTime(last).TotalMilliseconds,
        };
    }
}
#endif
