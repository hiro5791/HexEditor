using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.App.ViewModels;
using HexEditor.Core.Scripting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>スクリプトコンソール (AUTO-23) の表示。処理は <see cref="ConsolePanelViewModel"/>。</summary>
public sealed partial class ConsolePanel : UserControl, Panels.IPanelContent
{
    private readonly IScriptPanelHost _host;

    public ConsolePanel(ConsolePanelViewModel vm, IScriptPanelHost host)
    {
        Vm = vm;
        _host = host;
        InitializeComponent();
        foreach ((Control control, string key) in new (Control, string)[]
        {
            (ResetButton, "Console_Reset"), (ClearButton, "Console_Clear"), (LanguageBox, "Console_LanguageLabel"), (Input, "Console_InputLabel"),
        })
        {
            AutomationProperties.SetName(control, Loc.Get(key));
            ToolTipService.SetToolTip(control, Loc.Get(key));
        }

        Input.PlaceholderText = Loc.Get("Console_InputLabel");
        AutomationProperties.SetName(List, Loc.Get("Console_ResultsLabel"));
        // 言語名は翻訳しない固有名なので、コードで足す。
        LanguageBox.Items.Add(new ComboBoxItem { Content = "JavaScript", Tag = "js" });
        LanguageBox.Items.Add(new ComboBoxItem { Content = "C#", Tag = "csx" });
        LanguageBox.SelectedIndex = vm.Language == ScriptLanguage.CSharp ? 1 : 0;
        vm.EntryAdded += Vm_EntryAdded;
        Unloaded += (_, _) => vm.EntryAdded -= Vm_EntryAdded;
    }

    public ConsolePanelViewModel Vm { get; }

    bool Panels.IPanelContent.FocusContent() => Input.Focus(FocusState.Keyboard);

    private void Vm_EntryAdded(object? sender, EventArgs e)
    {
        if (Vm.Entries.Count > 0)
        {
            List.ScrollIntoView(Vm.Entries[^1]);
        }
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool csharp = LanguageBox.SelectedIndex == 1;
        if (csharp && !ScriptingService.Components.IsInstalled)
        {
            // C# のコンソールは C# スクリプト コンポーネントがあるときだけ (AUTO-23 の仕様 1)。
            Vm.Entries.Add(new ConsoleEntry(ConsoleEntryKind.Info, Loc.Get("Console_NeedComponent")));
            LanguageBox.SelectedIndex = 0;
            return;
        }

        Vm.Language = csharp ? ScriptLanguage.CSharp : ScriptLanguage.JavaScript;
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => Vm.Reset();

    private void Clear_Click(object sender, RoutedEventArgs e) => Vm.Clear();

    /// <summary>入力欄のキー: Enter で実行、Shift+Enter で改行、↑ / ↓ で履歴、Esc で実行中の入力を止める (AUTO-23 の仕様 5・「巨大ファイル・長時間処理」)。</summary>
    private async void Input_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Enter when !shift:
            {
                e.Handled = true;
                string code = Input.Text;
                Input.Text = string.Empty;
                await Vm.SubmitAsync(code);
                Input.Focus(FocusState.Programmatic);
                break;
            }

            case VirtualKey.Up when CaretOnFirstLine():
            {
                if (Vm.Navigator.Up(Input.Text) is { } previous)
                {
                    SetInput(previous);
                    e.Handled = true;
                }

                break;
            }

            case VirtualKey.Down when CaretOnLastLine():
            {
                if (Vm.Navigator.Down() is { } next)
                {
                    SetInput(next);
                    e.Handled = true;
                }

                break;
            }

            case VirtualKey.Escape:
                if (Vm.IsBusy)
                {
                    Vm.StopRunning();
                }
                else
                {
                    _host.FocusEditor();
                }

                e.Handled = true;
                break;
        }
    }

    private bool CaretOnFirstLine() => !Input.Text[..Math.Min(Input.SelectionStart, Input.Text.Length)].Contains('\r');

    private bool CaretOnLastLine() => !Input.Text[Math.Min(Input.SelectionStart + Input.SelectionLength, Input.Text.Length)..].Contains('\r');

    private void SetInput(string text)
    {
        Input.Text = text.Replace("\n", "\r", StringComparison.Ordinal);
        Input.Select(Input.Text.Length, 0);
    }

    /// <summary>入力欄の内容 (テスト用)。</summary>
    public string InputText
    {
        get => Input.Text.Replace('\r', '\n');
        set => SetInput(value);
    }

    /// <summary>入力を実行する (テスト用)。</summary>
    public Task SubmitAsync(string code) => Vm.SubmitAsync(code);

    /// <summary>入力欄でキーを押した動作 (テスト用): ↑ で前の入力を入れる。</summary>
    public void HistoryUp()
    {
        if (Vm.Navigator.Up(Input.Text) is { } previous)
        {
            SetInput(previous);
        }
    }
}
