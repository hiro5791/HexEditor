using System.Collections.Specialized;
using System.Globalization;
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Editing;
using HexEditor.Core.Saving;
using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Editing.TransformView;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Plugins;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HexEditor.App;

/// <summary>
/// 変換ビュー (EDIT-45): データを変えずに XOR などの変換をかけて表示し、表示上の入力は逆変換して元のデータに書く。変換はドキュメントに付け
/// (<see cref="Document.SetViewTransform"/>)、検索・インスペクタ・ハッシュ・テンプレートなど Hex ビューに付随する機能は表示どおりのデータを読む。
/// 適用中の変換はドキュメントに付随するデータ (00-overview 10 章) に保存し、開き直すと戻す (仕様 14)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>ドキュメントに付随するデータの種類 (適用中の変換)。</summary>
    private const string TransformDataKind = "transform";

    /// <summary>テスト用: 最後に開いた「XOR して表示…」のフライアウト。</summary>
    internal Flyout? TransformXorFlyout { get; private set; }

    private void RegisterTransformViewCommands()
    {
        string? Active(DocumentViewModel d) => d.Document.ViewTransform is null ? Loc.Get("TransformView_NotActive") : null;
        string? Allowed(DocumentViewModel d) => d.Document.LinkedParent is not null ? Loc.Get("TransformView_LinkedView") : null;
        Commands.Register("data.transformView.define", () => DefineTransformViewAsync(), () => NeedsDocument(Allowed));
        Commands.Register("data.transformView.xor", ShowXorFlyout, () => NeedsDocument(Allowed));
        Commands.Register("data.transformView.clear", () =>
        {
            if (Vm.Selected is { } doc)
            {
                ClearTransformView(doc);
            }
        }, () => NeedsDocument(Active));
        Commands.Register("data.transformView.applyToData", () => ApplyTransformToDataAsync(),
            () => NeedsDocument(d => Active(d) ?? (d.Document.ViewTransform is ViewTransformChain { CanInverse: false } ? Loc.Get("TransformView_ReadOnlyNotice") : null)));
        Vm.Documents.CollectionChanged += TransformView_DocumentsChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                Vm.Documents.CollectionChanged -= TransformView_DocumentsChanged;
            }
        };
    }

    /// <summary>プラグインの変換を ID から引く (変換の定義の <see cref="TransformOpKind.Plugin"/>)。</summary>
    internal static IPluginTransform? PluginTransform(string id) => PluginIntegration.TransformView(id);

    private static string? PluginTransformName(string id) =>
        PluginIntegration.TransformViews().FirstOrDefault(t => t.Registration.Id == id).Registration?.DisplayName;

    /// <summary>
    /// 長さを変えられないという文を、変換ビューが理由なら変換ビューの文にする (EDIT-45 の仕様 8:「変換ビューでは長さを変える編集はできません」)。
    /// 通知とコマンドの使えない理由の両方に使う。
    /// </summary>
    private static string TransformViewNotice(string message, DocumentViewModel? doc) =>
        doc?.Document.ViewTransform is not null && message == Loc.Get("Notice_FixedLength") && doc.Document.LinkedParent is null
            && doc.Document.Source.Capabilities.HasFlag(SourceCapabilities.CanResize)
            ? Loc.Get("TransformView_LengthNotice")
            : message;

    /// <summary>変換ビューを設定する (null で解除)。表示を更新し、ドキュメントに付随するデータに記録する。</summary>
    internal void SetTransformView(DocumentViewModel doc, TransformDefinition? definition)
    {
        IViewTransform? transform;
        try
        {
            transform = definition?.Create(PluginTransform, PluginTransformName);
        }
        catch (ArgumentException)
        {
            ShowNotice(Loc.Get("TransformView_Error_PluginMissing"), InfoBarSeverity.Error, doc);
            return;
        }

        doc.Document.SetViewTransform(transform);
        SaveTransformData(doc, definition);
        UpdateStatusBarLayout();
        Commands.NotifyStatesChanged();
    }

    internal void ClearTransformView(DocumentViewModel doc) => SetTransformView(doc, null);

    private static TransformDefinition? DefinitionOf(DocumentViewModel doc) => (doc.Document.ViewTransform as ViewTransformChain)?.Definition;

    // ---- 変換の定義ダイアログ ----

    private async Task DefineTransformViewAsync()
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        IReadOnlyList<(string Id, string Name)> plugins = [.. PluginIntegration.TransformViews().Select(t => (t.Registration.Id, t.Registration.DisplayName))];
        var panel = new TransformViewPanel(doc.Editor, DefinitionOf(doc), plugins);
        ContentDialog dialog = DialogParts.Dialog(Root, "TransformViewDialog", Loc.Get("TransformView_Title"),
            new ScrollViewer { Content = panel, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) }, Loc.Get("TransformView_Apply"),
            doc.Document.ViewTransform is null ? null : Loc.Get("TransformView_ClearButton"));
        TransformDefinition? definition = null;
        void Validate()
        {
            definition = panel.TryGetDefinition();
            dialog.IsPrimaryButtonEnabled = definition is not null;
        }

        panel.Changed += (_, _) => Validate();
        Validate();
        ContentDialogResult result = await ShowEditDialogAsync(dialog);
        if (result == ContentDialogResult.Primary && definition is not null)
        {
            SetTransformView(doc, definition);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            ClearTransformView(doc);
        }

        FocusEditor();
    }

    // ---- 「XOR して表示…」のフライアウト (仕様 4) ----

    private void ShowXorFlyout()
    {
        if (Vm.Selected is not { } doc || CurrentView() is not { } view)
        {
            return;
        }

        TransformDefinition? current = DefinitionOf(doc);
        byte[]? currentKey = current?.SingleXorKey;
        var key = DialogParts.Field("TransformXor_Key", Loc.Get("TransformView_Key"),
            currentKey is null ? string.Empty : string.Join(' ', currentKey.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))));
        var origin = DialogParts.Field("TransformXor_Origin", Loc.Get("TransformView_OriginOffset"),
            current is { KeyOrigin: TransformKeyOrigin.Offset } c ? RangeSelectionModel.Format(c.KeyOriginOffset) : "0");
        var error = DialogParts.Caption("TransformXor_Error");
        error.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var ok = new Button { Content = Loc.Get("Common_Ok"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = false };
        AutomationProperties.SetAutomationId(ok, "TransformXor_Ok");
        var panel = new StackPanel { Spacing = 8, MinWidth = 260 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("TransformView_XorTitle"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(key);
        panel.Children.Add(origin);
        panel.Children.Add(error);
        panel.Children.Add(ok);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.TopEdgeAlignedLeft };
        AutomationProperties.SetAutomationId(panel, "TransformXorFlyout");
        TransformDefinition? Parse()
        {
            byte[]? bytes = HexText.TryParse(key.Text);
            string? message = bytes is null
                ? Loc.Get(key.Text.Trim().Length == 0 ? "TransformView_Error_KeyEmpty" : "TransformView_Error_KeyHex")
                : bytes.Length > TransformDefinition.MaxKeyLength ? Loc.Get("TransformView_Error_KeyTooLong") : null;
            long offset = 0;
            bool originBad = false;
            try
            {
                offset = ExpressionEvaluator.Evaluate(origin.Text, new EditorExpressionContext(doc.Editor));
            }
            catch (ExpressionException)
            {
                originBad = true;
                message ??= Loc.Get("TransformView_Error_Origin");
            }

            DialogParts.MarkInvalid(key, bytes is null || bytes.Length > TransformDefinition.MaxKeyLength);
            DialogParts.MarkInvalid(origin, originBad);
            error.Text = message ?? string.Empty;
            return message is null ? TransformDefinition.Xor(bytes!, offset == 0 ? TransformKeyOrigin.DocumentStart : TransformKeyOrigin.Offset, offset) : null;
        }

        void Validate() => ok.IsEnabled = Parse() is not null;
        void Confirm()
        {
            if (Parse() is { } definition)
            {
                flyout.Hide();
                SetTransformView(doc, definition);
            }
        }

        key.TextChanged += (_, _) => Validate();
        origin.TextChanged += (_, _) => Validate();
        ok.Click += (_, _) => Confirm();
        foreach (TextBox box in new[] { key, origin })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    Confirm();
                }
            };
        }

        flyout.Opened += (_, _) => key.Focus(FocusState.Programmatic);
        flyout.Closed += (_, _) => FocusEditor();
        Validate();
        TransformXorFlyout = flyout;
        flyout.ShowAt(view);
    }

    // ---- 「この変換をデータに適用…」(仕様 13) ----

    /// <summary>
    /// 変換ビューの変換を実際のデータに書き込む。演算が 1 つでデータ演算で表せるものは、データ演算 (EDIT-31〜35) のダイアログを同じ演算・鍵・範囲で開き、
    /// 確定すると変換ビューを解除してから実行する。演算をつないだ変換などは、確認してから変換をそのままデータにかける。
    /// </summary>
    private async Task ApplyTransformToDataAsync()
    {
        if (Vm.Selected is not { } doc || DefinitionOf(doc) is not { } definition || doc.Document.ViewTransform is not ViewTransformChain chain)
        {
            return;
        }

        IReadOnlyList<TargetRange> ranges = definition.RangeStart is { } s
            ? [new TargetRange(s, Math.Min(definition.RangeLength ?? 0, Math.Max(0, doc.Document.Length - s)))]
            : [new TargetRange(0, doc.Document.Length)];
        if (DataOperationPresetOf(definition) is { } preset)
        {
            var panel = new DataOperationPanel(doc.Editor, [], preset.Spec.Kind, preset.Spec.BigEndian);
            panel.Preset(preset.Spec, preset.KeyHex, definition.RangeStart is null ? null : (ranges[0].Offset, ranges[0].Length));
            ContentDialog dialog = DialogParts.Dialog(Root, "DataOperationDialog", Loc.Get("DataOp_Title"),
                new ScrollViewer { Content = panel, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) }, Loc.Get("DataOp_Run"));
            DataOperationRequest? request = null;
            void Validate()
            {
                request = panel.TryGetRequest();
                dialog.IsPrimaryButtonEnabled = request is not null;
            }

            panel.Changed += (_, _) => Validate();
            Validate();
            if (await ShowEditDialogAsync(dialog) != ContentDialogResult.Primary || request is null)
            {
                return;
            }

            // 確定したら変換ビューを解除してから実行する (表示上の値ではなく元のデータに演算をかける)。
            ClearTransformView(doc);
            await RunDataOperationAsync(doc, request);
            FocusEditor();
            return;
        }

        ContentDialog confirm = DialogParts.Dialog(Root, "TransformApplyDialog", Loc.Get("TransformView_ApplyTitle"),
            new TextBlock { Text = Loc.Format("TransformView_ApplyConfirm", chain.Label), TextWrapping = TextWrapping.Wrap }, Loc.Get("DataOp_Run"));
        if (await ShowEditDialogAsync(confirm) != ContentDialogResult.Primary)
        {
            return;
        }

        ClearTransformView(doc);
        DataOperationRunner runner = DataOperationRunner.For(doc.Document, TestHooks.Volumes ?? SystemVolumeInfoProvider.Instance);
        DocumentSnapshot snapshot = doc.Document.Current;
        DataOperationResult? result = await RunTransformAsync(doc, Loc.Get("TransformView_ApplyTitle"), DataOperationRunner.TotalBytes(ranges),
            DataOperationRunner.IsLongRunning(ranges), op => runner.RunTransform(snapshot, ranges, (span, offset) => chain.Forward(offset, span), op));
        if (result is not null && !ApplyEdit(doc, () => TransformApplier.Apply(doc.Document, result.Replacements, "データ演算")))
        {
            TransformApplier.DisposeAll(result.Replacements);
        }

        FocusEditor();
    }

    /// <summary>データ演算のダイアログで表せる変換ならその設定 (演算が 1 つ、鍵の起点が 0 か範囲の先頭)。</summary>
    private static (DataOperationSpec Spec, string? KeyHex)? DataOperationPresetOf(TransformDefinition definition)
    {
        if (definition.Ops is not [var op] || definition.KeyOrigin == TransformKeyOrigin.Offset && definition.KeyOriginOffset != 0)
        {
            return null;
        }

        DataOperationKind? kind = op.Kind switch
        {
            TransformOpKind.Xor => DataOperationKind.Xor,
            TransformOpKind.Add => DataOperationKind.Add,
            TransformOpKind.Subtract => DataOperationKind.Subtract,
            TransformOpKind.Not => DataOperationKind.Not,
            TransformOpKind.RotateLeft => DataOperationKind.RotateLeft,
            TransformOpKind.RotateRight => DataOperationKind.RotateRight,
            TransformOpKind.ByteSwap16 => DataOperationKind.ByteSwap16,
            TransformOpKind.ByteSwap32 => DataOperationKind.ByteSwap32,
            TransformOpKind.ByteSwap64 => DataOperationKind.ByteSwap64,
            TransformOpKind.ReverseBits => DataOperationKind.ReverseBits,
            TransformOpKind.SwapNibbles => DataOperationKind.SwapNibbles,
            _ => null,
        };
        if (kind is not { } k)
        {
            return null;
        }

        var spec = new DataOperationSpec
        {
            Kind = k,
            Size = op.EffectiveSize,
            BigEndian = op.BigEndian,
            Operand = op.Operand,
            Increment = op.Increment,
            ShiftBits = op.Bits,
            OperandSource = op.Key is null ? OperandSource.Number : OperandSource.KeyBytes,
            Key = op.Key,

            // 変換ビューの「オフセット 0 から数える」はデータ演算の「オフセット 0 から数える」、「範囲の先頭から」は同じ名前の起点。
            KeyOrigin = definition.KeyOrigin == TransformKeyOrigin.RangeStart ? Core.Editing.PatternOrigin.RangeStart : Core.Editing.PatternOrigin.OffsetZero,
        };
        string? hex = op.Key is { } key ? string.Join(' ', key.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) : null;
        return (spec, hex);
    }

    // ---- ドキュメントに付随するデータ (仕様 14) ----

    private void SaveTransformData(DocumentViewModel doc, TransformDefinition? definition)
    {
        if (doc.Document.Source is not FileByteSource { Path: { } path } file)
        {
            return;
        }

        try
        {
            if (definition is null)
            {
                _documentData.Delete(path, TransformDataKind);
            }
            else
            {
                _documentData.WriteObject(path, TransformDataKind, file.Stamp, new JsonObject { ["definition"] = definition.ToJson() });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"The transform view could not be saved: {ex.GetType().Name}");
        }
    }

    private void TransformView_DocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DocumentViewModel doc in e.NewItems?.OfType<DocumentViewModel>() ?? [])
        {
            if (doc.Document.Source is FileByteSource { Path: { } path } && doc.Document.LinkedParent is null
                && _documentData.ReadObject(path, TransformDataKind) is { } data
                && TransformDefinition.FromJson(data.Value["definition"]) is { } definition)
            {
                try
                {
                    doc.Document.SetViewTransform(definition.Create(PluginTransform, PluginTransformName));
                }
                catch (ArgumentException)
                {
                    // 変換に使うプラグインがない: 元のデータを表示する。
                }
            }
        }
    }

    /// <summary>テスト用: 変換ビューの状態 (定義・バッジ・ステータスバー)。</summary>
    internal JsonObject TransformViewTestModel()
    {
        DocumentViewModel? doc = Vm.Selected;
        (string badge, bool frame) = CurrentView()?.TransformIndicatorForTest() ?? (string.Empty, false);
        return new JsonObject
        {
            ["active"] = doc?.Document.ViewTransform is not null,
            ["label"] = doc?.Document.ViewTransform?.Label,
            ["definition"] = doc is null ? null : DefinitionOf(doc)?.ToJson(),
            ["badge"] = badge,
            ["frame"] = frame,
            ["status"] = StatusTransform.Visibility == Visibility.Visible ? StatusTransform.Content as string ?? string.Empty : string.Empty,
            ["readOnly"] = doc?.Document.IsTransformReadOnly ?? false,
        };
    }

    private void StatusTransform_Click(object sender, RoutedEventArgs e) => _ = Commands.ExecuteAsync("data.transformView.define");
}
