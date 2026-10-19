using System.Collections.Specialized;
using System.ComponentModel;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Forensics.Cases;
using HexEditor.Core.Notifications;
using HexEditor.Core.Processes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 書き込み保護モード (12 の FOR-01) の画面: フォレンジックメニューのチェック付きの項目、ステータスバーの左端の表示、タイトルの「[書き込み保護]」、
/// タブの錠 (読み取り専用の 🔒 と例外の 🔓)、解除の確認ダイアログ、ドキュメント単位の例外 (タブの右クリックメニューと強い確認)、初回の InfoBar。
/// 有効な間は、このウィンドウのすべてのドキュメントを解除できない読み取り専用 (<see cref="ReadOnlyReason.WriteProtectionMode"/>) にし、
/// 読み書きで開いていたディスク・プロセスは読み取りのアクセス権で開き直す (仕様 2)。書き込みそのものの拒否は Core の関門が行う。
/// </summary>
public sealed partial class MainWindow
{
    public const string WriteBlockCommandId = "forensics.writeBlock";
    public const string WriteBlockAllowCommandId = "forensics.writeBlock.allowDocument";

    private void InitializeWriteBlock()
    {
        Commands.Register(WriteBlockCommandId, () => ToggleWriteBlockAsync(), () => Toggle(WriteBlockMode.IsEnabled));
        Commands.Register(WriteBlockAllowCommandId, () => Vm.Selected is { } doc ? AllowWritingAsync(doc) : Task.CompletedTask,
            () => Vm.Selected is { } doc ? AllowWritingState(doc) : NeedsDocument());

        // 書き込み保護モードで無効にする破壊的な操作 (仕様 2 の 5 つ目)。消去・クローン・復元・パーティションの書き込みの担当部品も
        // ForensicsService.RegisterDestructiveCommands で登録する。
        ForensicsService.RegisterDestructiveCommands("tools.files.shred");

        EventHandler changed = (_, _) => DispatcherQueue.TryEnqueue(OnWriteBlockChanged);
        WriteBlockMode.Changed += changed;
        Vm.Documents.CollectionChanged += WriteBlockDocumentsChanged;
        Closed += (_, _) =>
        {
            WriteBlockMode.Changed -= changed;
            Vm.Documents.CollectionChanged -= WriteBlockDocumentsChanged;
            foreach (DocumentViewModel doc in Vm.Documents)
            {
                ForgetWriteBlockSource(doc);
            }
        };

        StatusWriteBlockText.Text = Loc.Get("Status_WriteBlock");
        AutomationProperties.SetName(StatusWriteBlock, Loc.Get("Status_WriteBlock"));
        ToolTipService.SetToolTip(StatusWriteBlock, Loc.Get("Status_WriteBlock_ToolTip"));
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            TrackWriteBlockSource(doc);
        }

        ApplyWriteBlockToAll();
        ShowWriteBlockFirstNotice();
    }

    /// <summary>
    /// 初めて有効にしたとき (メニュー・ケース・起動のどれでも)、このアプリからの書き込みしか防げないことを InfoBar で一度だけ知らせ、ハードウェアの
    /// 書き込み防止装置か OS の USB 書き込み保護 (FOR-02) の併用を勧める (仕様 7)。最後にアクティブだったウィンドウにだけ出す。
    /// </summary>
    private void ShowWriteBlockFirstNotice()
    {
        if (!WriteBlockMode.IsEnabled || (WindowManager.Windows.Count > 0 && WindowManager.Current != this) || !WriteBlockMode.TakeFirstNotice())
        {
            return;
        }

        ShowNotice(Loc.Get("WriteBlock_FirstNotice"), InfoBarSeverity.Informational, actions:
        [
            new NotificationAction(Loc.Get("WriteBlock_FirstNotice_Usb"), () => _ = Commands.ExecuteAsync(UsbWriteProtectCommandId)),
        ]);
    }

    /// <summary>ステータスバーの表示をクリック: 切り替える (FOR-01 の「呼び出し」)。</summary>
    private void StatusWriteBlock_Click(object sender, RoutedEventArgs e) => _ = Commands.ExecuteAsync(WriteBlockCommandId);

    private void OnWriteBlockChanged()
    {
        ApplyWriteBlockToAll();
        ShowWriteBlockFirstNotice();
        UpdateTitle();
        UpdateStatusBarLayout();
        Commands.NotifyStatesChanged();
    }

    // ---- 切り替え ----

    private async Task ToggleWriteBlockAsync()
    {
        if (!WriteBlockMode.IsEnabled)
        {
            EnableWriteBlockFromMenu();
            return;
        }

        await ConfirmDisableWriteBlockAsync();
    }

    /// <summary>有効にする (初回の InfoBar は <see cref="ShowWriteBlockFirstNotice"/>)。</summary>
    internal void EnableWriteBlockFromMenu() => WriteBlockMode.Enable("menu");

    /// <summary>開いている証拠のドキュメント (どのウィンドウでも)。</summary>
    private static List<DocumentViewModel> OpenEvidenceDocuments() =>
        [.. WindowManager.Windows.SelectMany(w => w.Vm.Documents).Where(IsEvidenceDocument).Distinct()];

    /// <summary>証拠一覧 (FOR-03) に登録されたデータソースのドキュメントか。</summary>
    internal static bool IsEvidenceDocument(DocumentViewModel d)
    {
        string? path = d.Device?.Path ?? d.FilePath;
        return path is not null && ForensicsService.Cases.CurrentCase is not null && ForensicsService.Cases.IsRegistered(KindOf(d), path);
    }

    /// <summary>
    /// 解除の確認ダイアログ (仕様 4): 開いている証拠のドキュメントの数と名前を示し、「書き込み保護を解除する」「キャンセル」。解除したら true。
    /// </summary>
    internal async Task<bool> ConfirmDisableWriteBlockAsync()
    {
        List<DocumentViewModel> evidence = OpenEvidenceDocuments();
        var body = new StackPanel { Spacing = 8, MaxWidth = 460 };
        var message = new TextBlock
        {
            Text = evidence.Count == 0 ? Loc.Get("WriteBlock_Disable_NoEvidence") : Loc.Format("WriteBlock_Disable_Body", evidence.Count),
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(message, "WriteBlock_DisableBody");
        body.Children.Add(message);
        if (evidence.Count > 0)
        {
            var names = new TextBlock { Text = string.Join(Environment.NewLine, evidence.Select(d => "• " + d.DisplayName)), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(names, "WriteBlock_DisableEvidence");
            body.Children.Add(names);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "WriteBlockDisableDialog", Loc.Get("WriteBlock_Disable_Title"), body, Loc.Get("WriteBlock_Disable_Button"));
        dialog.DefaultButton = ContentDialogButton.Close;
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary)
        {
            return false;
        }

        return WriteBlockMode.Disable([.. evidence.Select(d => d.DisplayName)]);
    }

    // ---- ドキュメントごとの読み取り専用 (仕様 2) ----

    private void ApplyWriteBlockToAll()
    {
        foreach (DocumentViewModel doc in Vm.Documents.ToList())
        {
            ApplyWriteBlock(doc);
        }
    }

    /// <summary>ドキュメントにモードの状態を反映する (有効なら読み取り専用にし、無効・例外なら元に戻す)。</summary>
    private void ApplyWriteBlock(DocumentViewModel doc)
    {
        if (doc.IsPending || doc.Document.IsDisposed)
        {
            return;
        }

        Document document = doc.Document;
        bool excepted = doc.FilePath is { } path && WriteBlockMode.Policy.IsExcepted(path);
        doc.WriteBlockException = WriteBlockMode.IsEnabled && excepted;
        if (WriteBlockMode.IsEnabled && !excepted)
        {
            if (document.ReadOnlyReason != ReadOnlyReason.WriteProtectionMode)
            {
                doc.ReadOnlyBeforeWriteBlock = document.ReadOnlyReason;
                document.SetReadOnly(ReadOnlyReason.WriteProtectionMode);
            }

            ReopenReadOnlyForWriteBlock(doc);
            doc.ImmediateWrite = false;
            return;
        }

        if (document.ReadOnlyReason == ReadOnlyReason.WriteProtectionMode)
        {
            ReadOnlyReason before = doc.ReadOnlyBeforeWriteBlock ?? ReadOnlyReason.None;

            // 読み取りで開き直したディスク・プロセスは、書き込みを許可し直すまで読み取り専用 (ENG-14 の仕様 3)。
            if (before == ReadOnlyReason.None && (doc.Device is { Handle.Writable: false } || doc.ProcessMemory is { Memory.Writable: false }))
            {
                before = ReadOnlyReason.Device;
            }

            document.SetReadOnly(before);
        }

        doc.ReadOnlyBeforeWriteBlock = null;
    }

    /// <summary>読み書きで開いていたディスク・ボリューム・プロセスを、読み取りのアクセス権で開き直す (仕様 2 の 3 つ目)。</summary>
    private void ReopenReadOnlyForWriteBlock(DocumentViewModel doc)
    {
        try
        {
            if (doc.Device is { } device && device.Handle.Writable && !device.IsDisconnected)
            {
                IDeviceHandle handle = device.Access.Open(device.Path, writable: false);
                device.ReplaceHandle(handle, device.Access);
            }
            else if (doc.ProcessMemory is { } process && process.Memory.Writable && !process.HasExited)
            {
                IProcessMemory memory = process.Access.Open(process.Pid, writable: false);
                process.ReplaceMemory(memory, process.Access);
            }
        }
        catch (Exception ex) when (ex is DeviceException or ProcessAccessException or IOException or InvalidOperationException)
        {
            // 開き直せない (取り外された・終了した): データソースへのアクセスを止め、表示はキャッシュの範囲だけにする (FOR-01 の「エラー」)。
            AppLog.Warning($"Write protection mode: the source could not be reopened read-only: {ex.GetType().Name}");
            doc.Device?.MarkDisconnected(Win32Errors.DeviceNotConnected);
            ShowNotice(Loc.Get("WriteBlock_ReopenFailed"), InfoBarSeverity.Warning, doc);
        }
    }

    private void WriteBlockDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DocumentViewModel doc in (e.NewItems ?? Array.Empty<object>()).OfType<DocumentViewModel>())
        {
            TrackWriteBlockSource(doc);

            // 開く処理が読み取り専用の理由を決め終わってから反映する (同じ UI スレッドの、開く処理の後)。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Vm.Documents.Contains(doc))
                {
                    ApplyWriteBlock(doc);
                    UpdateTitle();
                    Commands.NotifyStatesChanged();
                }
            });
        }

        foreach (DocumentViewModel doc in (e.OldItems ?? Array.Empty<object>()).OfType<DocumentViewModel>())
        {
            ForgetWriteBlockSource(doc);

            // ドキュメントを閉じたら例外も終わる (仕様 5)。別のウィンドウへ移しただけなら終わらない。
            if (doc.FilePath is { } path && WriteBlockMode.Policy.IsExcepted(path)
                && !WindowManager.Windows.Any(w => w.Vm.Documents.Any(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase))))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!WindowManager.Windows.Any(w => w.Vm.Documents.Any(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase))))
                    {
                        WriteBlockMode.Service.RemoveException(path);
                    }
                });
            }
        }
    }

    /// <summary>開いたドキュメントのデータソースのファイルを保護の対象に入れる (保存して別のファイルになったら付け替える)。</summary>
    private void TrackWriteBlockSource(DocumentViewModel doc)
    {
        if (doc.FilePath is { } path)
        {
            WriteBlockMode.AddOpenSource(path);
        }

        doc.FilePathChanged += OnWriteBlockFilePathChanged;
        doc.Document.ReadOnlyChanged += OnWriteBlockReadOnlyChanged;
    }

    private void ForgetWriteBlockSource(DocumentViewModel doc)
    {
        if (doc.FilePath is { } path)
        {
            WriteBlockMode.RemoveOpenSource(path);
        }

        doc.FilePathChanged -= OnWriteBlockFilePathChanged;
        doc.Document.ReadOnlyChanged -= OnWriteBlockReadOnlyChanged;
    }

    private void OnWriteBlockFilePathChanged(object? sender, string? before)
    {
        if (sender is not DocumentViewModel doc)
        {
            return;
        }

        if (before is not null)
        {
            WriteBlockMode.RemoveOpenSource(before);
        }

        if (doc.FilePath is { } path)
        {
            WriteBlockMode.AddOpenSource(path);
        }
    }

    /// <summary>モードの間に、他の処理が読み取り専用の理由を変えた: 例外でなければモードの読み取り専用に戻す。</summary>
    private void OnWriteBlockReadOnlyChanged(object? sender, EventArgs e)
    {
        if (!WriteBlockMode.IsEnabled || sender is not Document document || document.ReadOnlyReason == ReadOnlyReason.WriteProtectionMode)
        {
            return;
        }

        DocumentViewModel? doc = Vm.Documents.FirstOrDefault(d => ReferenceEquals(d.Document, document));
        if (doc is not null && !(doc.FilePath is { } path && WriteBlockMode.Policy.IsExcepted(path)))
        {
            DispatcherQueue.TryEnqueue(() => ApplyWriteBlock(doc));
        }
    }

    // ---- ドキュメント単位の例外 (仕様 5) ----

    private CommandState AllowWritingState(DocumentViewModel doc) =>
        !WriteBlockMode.IsEnabled ? CommandState.Unavailable(Loc.Get("WriteBlock_Allow_NotEnabled"))
        : doc.FilePath is null || doc.Device is not null || doc.IsProcessMemory ? CommandState.Unavailable(Loc.Get("WriteBlock_Allow_FilesOnly"))
        : IsEvidenceDocument(doc) ? CommandState.Unavailable(Loc.Get("WriteBlock_Allow_Evidence"))
        : WriteBlockMode.Policy.IsExcepted(doc.FilePath) ? CommandState.Unavailable(Loc.Get("WriteBlock_Allow_Already"))
        : CommandState.Available;

    /// <summary>タブの右クリックメニューの「このドキュメントへの書き込みを許可」。</summary>
    private IEnumerable<TabMenuEntry?> WriteBlockTabMenuEntries(DocumentViewModel doc)
    {
        if (!WriteBlockMode.IsEnabled)
        {
            return [];
        }

        return
        [
            null,
            new TabMenuEntry("TabMenu_AllowWriting", Loc.Get("Tab_AllowWriting"), AllowWritingState(doc).Enabled, () =>
            {
                Vm.Selected = doc;
                _ = AllowWritingAsync(doc);
            }),
        ];
    }

    /// <summary>
    /// 強い確認 (共通の前提「破壊的な操作の確認」の 2): 対象の表示名を入力するまで実行ボタンを無効にし、既定のフォーカスは「キャンセル」。
    /// 確認したら例外を設定して読み取り専用を解除する。
    /// </summary>
    internal async Task AllowWritingAsync(DocumentViewModel doc)
    {
        if (!AllowWritingState(doc).Enabled || doc.FilePath is not { } path)
        {
            return;
        }

        var body = new StackPanel { Spacing = 8, MaxWidth = 460 };
        body.Children.Add(new TextBlock { Text = Loc.Format("WriteBlock_Allow_Body", doc.DisplayName, path), TextWrapping = TextWrapping.Wrap });
        TextBox name = DialogParts.Field("WriteBlock_AllowName", Loc.Format("WriteBlock_Allow_Type", doc.DisplayName), string.Empty, monospace: false);
        body.Children.Add(name);
        ContentDialog dialog = DialogParts.Dialog(Root, "WriteBlockAllowDialog", Loc.Get("WriteBlock_Allow_Title"), body, Loc.Format("WriteBlock_Allow_Button", doc.DisplayName));
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.IsPrimaryButtonEnabled = false;
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = string.Equals(name.Text.Trim(), doc.DisplayName, StringComparison.Ordinal);
        if (await dialog.ShowQueuedAsync() != ContentDialogResult.Primary || !Vm.Documents.Contains(doc))
        {
            return;
        }

        if (WriteBlockMode.Service.AddException(path))
        {
            ApplyWriteBlock(doc);
            ShowNotice(Loc.Format("WriteBlock_Allowed", doc.DisplayName), InfoBarSeverity.Informational, doc);
            Commands.NotifyStatesChanged();
        }
    }
}
