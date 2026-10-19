using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.Core.Elevation;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Forensics.WriteBlock;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// OS の USB ストレージ書き込み保護 (12 の FOR-02)。<c>HKLM\SYSTEM\CurrentControlSet\Control\StorageDevicePolicies</c> の <c>WriteProtect</c> を
/// 切り替える。読み取りは UI のプロセスで行い、書き換えは管理者として実行していれば同じプロセス、そうでなければ補助プロセス (ENG-28 の
/// <c>SetUsbWriteProtect</c>) で行う。有効にする前の状態 (値なし・0 など) をアプリの状態に残し、無効にするときに戻す (仕様 1)。
/// </summary>
public sealed partial class MainWindow
{
    public const string UsbWriteProtectCommandId = "forensics.usbWriteProtect";

    /// <summary>アプリの状態: 有効にする前の値 (<see cref="UsbWriteProtectOriginal.ToState"/>)。</summary>
    internal const string UsbOriginalStateKey = "forensics.usbProtect.original";

    private void InitializeUsbWriteProtect() =>
        Commands.Register(UsbWriteProtectCommandId, () => ToggleUsbWriteProtectAsync(), () => Toggle(UsbWriteProtectIsOn()));

    /// <summary>今のレジストリの値で有効か (メニューのチェック。読めなければ無効とみなす)。</summary>
    internal static bool UsbWriteProtectIsOn()
    {
        try
        {
            return UsbWriteProtect.IsOn(App.Devices?.UsbWriteProtectReader.Read());
        }
        catch (UsbWriteProtectException ex)
        {
            AppLog.Warning($"USB write protection could not be read: {ex.ErrorCode}");
            return false;
        }
    }

    private async Task ToggleUsbWriteProtectAsync()
    {
        bool enable = !UsbWriteProtectIsOn();

        // 仕様 3・6: すでに接続されているデバイスには効かないこと、内蔵の SATA・NVMe は保護しないことを示し、「設定を変更する」「キャンセル」。
        var body = new TextBlock { Text = Loc.Get(enable ? "UsbProtect_EnableBody" : "UsbProtect_DisableBody"), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        AutomationProperties.SetAutomationId(body, "UsbProtect_Body");
        ContentDialog dialog = DialogParts.Dialog(Root, "UsbProtectDialog", Loc.Get(enable ? "UsbProtect_EnableTitle" : "UsbProtect_DisableTitle"), body,
            Loc.Get("UsbProtect_Change"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await SetUsbWriteProtectAsync(enable);
    }

    /// <summary>切り替える。管理者権限が使えなければ案内を出し、失敗したら理由を示して設定を変えない (FOR-02 の「エラー」)。変えたら true。</summary>
    internal async Task<bool> SetUsbWriteProtectAsync(bool enable)
    {
        IUsbWriteProtectStore? store;
        try
        {
            store = await DeviceService.UsbWriteProtectWriterAsync();
        }
        catch (HelperElevationDeclinedException)
        {
            ShowNotice(Loc.Get("UsbProtect_NeedsAdmin"), InfoBarSeverity.Warning);
            return false;
        }
        catch (Exception ex) when (ex is HelperNotFoundException or HelperTamperedException or IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            if (!await HandleElevatedOpenFailureAsync(ex, forDisk: true))
            {
                throw;
            }

            return false;
        }

        if (store is null)
        {
            await ShowAdminGuidanceAsync(Loc.Get("AdminGuide_UsbProtect"), forDisk: true);
            return false;
        }

        RefreshHelperIndicator();
        try
        {
            if (enable)
            {
                UsbWriteProtectOriginal original = await Task.Run(() => UsbWriteProtect.Enable(store));

                // 前に有効にして戻していない (元の状態を覚えている) なら、最初の状態を残す。
                if (UsbWriteProtectOriginal.FromState(AppState.GetString(UsbOriginalStateKey, string.Empty)) is null)
                {
                    AppState.SetString(UsbOriginalStateKey, original.ToState());
                }
            }
            else
            {
                UsbWriteProtectOriginal? original = UsbWriteProtectOriginal.FromState(AppState.GetString(UsbOriginalStateKey, string.Empty));
                await Task.Run(() => UsbWriteProtect.Disable(store, original));
                AppState.SetString(UsbOriginalStateKey, string.Empty);
            }
        }
        catch (UsbWriteProtectException ex)
        {
            ForensicsService.Audit.Record(new AuditRecord(enable ? AuditOperations.UsbProtectEnable : AuditOperations.UsbProtectDisable)
            {
                Path = @"HKLM\" + UsbWriteProtect.KeyPath,
                Result = AuditResult.Failure,
                Message = ex.Message,
                AlwaysGlobal = true,
            });
            ShowNotice(Loc.Format(ex.ErrorCode == Core.Devices.Win32Errors.AccessDenied ? "UsbProtect_Denied" : "UsbProtect_Failed", ex.Message), InfoBarSeverity.Error);
            return false;
        }

        // 仕様 5: 変更を監査ログに記録する (ケースがなくても設定フォルダのログに残す)。
        ForensicsService.Audit.Record(new AuditRecord(enable ? AuditOperations.UsbProtectEnable : AuditOperations.UsbProtectDisable)
        {
            Path = @"HKLM\" + UsbWriteProtect.KeyPath,
            Message = enable ? "OS USB storage write protection was enabled." : "OS USB storage write protection was disabled.",
            AlwaysGlobal = true,
        });
        AppLog.Info($"OS USB write protection {(enable ? "enabled" : "disabled")}");
        ShowNotice(Loc.Get(enable ? "UsbProtect_Enabled" : "UsbProtect_Disabled"), InfoBarSeverity.Success);
        Commands.NotifyStatesChanged();
        return true;
    }

    /// <summary>
    /// アプリの終了の前 (仕様 4): 有効のままなら「OS の USB 書き込み保護が有効のままです」と示し、「無効にして終了」「有効のまま終了」を選ばせる。
    /// 終了を続けるなら true (ダイアログを閉じた場合は終了しない)。
    /// </summary>
    internal async Task<bool> ConfirmUsbWriteProtectAtExitAsync()
    {
        if (!UsbWriteProtectIsOn())
        {
            return true;
        }

        var body = new TextBlock { Text = Loc.Get("UsbProtect_ExitBody"), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
        AutomationProperties.SetAutomationId(body, "UsbProtect_ExitBody");
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
            FlowDirection = Root.FlowDirection,
            Title = Loc.Get("UsbProtect_ExitTitle"),
            Content = body,
            PrimaryButtonText = Loc.Get("UsbProtect_ExitDisable"),
            SecondaryButtonText = Loc.Get("UsbProtect_ExitKeep"),
            DefaultButton = ContentDialogButton.Primary,
        };
        AutomationProperties.SetAutomationId(dialog, "UsbProtectExitDialog");
        switch (await dialog.ShowQueuedAsync())
        {
            case ContentDialogResult.Primary:
                // 無効にできなければ終了しない (有効のまま気づかずに終わらないように)。
                return await SetUsbWriteProtectAsync(enable: false);
            case ContentDialogResult.Secondary:
                return true;
            default:
                return false;
        }
    }
}
