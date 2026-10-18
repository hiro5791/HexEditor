using System.Globalization;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Settings;
using HexEditor.Core.Templates.Online;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Views;

/// <summary>
/// テンプレートの管理画面の「オンライン」(07 の TPL-36 の仕様 4): 検索、分類での絞り込み、詳細 (説明、版、ライセンス、ソースの表示)、
/// インストール・削除・更新、利用者が追加するリポジトリ。通信がオフなら「オンラインのテンプレートは無効になっています」と設定を開くボタン
/// だけを出し、通信しない (仕様 7)。
/// </summary>
public static class OnlineTemplateSection
{
    public const string Id = "templateOnline";

    private static readonly List<WeakReference<Root>> Instances = [];

    public static void Register()
    {
        if (SettingsSections.All.All(s => s.Id != Id))
        {
            SettingsSections.Register(new SettingsSection(TemplateSettingsSection.Category, Id, "SetSection_TemplateOnline", w => Create(w), 15)
            {
                SearchKeys = ["SetSearch_TemplateOnline"],
            });
        }
    }

    private static FrameworkElement Create(MainWindow window)
    {
        var root = new Root(window);
        lock (Instances)
        {
            Instances.RemoveAll(w => !w.TryGetTarget(out _));
            Instances.Add(new WeakReference<Root>(root));
        }

        return root;
    }

    /// <summary>開いている画面 (テスト用の命令)。</summary>
    internal static Root? Current
    {
        get
        {
            lock (Instances)
            {
                foreach (WeakReference<Root> w in Instances)
                {
                    if (w.TryGetTarget(out Root? r) && r.XamlRoot is not null)
                    {
                        return r;
                    }
                }
            }

            return null;
        }
    }

    internal sealed partial class Root : StackPanel
    {
        private readonly MainWindow _window;
        private readonly TextBox _search;
        private readonly ComboBox _category;
        private readonly TextBlock _status;
        private readonly StackPanel _list = new() { Spacing = 2 };
        private readonly StackPanel _repositories = new() { Spacing = 4 };
        private readonly TextBox _repoUrl;
        private IReadOnlyList<TemplateRepositoryListing> _listings = [];
        private CancellationTokenSource? _loading;

        public Root(MainWindow window)
        {
            _window = window;
            Spacing = 8;
            AutomationProperties.SetAutomationId(this, "TplOnline");
            _search = new TextBox { PlaceholderText = Loc.Get("TplOnline_Search") };
            AutomationProperties.SetAutomationId(_search, "TplOnline_Search");
            AutomationProperties.SetName(_search, Loc.Get("TplOnline_Search"));
            _search.TextChanged += (_, _) => RenderList();
            _category = new ComboBox { MinWidth = 160 };
            AutomationProperties.SetAutomationId(_category, "TplOnline_Category");
            AutomationProperties.SetName(_category, Loc.Get("TplOnline_Category"));
            _category.SelectionChanged += (_, _) => RenderList();
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(_status, "TplOnline_Status");
            AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            _repoUrl = new TextBox { PlaceholderText = "https://example.org/templates/index.json", MinWidth = 320, FlowDirection = FlowDirection.LeftToRight };
            AutomationProperties.SetAutomationId(_repoUrl, "TplOnline_RepoUrl");
            AutomationProperties.SetName(_repoUrl, Loc.Get("TplOnline_RepoUrl"));
            Build();
        }

        /// <summary>読み込んだ一覧 (テスト用)。</summary>
        internal IReadOnlyList<TemplateRepositoryListing> Listings => _listings;

        internal string StatusText => _status.Text;

        internal Task? LoadingTask { get; private set; }

        /// <summary>行 (テスト用): ID、表示名、説明、版、ライセンス、署名なし、インストール済みの版、更新があるか。</summary>
        internal List<(string Id, string Name, string Description, string Version, string License, bool Unsigned, string? Installed, bool Update)> RowsForTest { get; } = [];

        private void Build()
        {
            Children.Clear();
            if (!OnlineTemplateService.DownloadsAllowed)
            {
                // 通信がオフ (仕様 7): 何も読みに行かず、設定を開くボタンだけを出す。
                var off = new InfoBar
                {
                    IsOpen = true,
                    IsClosable = false,
                    Severity = InfoBarSeverity.Informational,
                    Message = Loc.Get("TplOnline_Disabled"),
                };
                AutomationProperties.SetAutomationId(off, "TplOnline_Disabled");
                var open = new Button { Content = Loc.Get("TplOnline_OpenSettings") };
                AutomationProperties.SetAutomationId(open, "TplOnline_OpenSettings");
                open.Click += (_, _) => _window.OpenSettingsPage(SettingCategories.Privacy, Platform.Network.NetworkPolicy.OfflineKey);
                off.ActionButton = open;
                Children.Add(off);
                _status.Text = Loc.Get("TplOnline_Disabled");
                return;
            }

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            tools.Children.Add(_search);
            tools.Children.Add(_category);
            tools.Children.Add(Button("TplOnline_Refresh", "TplOnline_Refresh", () => LoadingTask = LoadAsync()));
            tools.Children.Add(Button("TplOnline_CheckUpdates", "TplOnline_CheckUpdates", () => _ = _window.ExecuteCommandAsync("templates.online.checkUpdates")));
            Children.Add(tools);
            Children.Add(_status);
            Children.Add(new ScrollViewer { Content = _list, MaxHeight = 420 });

            Children.Add(new TextBlock { Text = Loc.Get("TplOnline_Repositories"), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });
            Children.Add(_repositories);
            var add = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            add.Children.Add(_repoUrl);
            add.Children.Add(Button("TplOnline_AddRepo", "TplOnline_AddRepo", () =>
            {
                if (OnlineTemplateService.AddRepository(_repoUrl.Text))
                {
                    _repoUrl.Text = string.Empty;
                    LoadingTask = LoadAsync();
                }
                else
                {
                    _status.Text = Loc.Get("TplOnline_BadUrl");
                }
            }));
            Children.Add(add);
            RenderRepositories();
            LoadingTask = LoadAsync();
        }

        private void RenderRepositories()
        {
            _repositories.Children.Clear();
            foreach (string url in OnlineTemplateService.UserRepositories)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock { Text = url, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight });
                string u = url;
                row.Children.Add(Button("TplOnline_RemoveRepo_" + Safe(url), "TplMgr_RemoveFolder", () =>
                {
                    OnlineTemplateService.RemoveRepository(u);
                    RenderRepositories();
                    LoadingTask = LoadAsync();
                }));
                _repositories.Children.Add(row);
            }

            if (OnlineTemplateService.UserRepositories.Count == 0)
            {
                _repositories.Children.Add(new TextBlock { Text = Loc.Get("TplOnline_NoRepositories"), Foreground = Brush("TextFillColorSecondaryBrush") });
            }
        }

        private async Task LoadAsync()
        {
            _loading?.Cancel();
            _loading = new CancellationTokenSource();
            CancellationToken ct = _loading.Token;
            _status.Text = Loc.Get("TplOnline_Loading");
            IReadOnlyList<TemplateRepositoryListing> listings;
            try
            {
                listings = await Task.Run(() => OnlineTemplateService.LoadAllAsync(ct), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _listings = listings;
            var messages = new List<string>();
            foreach (TemplateRepositoryListing l in listings.Where(l => !l.Ok))
            {
                messages.Add(l.Error switch
                {
                    TemplateRepositoryError.Tampered => Loc.Format("TplOnline_Tampered", l.Repository.Name),
                    TemplateRepositoryError.Invalid => Loc.Format("TplOnline_Invalid", l.Repository.Name, l.Message ?? string.Empty),
                    _ => Loc.Format("TplOnline_Unavailable", l.Message ?? string.Empty),
                });
            }

            if (listings.Count == 0)
            {
                messages.Add(Loc.Get("TplOnline_NoRepositories"));
            }

            _status.Text = string.Join("\n", messages);
            var categories = listings.SelectMany(l => l.Templates).SelectMany(t => t.Categories).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            string? selected = _category.SelectedItem as string;
            _category.Items.Clear();
            _category.Items.Add(Loc.Get("TplOnline_AllCategories"));
            foreach (string c in categories)
            {
                _category.Items.Add(c);
            }

            _category.SelectedIndex = selected is not null && categories.Contains(selected) ? categories.IndexOf(selected) + 1 : 0;
            RenderList();
        }

        private void RenderList()
        {
            _list.Children.Clear();
            RowsForTest.Clear();
            string query = _search.Text;
            string? category = _category.SelectedIndex > 0 ? _category.SelectedItem as string : null;
            CultureInfo ui = CultureInfo.CurrentUICulture;
            OnlineTemplateStore store = OnlineTemplateService.Store;
            foreach (TemplateRepositoryListing listing in _listings.Where(l => l.Ok))
            {
                // 同じ ID の複数の版は新しい版だけを並べる。
                foreach (OnlineTemplateEntry entry in listing.Templates
                    .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(t => t.Version, Comparer<string>.Create(TemplateVersion.Compare)).First()))
                {
                    if (!entry.Matches(query) || (category is not null && !entry.Categories.Contains(category, StringComparer.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    InstalledOnlineTemplate? installed = store.Find(entry.Id);
                    bool update = installed is not null && installed.Repository == listing.Repository.IndexUrl.AbsoluteUri
                        && TemplateVersion.Compare(entry.Version, installed.Version) > 0;
                    RowsForTest.Add((entry.Id, entry.DisplayName(ui), entry.Description(ui), entry.Version, entry.License, !listing.Signed, installed?.Version, update));
                    _list.Children.Add(Row(listing, entry, installed, update, ui));
                }
            }

            if (_list.Children.Count == 0 && _listings.Any(l => l.Ok))
            {
                _list.Children.Add(new TextBlock { Text = Loc.Get("TplOnline_NoMatch"), Foreground = Brush("TextFillColorSecondaryBrush") });
            }
        }

        private FrameworkElement Row(TemplateRepositoryListing listing, OnlineTemplateEntry entry, InstalledOnlineTemplate? installed, bool update, CultureInfo ui)
        {
            string id = Safe(entry.Id);
            var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = entry.DisplayName(ui), Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            var parts = new List<string> { Loc.Format("TplApply_Version", entry.Version) };
            if (entry.License.Length > 0)
            {
                parts.Add(Loc.Format("TplOnline_License", entry.License));
            }

            if (entry.Author.Length > 0)
            {
                parts.Add(Loc.Format("TplOnline_Author", entry.Author));
            }

            parts.Add(listing.Repository.Name);
            if (installed is not null)
            {
                parts.Add(Loc.Format("TplOnline_InstalledVersion", installed.Version));
            }

            info.Children.Add(new TextBlock { Text = string.Join(" · ", parts), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
            if (!listing.Signed)
            {
                var unsigned = new TextBlock { Text = Loc.Get("TplOnline_Unsigned"), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Brush("SystemFillColorCautionBrush") };
                AutomationProperties.SetAutomationId(unsigned, "TplOnline_Unsigned_" + id);
                info.Children.Add(unsigned);
            }

            if (entry.UsesExtensions)
            {
                info.Children.Add(new TextBlock { Text = Loc.Get("TplOnline_UsesExtensions"), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = Brush("SystemFillColorCautionBrush") });
            }

            string description = entry.Description(ui);
            if (description.Length > 0)
            {
                info.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
            }

            grid.Children.Add(info);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
            if (installed is null)
            {
                buttons.Children.Add(Button("TplOnline_Install_" + id, "TplOnline_Install", () => _ = InstallAsync(listing, entry)));
            }
            else
            {
                if (update)
                {
                    buttons.Children.Add(Button("TplOnline_Update_" + id, "TplOnline_Update", () => _ = InstallAsync(listing, entry)));
                }

                buttons.Children.Add(Button("TplOnline_Remove_" + id, "TplOnline_Remove", () =>
                {
                    OnlineTemplateService.Store.Remove(entry.Id);
                    TemplateSettingsSection.RefreshAll();
                    RenderList();
                }));
            }

            buttons.Children.Add(Button("TplOnline_Source_" + id, "TplOnline_Source", () => _ = ShowSourceAsync(listing, entry)));
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);
            AutomationProperties.SetAutomationId(grid, "TplOnline_Row_" + id);
            AutomationProperties.SetName(grid, entry.DisplayName(ui));
            return grid;
        }

        internal async Task<bool> InstallAsync(TemplateRepositoryListing listing, OnlineTemplateEntry entry)
        {
            bool ok = await _window.InstallOnlineTemplateAsync(listing, entry);
            TemplateSettingsSection.RefreshAll();
            RenderList();
            return ok;
        }

        private async Task ShowSourceAsync(TemplateRepositoryListing listing, OnlineTemplateEntry entry)
        {
            string text;
            try
            {
                byte[] data = await Task.Run(() => OnlineTemplateService.Client.DownloadAsync(listing.Repository, entry, CancellationToken.None));
                text = System.Text.Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) when (ex is TemplateTamperedException)
            {
                text = Loc.Get("TplOnline_FileTampered");
            }
            catch (Exception ex) when (ex is TemplateFeedUnavailableException or TemplateFeedTooLargeException)
            {
                text = Loc.Format("TplOnline_Unavailable", ex.Message);
            }

            await _window.ShowOnlineTemplateSourceAsync(entry.DisplayName(CultureInfo.CurrentUICulture), text);
        }

        private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

        private static string Safe(string s) => new([.. s.Where(char.IsLetterOrDigit)]);

        private static Button Button(string id, string key, Action click)
        {
            var b = new Button { Content = Loc.Get(key) };
            AutomationProperties.SetAutomationId(b, id);
            b.Click += (_, _) => click();
            return b;
        }
    }
}
