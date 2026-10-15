using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HexEditor.App.Services;
using HexEditor.App.Services.Plugins;
using HexEditor.Core.Plugins;
using HexEditor.Core.Scripting;

namespace HexEditor.App.ViewModels;

/// <summary>プラグインマネージャの画面の操作のうち、ダイアログ・ファイルの選択・Explorer など View 側で行うもの。</summary>
public interface IPluginManagerHost
{
    Task<string?> PickPackageAsync();

    Task<string?> PickFolderAsync();

    void OpenFolder(string folder);

    Task RestartAsync();

    Task<bool> ConfirmRemoveAsync(string name);

    void ShowPluginSettings(PluginRecord plugin);
}

/// <summary>一覧の 1 行 (AUTO-32 の仕様 1)。</summary>
public sealed partial class PluginItemViewModel(PluginRecord plugin) : ObservableObject
{
    public PluginRecord Plugin { get; } = plugin;

    public string Id => Plugin.Id;

    public string Name => Plugin.NameFor(PluginService.UiLanguage);

    public string Version => Plugin.Version;

    public string Publisher => Plugin.Publisher.Length > 0 ? Plugin.Publisher : Loc.Get("PluginManager_NoPublisher");

    public string VersionAndPublisher => Loc.Format("PluginManager_VersionPublisher", Version, Publisher);

    public string Description => Plugin.Manifest?.DescriptionFor(PluginService.UiLanguage) ?? string.Empty;

    public bool HasDescription => Description.Length > 0;

    /// <summary>署名の状態 (AUTO-35 の仕様 1)。</summary>
    public string SignatureText => Plugin.Signature.State switch
    {
        PluginSignatureState.Valid when Plugin.Signature.RevocationUnchecked => Loc.Format("PluginManager_SignedRevocationUnchecked", Plugin.Signature.Publisher ?? string.Empty),
        PluginSignatureState.Valid => Loc.Format("PluginManager_Signed", Plugin.Signature.Publisher ?? string.Empty),
        PluginSignatureState.Invalid => Loc.Get("PluginManager_SignatureInvalid"),
        _ when Plugin.Signature.Files.Count == 0 && Plugin.State != PluginState.Enabled => Loc.Get("PluginManager_SignatureUnchecked"),
        _ => Loc.Get("PluginManager_Unsigned"),
    };

    /// <summary>状態 (有効 / 無効 / 読み込めません (理由) / 停止中)。次の起動で変わるものはその旨を添える。</summary>
    public string StateText
    {
        get
        {
            string state = Plugin.State switch
            {
                PluginState.Enabled => Loc.Get("PluginManager_StateEnabled"),
                PluginState.Disabled => Loc.Get("PluginManager_StateDisabled"),
                PluginState.Failed => Loc.Format("PluginManager_StateFailed", Plugin.Reason ?? string.Empty),
                PluginState.Suspended => Loc.Format("PluginManager_StateSuspended", Plugin.Reason ?? string.Empty),
                _ => Plugin.Reason is { Length: > 0 } reason ? Loc.Format("PluginManager_StateNotLoaded", reason) : Loc.Get("PluginManager_StateLoading"),
            };
            return Plugin.PendingRestart ? Loc.Format("PluginManager_PendingRestart", state) : state;
        }
    }

    /// <summary>状態の英語の名前 (テスト・アクセシビリティ用)。</summary>
    public string StateName => Plugin.State.ToString();

    public bool IsDevelopment => Plugin.IsDevelopment;

    public string DevelopmentText => Loc.Get("PluginManager_Development");

    /// <summary>追加した拡張ポイントの一覧 (AUTO-32 の仕様 1)。重複などで無効にしたものは理由を添える。</summary>
    public string ExtensionsText => Plugin.Extensions.Count == 0
        ? Loc.Get("PluginManager_NoExtensions")
        : Loc.Format("PluginManager_Extensions", string.Join(", ", Plugin.Extensions.Select(e => e.Status == ExtensionStatus.Active
            ? $"{e.DisplayName} ({Loc.Get("PluginKind_" + e.Kind)})"
            : Loc.Format("PluginManager_ExtensionDisabled", e.Id, e.Problem ?? string.Empty))));

    public string AccessibleName => $"{Name}, {Version}, {StateText}";

    public bool HasSettings => Plugin.State == PluginState.Enabled && Plugin.Extensions.Any(e => e.IsUsable && e.Kind == PluginExtensionKinds.Settings);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>「この発行者を常に信頼する」の一覧の 1 行 (AUTO-35 の仕様 3。取り消せる)。</summary>
public sealed record TrustedPublisherItem(string Thumbprint, string Name)
{
    public string Text => $"{Name} ({Thumbprint[..Math.Min(16, Thumbprint.Length)]}…)";
}

/// <summary>
/// プラグインマネージャ (08 の AUTO-32): 一覧 (名前・バージョン・発行者・署名・状態・説明・拡張ポイント)、追加 (.hexplugin)、有効 / 無効、削除、
/// プラグインのフォルダを開く、「今すぐ再起動」。開発用のフォルダ (AUTO-33) と信頼済みの発行者 (AUTO-35) もここで扱う。
/// </summary>
public sealed partial class PluginManagerViewModel : ObservableObject
{
    private readonly IPluginManagerHost _host;
    private bool _syncing;

    public PluginManagerViewModel(IPluginManagerHost host)
    {
        _host = host;
        Refresh();
    }

    public ObservableCollection<PluginItemViewModel> Items { get; } = [];

    public ObservableCollection<string> DevFolders { get; } = [];

    public ObservableCollection<TrustedPublisherItem> Publishers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedEnabled), nameof(CanToggle), nameof(CanRemove), nameof(SelectedHasSettings))]
    public partial PluginItemViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Items.Count == 0;

    public bool IsSafeMode => PluginService.SafeMode;

    public bool RestartRequired => PluginService.RestartRequired;

    public bool HasPublishers => Publishers.Count > 0;

    /// <summary>直前の操作の結果 (追加できなかった理由など)。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    public bool HasMessage => Message.Length > 0;

    /// <summary>選んだプラグインを次の起動で読み込むか (有効 / 無効の切り替え。AUTO-32 の仕様 3)。</summary>
    public bool SelectedEnabled
    {
        get => Selected is { } s && PluginService.Manager is { } m && m.IsEnabledInState(s.Id);
        set
        {
            if (_syncing || Selected is not { } s || PluginService.Manager is not { } m || value == SelectedEnabled)
            {
                return;
            }

            m.SetEnabled(s.Id, value);
            s.Refresh();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RestartRequired));
        }
    }

    public bool CanToggle => Selected is not null;

    public bool CanRemove => Selected is { Plugin.IsDevelopment: false };

    public bool SelectedHasSettings => Selected?.HasSettings == true;

    /// <summary>一覧を作り直す (選択は ID で引き継ぐ)。</summary>
    public void Refresh()
    {
        string? selected = Selected?.Id;
        _syncing = true;
        try
        {
            Items.Clear();
            if (PluginService.Manager is { } m)
            {
                foreach (PluginRecord p in m.Plugins)
                {
                    Items.Add(new PluginItemViewModel(p));
                }

                Publishers.Clear();
                foreach (TrustRecord r in m.Options.Trust.Records.Where(r => r.Kind == PluginManager.TrustKindPublisher))
                {
                    Publishers.Add(new TrustedPublisherItem(r.Key, r.Hash));
                }
            }

            DevFolders.Clear();
            foreach (string f in PluginService.DevFolders)
            {
                DevFolders.Add(f);
            }

            Selected = Items.FirstOrDefault(i => i.Id == selected);
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(RestartRequired));
        OnPropertyChanged(nameof(HasPublishers));
        OnPropertyChanged(nameof(SelectedEnabled));
    }

    /// <summary>「追加...」: <c>.hexplugin</c> を選んで追加する (AUTO-32 の仕様 2)。</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (await _host.PickPackageAsync() is { } path)
        {
            await InstallAsync(path);
        }
    }

    /// <summary>ファイルを追加する (選んだファイル・ドロップしたファイル)。マニフェストを検証し、信頼の確認を出してから展開する。</summary>
    public async Task InstallAsync(string path)
    {
        if (PluginService.Manager is not { } m)
        {
            return;
        }

        if (PluginService.SafeMode)
        {
            ShowMessage(Loc.Get("PluginManager_SafeModeInstall"), error: true);
            return;
        }

        PluginInstallResult result = await m.InstallAsync(path);
        ShowMessage(result.Installed ? result.Message : Loc.Format("PluginManager_InstallFailed", result.Message), !result.Installed);
        Refresh();
    }

    private void ShowMessage(string text, bool error)
    {
        MessageIsError = error;
        Message = text;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(PluginService.PluginsFolder);
        _host.OpenFolder(PluginService.PluginsFolder);
    }

    [RelayCommand]
    private Task RestartAsync() => _host.RestartAsync();

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (Selected is not { } s || PluginService.Manager is not { } m || !await _host.ConfirmRemoveAsync(s.Name))
        {
            return;
        }

        m.Remove(s.Id);
        Refresh();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (Selected is { HasSettings: true } s)
        {
            _host.ShowPluginSettings(s.Plugin);
        }
    }

    [RelayCommand]
    private async Task AddDevFolderAsync()
    {
        if (await _host.PickFolderAsync() is { } folder)
        {
            PluginService.AddDevFolder(folder);
            ShowMessage(Loc.Get("PluginManager_DevFolderAdded"), error: false);
            Refresh();
        }
    }

    [RelayCommand]
    private void RemoveDevFolder(string folder)
    {
        PluginService.RemoveDevFolder(folder);
        Refresh();
    }

    [RelayCommand]
    private void RevokePublisher(TrustedPublisherItem item)
    {
        PluginService.Manager?.Options.Trust.Revoke(PluginManager.TrustKindPublisher, item.Thumbprint);
        Refresh();
    }
}
