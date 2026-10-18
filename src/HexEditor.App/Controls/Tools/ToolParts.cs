using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Expressions;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App.Controls.Tools;

/// <summary>
/// 電卓・変換・参照ツール (TOOL-22〜TOOL-28) のパネルが、アクティブな文書とアプリの機能 (書き込み・検索バー・移動) を使うための口。
/// パネルは <see cref="MainWindow"/> を直接は知らず、この口だけを使う (ウィンドウごとに 1 つ)。
/// </summary>
internal sealed class ToolHost
{
    /// <summary>アクティブな文書。なければ null。</summary>
    public required Func<DocumentViewModel?> Document { get; init; }

    /// <summary>バイト列をカーソル位置 (選択範囲があれば先頭) に書き込む。成功なら true。<c>followMode</c> なら挿入モードのとき挿入する。</summary>
    public required Func<byte[], string, bool, bool> Write { get; init; }

    /// <summary>検索バーを開き、Hex の検索条件にする。</summary>
    public required Action<byte[]> SendToSearch { get; init; }

    /// <summary>オフセットへ移動する。</summary>
    public required Action<long> GoTo { get; init; }

    /// <summary>編集できるか (読み取り専用・処理中でないか)。</summary>
    public required Func<bool> CanEdit { get; init; }

    /// <summary>通知を出す。</summary>
    public required Action<string, bool> Notify { get; init; }

    /// <summary>アクティブな文書が変わった、またはカーソル・選択範囲・内容が変わった。</summary>
    public event EventHandler? Changed;

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public EditorState? Editor => Document()?.Editor;

    /// <summary>ドキュメントのエンディアン (VIEW-11)。ドキュメントがなければ false。</summary>
    public bool BigEndian => Editor?.View.BigEndian == true;

    /// <summary>
    /// カーソル位置から (選択範囲があれば選択範囲を) 最大 <paramref name="maxBytes"/> バイト読む。
    /// <paramref name="length"/> を指定すると、その長さを読む (選択範囲は使わない)。読めなければ null。
    /// </summary>
    public byte[]? ReadAtCursor(int maxBytes, int? length = null)
    {
        if (Editor is not { } editor)
        {
            return null;
        }

        long start = editor.HasSelection && length is null ? editor.SelectionStart : editor.Cursor;
        long available = editor.Document.Length - start;
        long want = length ?? (editor.HasSelection ? Math.Min(editor.SelectionLength, maxBytes) : maxBytes);
        int count = (int)Math.Clamp(Math.Min(want, available), 0, maxBytes);
        byte[] bytes = new byte[count];
        if (count > 0 && !editor.Document.Current.Read(start, bytes).IsComplete)
        {
            return null;
        }

        return bytes;
    }
}

/// <summary>パネルの部品を作る補助。</summary>
internal static class ToolParts
{
    public static readonly FontFamily Mono = DialogParts.Mono;

    public static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>見出し付きの 1 行入力欄(左から右、等幅)。</summary>
    public static TextBox Box(string id, string header, bool readOnly = false, bool mono = true)
    {
        TextBox box = DialogParts.Field(id, header, string.Empty, mono);
        box.IsReadOnly = readOnly;
        box.MinWidth = 160;
        return box;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBox, StrongBox> s_programmatic = new();

    private sealed class StrongBox
    {
        public string? Value;
    }

    /// <summary>
    /// プログラムが入れる文字列。TextChanged は文字を入れた後で非同期に届くため、利用者の入力と区別できるよう、入れた文字列を覚える
    /// (<see cref="IsProgrammatic"/>)。他の欄の更新が、その欄の入力として処理し直されるのを防ぐ。
    /// </summary>
    public static void SetText(TextBox box, string text)
    {
        s_programmatic.GetOrCreateValue(box).Value = text;
        box.Text = text;
    }

    /// <summary>欄の今の文字列が、プログラムが入れたもの(利用者が変えていない)か。</summary>
    public static bool IsProgrammatic(TextBox box) => s_programmatic.TryGetValue(box, out StrongBox? expected) && expected.Value == box.Text;

    /// <summary>コピーのボタン(アイコンだけ。名前と ToolTip は <paramref name="name"/>)。</summary>
    public static Button CopyButton(string id, string name, Func<string> text)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => Copy(text());
        s_copyButtons[id] = new WeakReference<Button>(button);
        return button;
    }

    private static readonly Dictionary<string, WeakReference<Button>> s_copyButtons = [];

    /// <summary>コピーのボタン(AutomationId)を押したのと同じ処理(テスト用)。</summary>
    internal static void InvokeCopyButton(string id)
    {
        if (s_copyButtons.TryGetValue(id, out WeakReference<Button>? weak) && weak.TryGetTarget(out Button? button))
        {
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button)).Invoke();
            return;
        }

        throw new InvalidOperationException($"Copy button not found: {id}");
    }

    /// <summary>システムのクリップボードに文字列を置く。</summary>
    public static void Copy(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        SystemClipboard.SetContent(package);
    }

    public static Button TextButton(string id, string key, Action click)
    {
        var button = new Button { Content = Loc.Get(key) };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>ラベル、値(選択できる文字列)、コピーのボタンの 1 行。</summary>
    public static (Grid Row, TextBlock Value) ValueRow(string id, string labelKey, string copyId, Func<string> copyText)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string label = Loc.Get(labelKey);
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.WrapWholeWords };
        var value = new TextBlock
        {
            IsTextSelectionEnabled = true,
            FontFamily = Mono,
            FlowDirection = FlowDirection.LeftToRight,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAutomationId(value, id);
        AutomationProperties.SetName(value, label);
        Button copy = CopyButton(copyId, Loc.Format("Tool_CopyValue", label), copyText);
        Grid.SetColumn(value, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(name);
        grid.Children.Add(value);
        grid.Children.Add(copy);
        return (grid, value);
    }

    /// <summary>入力式の誤りの説明 (入力欄の下)。</summary>
    public static TextBlock ErrorText(string id)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("SystemFillColorCriticalBrush"),
            Visibility = Visibility.Collapsed,
            FlowDirection = FlowDirection.LeftToRight,
        };
        AutomationProperties.SetAutomationId(text, id);
        return text;
    }

    public static void ShowError(TextBlock text, TextBox? box, string? message)
    {
        text.Text = message ?? string.Empty;
        text.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        if (box is not null)
        {
            DialogParts.MarkInvalid(box, !string.IsNullOrEmpty(message));
        }
    }

    /// <summary>パネルの外枠(背景、余白、スクロール)。</summary>
    public static ScrollViewer Frame(StackPanel body)
    {
        body.Spacing = 8;
        body.Padding = new Thickness(12);
        return new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    /// <summary>入力式の誤りの説明文(移動バーと同じ文言)。</summary>
    public static string Describe(ExpressionException error) => DialogParts.ExpressionError(error);
}

/// <summary>ツールのパネルの共通の土台: 背景、F6 の移動先、アクティブな文書の変化の購読。</summary>
internal abstract class ToolPanelBase : UserControl, Panels.IPanelContent
{
    protected ToolPanelBase(ToolHost host)
    {
        Host = host;
        Background = ToolParts.ThemeBrush("LayerFillColorDefaultBrush");
        Loaded += (_, _) =>
        {
            Host.Changed += OnHostChanged;
            OnHostChanged(this, EventArgs.Empty);
        };
        Unloaded += (_, _) => Host.Changed -= OnHostChanged;
    }

    protected ToolHost Host { get; }

    /// <summary>最初にフォーカスを受ける部品。</summary>
    protected abstract Control FirstFocus { get; }

    /// <summary>アクティブな文書・カーソル・内容が変わった。</summary>
    protected virtual void OnHostChanged(object? sender, EventArgs e)
    {
    }

    public bool FocusContent() => FirstFocus.Focus(FocusState.Programmatic);
}
