using System.Security.Cryptography;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing.Codecs;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using CipherMode = HexEditor.Core.Editing.Codecs.CipherMode;

namespace HexEditor.App;

/// <summary>
/// 派生ドキュメント (EDIT-46): 展開・復号した結果を新しいタブで開き、編集してから逆の変換をかけて元の範囲に書き戻す。
/// タブの上部の帯に元のドキュメント・範囲・変換と「元に書き戻す」ボタンを出す。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>タブの見出しに付ける範囲と変換 (<c> [0x100-0x4FF 展開]</c>。仕様 1)。</summary>
    private static string DerivedRangeLabel(long start, long length, string label) =>
        Loc.Format("Derived_TabRange", start.ToString("X"), (start + Math.Max(0, length - 1)).ToString("X"), label);

    /// <summary>変換の説明 (帯に出す。「zlib の展開」「AES-256 GCM の復号」)。</summary>
    private static string DerivedTransformText(DerivedTransform t) => t.Kind == DerivedKind.Decompressed
        ? Loc.Format("Derived_Transform_Decompressed", CompressionFormatName(t.Format))
        : Loc.Format("Derived_Transform_Decrypted", CipherAlgorithmName(t.Algorithm) + (t.Mode == CipherMode.None ? string.Empty : " " + CipherModeName(t.Mode)));

    /// <summary>展開・復号した内容を、派生ドキュメントの新しいタブで開く。</summary>
    private void OpenDerivedDocument(DocumentViewModel parent, Document target, EditContent content, long start, long length, DerivedTransform transform)
    {
        int depth = (parent.Derived?.Depth ?? 0) + 1;
        if (depth > DerivedLink.MaxDepth)
        {
            // 入れ子は 8 段まで (仕様 8)。
            content.Dispose();
            target.Dispose();
            ShowNotice(Loc.Format("Derived_TooDeep", DerivedLink.MaxDepth), InfoBarSeverity.Error, parent);
            return;
        }

        target.InsertContent(0, content, Loc.Get(transform.Kind == DerivedKind.Decompressed ? "Operation_Decompress" : "Operation_Decrypt"));
        target.ClearHistory();
        DocumentViewModel vm = Vm.AddDerived(target, parent.DisplayName, parent);
        vm.DerivedParent = parent;
        vm.Derived = new DerivedLink(parent.Document, start, length, transform, depth);
        UpdateDerivedLabels(vm);
        Vm.Selected = vm;
    }

    private static void UpdateDerivedLabels(DocumentViewModel vm)
    {
        if (vm.Derived is not { } link)
        {
            return;
        }

        string label = Loc.Get(link.Transform.Kind == DerivedKind.Decompressed ? "Derived_Label_Decompressed" : "Derived_Label_Decrypted");
        vm.RangeLabel = DerivedRangeLabel(link.Offset, link.Length, label);
        string parentName = vm.DerivedParent?.DisplayName ?? vm.DisplayName;
        vm.DerivedBandText = Loc.Format("Derived_Band", parentName, StatusFormat.Hex(link.Offset), StatusFormat.Hex(link.Offset + Math.Max(0, link.Length - 1)),
            DerivedTransformText(link.Transform));
    }

    private void DerivedWriteBack_Click(object sender, RoutedEventArgs e) => _ = Commands.ExecuteAsync("data.derived.writeBack");

    /// <summary>「元に書き戻す」(仕様 3〜6)。</summary>
    private async Task WriteBackAsync()
    {
        if (Vm.Selected is not { Derived: { } link } doc)
        {
            return;
        }

        if (link.IsDetached || doc.DerivedParent is not { } parent)
        {
            ShowNotice(Loc.Get("Derived_ParentClosed"), InfoBarSeverity.Warning, doc);
            return;
        }

        if (!EnsureEditable(parent))
        {
            return;
        }

        // 元の範囲が別の編集で変わっていれば確かめる (仕様 5)。
        if (link.RangeChanged())
        {
            ContentDialog confirm = DialogParts.Dialog(Root, "DerivedChangedDialog", Loc.Get("Derived_WriteBackTitle"),
                new TextBlock { Text = Loc.Get("Derived_RangeChanged"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                Loc.Get("Derived_Overwrite"));
            confirm.DefaultButton = ContentDialogButton.Close;
            if (await ShowEditDialogAsync(confirm) != ContentDialogResult.Primary)
            {
                return;
            }
        }

        byte[]? key = null;
        byte[]? nonce = null;
        if (link.Transform.Kind == DerivedKind.Decrypted)
        {
            // 鍵は保持しないため、改めて入力させる (仕様 6)。
            if (await ShowWriteBackKeyDialogAsync(link.Transform) is not var (k, n) || k is null)
            {
                return;
            }

            key = k;
            nonce = n;
        }

        try
        {
            DocumentSnapshot source = doc.Document.DataSnapshot;
            Document parentDocument = parent.Document;
            EditContent? content = await RunTransformAsync(parent, Loc.Get("Operation_WriteBack"), source.Length, source.Length > CodecLongRunning,
                op => link.BuildWriteBack(source, ContentSink.For(parentDocument, CheckTempSpace(parentDocument)), key, nonce, op));
            if (content is null)
            {
                return;
            }

            if (content.Length != link.Length && !parent.Document.CanResize)
            {
                // 長さが変わり、元のドキュメントが固定長なら書き戻さない (仕様 3)。
                content.Dispose();
                ShowNotice(Loc.Format("Derived_FixedLength", StatusFormat.Number(link.Length, Culture), StatusFormat.Number(content.Length, Culture)),
                    InfoBarSeverity.Error, doc);
                return;
            }

            if (!ApplyEdit(parent, () => link.Apply(content, "元に書き戻す")))
            {
                content.Dispose();
                return;
            }

            UpdateDerivedLabels(doc);
            ShowNotice(Loc.Get("Derived_Written"), InfoBarSeverity.Success, doc);
            if (nonce is not null)
            {
                // 新しいナンスは表示するだけ (ログ・設定には書かない。EDIT-40 の仕様 7)。データの形式に合わせて利用者が書き込む。
                await ShowValueAsync("DerivedNonceDialog", Loc.Get("Derived_WriteBackTitle"), "Derived_NewNonceValue", Loc.Get("Derived_NewNonceValue"), nonce);
            }
        }
        catch (CipherInputException)
        {
            ShowNotice(Loc.Get("Cipher_Error_Settings"), InfoBarSeverity.Error, doc);
        }
        catch (CompressionTooLargeException ex)
        {
            ShowNotice(Loc.Format("Compress_Error_Lz4BlockLimit", GigaBytes(ex.Limit)), InfoBarSeverity.Error, doc);
        }
        finally
        {
            if (key is not null)
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    /// <summary>
    /// 書き戻しの鍵の入力 (Hex またはパスフレーズ)。GCM・ChaCha20-Poly1305 では新しいナンスを生成するかを選ばせる (既定は生成する。仕様 6)。
    /// それ以外の IV は開いたときと同じ値を使う。
    /// </summary>
    private async Task<(byte[]? Key, byte[]? Nonce)?> ShowWriteBackKeyDialogAsync(DerivedTransform t)
    {
        bool aead = CipherCatalog.IsAuthenticated(t.Algorithm, t.Mode);
        RadioButton keyHex = DialogParts.Radio("Derived_KeyHex", Loc.Get("Cipher_KeyHex"), "DerivedKey", true);
        RadioButton keyPass = DialogParts.Radio("Derived_KeyPassphrase", Loc.Get("Cipher_KeyPassphrase"), "DerivedKey", false);
        PasswordBox key = Secret("Derived_Key", Loc.Get("Cipher_Key"));
        PasswordBox passphrase = Secret("Derived_Passphrase", Loc.Get("Cipher_Passphrase"));
        TextBox salt = DialogParts.Field("Derived_Salt", Loc.Get("Cipher_Salt"));
        var iterations = new NumberBox { Header = Loc.Get("Cipher_Iterations"), Minimum = 1, Maximum = 100_000_000, Value = CipherCatalog.DefaultIterations, MinWidth = 200 };
        AutomationProperties.SetAutomationId(iterations, "Derived_Iterations");
        RadioButton newNonce = DialogParts.Radio("Derived_NewNonce", Loc.Get("Derived_NewNonce"), "DerivedNonce", true);
        RadioButton sameNonce = DialogParts.Radio("Derived_SameNonce", Loc.Get("Derived_SameNonce"), "DerivedNonce", false);
        TextBlock nonceNote = DialogParts.Caption("Derived_NonceNote");
        nonceNote.Text = Loc.Get("Derived_NonceNoteText");
        nonceNote.TextWrapping = TextWrapping.Wrap;
        TextBlock error = DialogParts.Caption("Derived_KeyError");
        error.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        var body = new StackPanel { Spacing = 8, MinWidth = 420 };
        body.Children.Add(new TextBlock { Text = Loc.Format("Derived_KeyPrompt", DerivedTransformText(t)), TextWrapping = TextWrapping.Wrap });
        foreach (UIElement e in new UIElement[] { keyHex, keyPass, key, passphrase, iterations, salt })
        {
            body.Children.Add(e);
        }

        if (aead)
        {
            body.Children.Add(newNonce);
            body.Children.Add(sameNonce);
            body.Children.Add(nonceNote);
        }

        body.Children.Add(error);
        ContentDialog dialog = DialogParts.Dialog(Root, "DerivedKeyDialog", Loc.Get("Derived_WriteBackTitle"), body, Loc.Get("Derived_WriteBackRun"));
        void Update()
        {
            bool hex = keyHex.IsChecked == true;
            key.Visibility = hex ? Visibility.Visible : Visibility.Collapsed;
            passphrase.Visibility = iterations.Visibility = salt.Visibility = hex ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsPrimaryButtonEnabled = hex ? key.Password.Length > 0 : passphrase.Password.Length > 0;
        }

        keyHex.Checked += (_, _) => Update();
        keyPass.Checked += (_, _) => Update();
        key.PasswordChanged += (_, _) => Update();
        passphrase.PasswordChanged += (_, _) => Update();
        Update();
        byte[]? keyBytes = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            int length = CipherCatalog.DerivedKeyLength(t.Algorithm);
            if (keyHex.IsChecked == true)
            {
                keyBytes = ParseHex(key.Password);
                bool valid = keyBytes is not null && (t.Algorithm == CipherAlgorithm.Rc4 ? keyBytes.Length is >= 5 and <= 256 : CipherCatalog.KeyLengths(t.Algorithm).Contains(keyBytes.Length));
                if (!valid)
                {
                    error.Text = keyBytes is null ? Loc.Get("Cipher_Error_KeyHex")
                        : Loc.Format("Cipher_Error_KeyLength", CipherAlgorithmName(t.Algorithm), length, keyBytes.Length);
                    if (keyBytes is not null)
                    {
                        CryptographicOperations.ZeroMemory(keyBytes);
                    }

                    keyBytes = null;
                    args.Cancel = true;
                }

                return;
            }

            if (ParseHex(salt.Text) is not { Length: > 0 } saltBytes)
            {
                // 書き戻しでは、暗号化したときのソルトが必要。
                error.Text = Loc.Get("Derived_SaltRequired");
                args.Cancel = true;
                return;
            }

            keyBytes = CipherCatalog.DeriveKey(passphrase.Password, ref saltBytes, (int)iterations.Value, length);
        };

        _codecDialogState = () => new System.Text.Json.Nodes.JsonObject { ["open"] = true, ["kind"] = "writeBackKey", ["aead"] = aead, ["error"] = error.Text };
        _codecDialogSet = (field, value) =>
        {
            switch (field)
            {
                case "key":
                    keyHex.IsChecked = true;
                    key.Password = value;
                    break;
                case "nonce":
                    (value == "same" ? sameNonce : newNonce).IsChecked = true;
                    break;
            }
        };
        ContentDialogResult result;
        try
        {
            result = await ShowEditDialogAsync(dialog);
        }
        finally
        {
            _codecDialogState = null;
            _codecDialogSet = null;
            key.Password = string.Empty;
            passphrase.Password = string.Empty;
        }

        if (result != ContentDialogResult.Primary || keyBytes is null)
        {
            if (keyBytes is not null)
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }

            return null;
        }

        byte[]? nonce = aead && newNonce.IsChecked == true ? CipherCatalog.RandomIv(t.Algorithm, t.Mode) : null;
        return (keyBytes, nonce);
    }
}
