using System.Text;
using HexEditor.App.Services;
using HexEditor.Core.Disassembly.Demangling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App.Controls;

/// <summary>
/// 「デマングル」のページ (06 の ANA-30 の画面。タブで開く。モードレス): 上に入力欄 (複数行、1 行に 1 つ)、下に結果欄、オプション
/// (型情報を省いた短い名前、Rust のハッシュを表示)、「コピー」。入力に合わせてその場で変換し、1 万行を超える入力はバックグラウンドで変換する。
/// </summary>
public sealed partial class DemanglePage : UserControl
{
    private readonly TextBox _input;
    private readonly TextBox _output;
    private readonly CheckBox _short;
    private readonly CheckBox _hash;
    private readonly TextBlock _status;
    private CancellationTokenSource? _running;
    private int _version;

    public DemanglePage()
    {
        AutomationProperties.SetAutomationId(this, "DemanglePage");
        AutomationProperties.SetName(this, Loc.Get("ToolPage_Demangle"));
        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = DialogParts.Mono,
            FlowDirection = FlowDirection.LeftToRight,
            Header = Loc.Get("Demangle_Input"),
            PlaceholderText = Loc.Get("Demangle_InputPlaceholder"),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_input, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_input, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(_input, "Demangle_Input");
        _output = new TextBox
        {
            AcceptsReturn = true,
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = DialogParts.Mono,
            FlowDirection = FlowDirection.LeftToRight,
            Header = Loc.Get("Demangle_Output"),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_output, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(_output, ScrollBarVisibility.Auto);
        AutomationProperties.SetAutomationId(_output, "Demangle_Output");
        _short = DialogParts.Check("Demangle_ShortNames", Loc.Get("Demangle_ShortNames"), false);
        _hash = DialogParts.Check("Demangle_RustHash", Loc.Get("Demangle_RustHash"), false);
        var copy = new Button { Content = Loc.Get("Demangle_Copy") };
        AutomationProperties.SetAutomationId(copy, "Demangle_Copy");
        copy.Click += (_, _) =>
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(_output.Text);
            SystemClipboard.SetContent(package);
        };
        _status = DialogParts.Caption("Demangle_Status");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _short, _hash, copy } };
        var grid = new Grid { Padding = new Thickness(16), RowSpacing = 8, MaxWidth = 1200, HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(_input);
        Grid.SetRow(options, 1);
        grid.Children.Add(options);
        Grid.SetRow(_output, 2);
        grid.Children.Add(_output);
        Grid.SetRow(_status, 3);
        grid.Children.Add(_status);
        Content = grid;
        _input.TextChanged += (_, _) => _ = ConvertAsync();
        _short.Click += (_, _) => _ = ConvertAsync();
        _hash.Click += (_, _) => _ = ConvertAsync();
    }

    public void SetInput(string text) => _input.Text = text;

    public void FocusInput() => DispatcherQueue.TryEnqueue(() => _input.Focus(FocusState.Programmatic));

    /// <summary>結果欄の文字列 (テスト用)。</summary>
    internal string OutputText => _output.Text;

    /// <summary>変換が終わるのを待つ (テスト用)。</summary>
    internal Task? Converting { get; private set; }

    private Task ConvertAsync()
    {
        Converting = ConvertCoreAsync();
        return Converting;
    }

    private async Task ConvertCoreAsync()
    {
        int version = ++_version;
        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        string text = _input.Text;
        var options = new DemangleOptions { ShortNames = _short.IsChecked == true, ShowRustHash = _hash.IsChecked == true };
        string cannot = Loc.Get("Demangle_Failed");
        int lines = text.Count(c => c is '\n' or '\r') + 1;
        string result;
        try
        {
            if (lines > Demangler.BackgroundLineCount)
            {
                _status.Text = Loc.Get("Demangle_Converting");
                result = await Task.Run(() => Format(Demangler.DemangleLines(text, options, cts.Token), cannot), cts.Token);
            }
            else
            {
                result = Format(Demangler.DemangleLines(text, options, cts.Token), cannot);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version == _version)
        {
            _output.Text = result;
            _status.Text = string.Empty;
        }
    }

    /// <summary>結果の行 (不正な名前は「デマングルできません」と元の文字列。ANA-30 の「エラー」)。</summary>
    private static string Format(IReadOnlyList<DemangleResult> results, string cannot)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < results.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\r');
            }

            DemangleResult r = results[i];
            sb.Append(r.Invalid ? string.Format(System.Globalization.CultureInfo.CurrentCulture, cannot, r.Text) : r.Text);
        }

        return sb.ToString();
    }
}
