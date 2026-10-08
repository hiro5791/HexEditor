using HexEditor.App.Hosting;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>表示・ヘルプのメニューと、メニュー項目の有効・無効 (UI-03、UI-26、UI-40)。</summary>
public sealed partial class MainWindow
{
    public const string StatusBarVisibleKey = "ui.statusBar.visible";

    // ---- 表示 ----

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem { Tag: string theme })
        {
            App.Settings.SetString(Appearance.ThemeKey, theme, Appearance.ThemeDefault);
            ApplyAppearance();
        }
    }

    private void UpdateThemeMenu()
    {
        string theme = App.Settings.GetString(Appearance.ThemeKey, Appearance.ThemeDefault);
        ThemeSystem.IsChecked = theme is not ("light" or "dark");
        ThemeLight.IsChecked = theme == "light";
        ThemeDark.IsChecked = theme == "dark";
        bool statusBar = App.Settings.GetBool(StatusBarVisibleKey, true);
        StatusBarToggle.IsChecked = statusBar;
        StatusBar.Visibility = statusBar && !HidesStatusBarForFullScreen ? Visibility.Visible : Visibility.Collapsed;
        QueueStatusBarLayout();
    }

    private void StatusBarToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.SetBool(StatusBarVisibleKey, StatusBarToggle.IsChecked, true);
        UpdateThemeMenu();
    }

    /// <summary>テキスト列の文字コード (VIEW-21 のフェーズ 0: ASCII と ANSI)。入力・コピー・表示に使う。</summary>
    private void Encoding_Click(object sender, RoutedEventArgs e)
    {
        if (Editor is { } editor && sender is RadioMenuFlyoutItem { Tag: string tag })
        {
            editor.TextEncoding = Core.View.TextEncoding.FromId(tag);
            UpdateEncodingMenu();
        }
    }

    private void UpdateEncodingMenu()
    {
        EncodingAnsi.Text = Loc.Format("Menu_View_EncodingAnsi", Core.View.TextEncoding.Ansi.CodePage);
        EncodingAnsi.AccessKey = Loc.Get("Menu_View_EncodingAnsiAccessKey");
        string id = Editor?.TextEncoding.Id ?? "ascii";
        EncodingAscii.IsChecked = id == "ascii";
        EncodingAnsi.IsChecked = id == "ansi";
        foreach (string other in Core.View.TextEncoding.SelectableIds)
        {
            if (_viewItems.TryGetValue("Command_Encoding_" + other, out MenuFlyoutItemBase? item) && item is RadioMenuFlyoutItem radio)
            {
                radio.IsChecked = id == other;
            }
        }
    }

    /// <summary>ステータスバーの文字コードの項目: 文字コードの選択メニューを開く (UI-06 の仕様 1)。</summary>
    private void StatusEncoding_Click(object sender, RoutedEventArgs e)
    {
        UpdateEncodingMenu();
        var menu = new MenuFlyout();
        foreach (string tag in Core.View.TextEncoding.SelectableIds)
        {
            string text = tag == "ansi" ? Loc.Format("Menu_View_EncodingAnsi", Core.View.TextEncoding.Ansi.CodePage) : Core.View.TextEncoding.FromId(tag).Name;
            var item = new RadioMenuFlyoutItem { Text = text, Tag = tag, GroupName = "StatusEncoding" };
            item.IsChecked = tag == (Editor?.TextEncoding.Id ?? "ascii");
            item.Click += Encoding_Click;
            menu.Items.Add(item);
        }

        menu.ShowAt(StatusEncoding);
    }

    // ---- ヘルプ (UI-40) ----

    private async void Documentation_Click(object sender, RoutedEventArgs e) => await OpenUriAsync(new Uri(AboutInfo.DocumentationUrl));

    private async void ReportProblem_Click(object sender, RoutedEventArgs e) => await OpenUriAsync(AboutInfo.IssueUrl(Program.Environment));

    /// <summary>
    /// バージョン情報 (UI-40 の仕様 2〜4、UI-57 の仕様 5): 版・配布形態・アーキテクチャなどと、テレメトリを送らない方針、
    /// データフォルダの場所。「情報をコピー」「サードパーティのライセンス」。
    /// </summary>
    private async void About_Click(object sender, RoutedEventArgs e)
    {
        IAppEnvironment env = Program.Environment;
        var body = new StackPanel { Spacing = 12, MinWidth = 420 };
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        foreach ((string label, string value) in AboutInfo.Items(env))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = Loc.Get("About_" + label.Replace(".", string.Empty).Replace(" ", string.Empty)) };
            var text = new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(text, "About_" + label.Replace(".", string.Empty).Replace(" ", string.Empty));
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
            row++;
        }

        body.Children.Add(grid);

        var dataFolder = new HyperlinkButton { Content = Loc.Get("About_OpenDataFolder"), Padding = new Thickness(0) };
        AutomationProperties.SetAutomationId(dataFolder, "About_DataFolder");
        ToolTipService.SetToolTip(dataFolder, env.Locations.Root);
        dataFolder.Click += async (_, _) =>
        {
            Directory.CreateDirectory(env.Locations.Root);
            await Windows.System.Launcher.LaunchFolderPathAsync(env.Locations.Root);
        };
        body.Children.Add(dataFolder);
        body.Children.Add(new TextBlock { Text = Loc.Get("About_NoTelemetry"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock
        {
            Text = Loc.Get("About_License"),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = "HexEditor",
            Content = new ScrollViewer { Content = body },
            PrimaryButtonText = Loc.Get("About_CopyInfo"),
            SecondaryButtonText = Loc.Get("About_ThirdParty"),
            CloseButtonText = Loc.Get("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "AboutDialog");

        // ボタンは 3 つを同じ幅で並べるため、既定の幅 (最大 548 px) では長い訳 (疑似翻訳で確認) が切れる。幅を広げる (UI-46)。
        dialog.Resources["ContentDialogMinWidth"] = 720.0;
        dialog.Resources["ContentDialogMaxWidth"] = 800.0;

        // 「情報をコピー」は閉じずにコピーだけする。
        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            var package = new DataPackage();
            package.SetText(AboutInfo.Text(env));
            SystemClipboard.SetContent(package);
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            await ShowThirdPartyNoticesAsync();
        }
    }

    /// <summary>同梱のライブラリのライセンス一覧 (UI-40 の仕様 4)。</summary>
    private async Task ShowThirdPartyNoticesAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        string text = File.Exists(path) ? await File.ReadAllTextAsync(path) : Loc.Get("About_ThirdPartyMissing");
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            Title = Loc.Get("About_ThirdParty"),
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = new TextBlock
                {
                    Text = text,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                },
            },
            CloseButtonText = Loc.Get("Common_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    // ---- メニュー項目の有効・無効 (UI-03 の仕様 3) ----

    /// <summary>
    /// 使えないコマンドは無効表示にする (非表示にはしない)。文書・選択・編集の状態が変わるたびに呼ぶ。有効条件は
    /// コマンドの処理 (MainWindow.Commands.cs) が決める (UI-16)。
    /// </summary>
    private void UpdateCommandStates() => RefreshCommandUi();
}
