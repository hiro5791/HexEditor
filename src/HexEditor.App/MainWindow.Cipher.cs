using System.Security.Cryptography;
using System.Text.Json.Nodes;
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
/// データ > 暗号化… / 復号… (EDIT-40)。暗号は .NET の System.Security.Cryptography (CNG) を使う。鍵・パスフレーズ・IV は設定・履歴・復旧用データ・
/// ログに保存せず、ダイアログを閉じて処理が終わったら鍵のバイト列を 0 で消す (仕様 7)。記憶するのはアルゴリズム・モード・パディングの選択だけ。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>記憶する選択 (アルゴリズム・モード・パディング。state.json)。</summary>
    private const string CipherStateKey = "data.cipher.dialog";

    private sealed record CipherRequest(CipherParameters Parameters, bool NewTab, byte[]? GeneratedSalt);

    internal static string CipherAlgorithmName(CipherAlgorithm a) => Loc.Get("Cipher_Algorithm_" + a);

    internal static string CipherModeName(CipherMode m) => Loc.Get("Cipher_Mode_" + m);

    private static string CipherPaddingName(CipherPadding p) => Loc.Get("Cipher_Padding_" + p);

    private async Task CipherAsync(bool encrypt)
    {
        if (Vm.Selected is not { } doc || CodecTarget(doc) is not var (start, length))
        {
            return;
        }

        if (await ShowCipherDialogAsync(doc, length, encrypt) is not { } request)
        {
            return;
        }

        CipherParameters p = request.Parameters;
        try
        {
            if (request.GeneratedSalt is { } salt)
            {
                // ソルトを空欄にしたときは 16 バイトの乱数を作った (仕様 2)。復号に必要なので表示する。
                ShowNotice(Loc.Format("Cipher_SaltGenerated", DialogParts.Hex(salt)), InfoBarSeverity.Informational, doc);
            }

            await RunCipherAsync(doc, start, length, p, encrypt, request.NewTab);
        }
        finally
        {
            p.Dispose();
        }
    }

    private async Task RunCipherAsync(DocumentViewModel doc, long start, long length, CipherParameters p, bool encrypt, bool newTab)
    {
        string name = Loc.Get(encrypt ? "Operation_Encrypt" : "Operation_Decrypt");
        DocumentSnapshot snapshot = doc.Document.Current;
        Document? target = newTab ? Vm.CreateDerivedDocument(doc.DisplayName) : null;
        Document sinkOwner = target ?? doc.Document;
        CipherResult? result;
        try
        {
            result = await RunTransformAsync(doc, name, length, length > CodecLongRunning,
                op => CipherEngine.Run(snapshot, start, length, p, encrypt, ContentSink.For(sinkOwner, CheckTempSpace(sinkOwner)), op));
        }
        catch (CipherPaddingException)
        {
            target?.Dispose();

            // 「パディングが正しくありません」。「パディングなしで復号する」を付ける (「エラー」)。
            ContentDialog dialog = DialogParts.Dialog(Root, "CipherPaddingDialog", Loc.Get("Cipher_DecryptTitle"),
                new TextBlock { Text = Loc.Get("Cipher_Error_Padding"), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                Loc.Get("Cipher_DecryptWithoutPadding"));
            dialog.DefaultButton = ContentDialogButton.Close;
            if (await ShowEditDialogAsync(dialog) == ContentDialogResult.Primary)
            {
                using CipherParameters raw = p.WithoutPadding();
                await RunCipherAsync(doc, start, length, raw, encrypt, newTab);
            }

            return;
        }
        catch (CipherAuthenticationException)
        {
            target?.Dispose();
            ShowNotice(Loc.Get("Cipher_Error_Authentication"), InfoBarSeverity.Error, doc);
            return;
        }
        catch (CipherInputException)
        {
            target?.Dispose();
            ShowNotice(Loc.Get("Cipher_Error_Settings"), InfoBarSeverity.Error, doc);
            return;
        }

        if (result is null)
        {
            target?.Dispose();
            return;
        }

        if (target is not null)
        {
            if (encrypt)
            {
                target.InsertContent(0, result.Content, name);
                target.ClearHistory();
                DocumentViewModel vm = Vm.AddDerived(target, doc.DisplayName, doc);
                vm.RangeLabel = DerivedRangeLabel(start, length, Loc.Get("Derived_Label_Encrypted"));
                Vm.Selected = vm;
            }
            else
            {
                var transform = new DerivedTransform
                {
                    Kind = DerivedKind.Decrypted,
                    Algorithm = p.Algorithm,
                    Mode = p.Mode,
                    Padding = p.Padding,
                    Iv = (byte[])p.Iv.Clone(),
                    Aad = (byte[])p.Aad.Clone(),
                    TagAtEnd = p.TagFromSelectionEnd,
                };
                OpenDerivedDocument(doc, target, result.Content, start, length, transform);
            }
        }
        else
        {
            ApplyCodecResult(doc, start, length, result.Content, encrypt ? "暗号化" : "復号");
        }

        if (result.Tag is { } tag)
        {
            // 認証タグを「別の欄に表示するだけ」(仕様 5)。
            await ShowTagAsync(tag);
        }
    }

    private Task ShowTagAsync(byte[] tag) =>
        ShowValueAsync("CipherTagDialog", Loc.Get("Cipher_EncryptTitle"), "Cipher_TagResult", Loc.Get("Cipher_TagResult"), tag);

    /// <summary>値 (認証タグ・新しいナンス) を、コピーできる読み取り専用の欄で示す (通知はログに残るため使わない)。</summary>
    private async Task ShowValueAsync(string dialogId, string title, string fieldId, string header, byte[] value)
    {
        TextBox box = DialogParts.Field(fieldId, header, DialogParts.Hex(value));
        box.IsReadOnly = true;
        ContentDialog dialog = DialogParts.Dialog(Root, dialogId, title, box, Loc.Get("Common_Close"));
        dialog.CloseButtonText = string.Empty;
        await ShowEditDialogAsync(dialog);
    }

    /// <summary>暗号化・復号のダイアログ。閉じたら入力欄 (鍵・パスフレーズ・IV) を空にする。</summary>
    private async Task<CipherRequest?> ShowCipherDialogAsync(DocumentViewModel doc, long length, bool encrypt)
    {
        IReadOnlyList<CipherAlgorithm> algorithms = CipherCatalog.Algorithms;
        ComboBox algorithm = ItemCombo("Cipher_Algorithm", Loc.Get("Cipher_Algorithm"), algorithms.Select(CipherAlgorithmName), 0);
        for (int i = 0; i < algorithms.Count; i++)
        {
            if (!CipherCatalog.IsSupported(algorithms[i]) && algorithm.Items[i] is ComboBoxItem item)
            {
                item.IsEnabled = false;
            }
        }

        TextBlock algorithmNote = DialogParts.Caption("Cipher_AlgorithmNote");
        algorithmNote.TextWrapping = TextWrapping.Wrap;
        ComboBox mode = DialogParts.Combo("Cipher_Mode", Loc.Get("Cipher_Mode"), [], -1);
        RadioButton keyHex = DialogParts.Radio("Cipher_KeyHex", Loc.Get("Cipher_KeyHex"), "CipherKey", true);
        RadioButton keyPass = DialogParts.Radio("Cipher_KeyPassphrase", Loc.Get("Cipher_KeyPassphrase"), "CipherKey", false);
        PasswordBox key = Secret("Cipher_Key", Loc.Get("Cipher_Key"));
        PasswordBox passphrase = Secret("Cipher_Passphrase", Loc.Get("Cipher_Passphrase"));
        CheckBox show = DialogParts.Check("Cipher_ShowKey", Loc.Get("Cipher_ShowKey"), false);
        var iterations = new NumberBox
        {
            Header = Loc.Get("Cipher_Iterations"),
            Minimum = 1,
            Maximum = 100_000_000,
            Value = CipherCatalog.DefaultIterations,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 200,
        };
        AutomationProperties.SetAutomationId(iterations, "Cipher_Iterations");
        AutomationProperties.SetName(iterations, Loc.Get("Cipher_Iterations"));
        TextBox salt = DialogParts.Field("Cipher_Salt", Loc.Get("Cipher_Salt"));
        salt.PlaceholderText = Loc.Get("Cipher_SaltPlaceholder");
        TextBox iv = DialogParts.Field("Cipher_Iv", Loc.Get("Cipher_Iv"));
        var randomIv = new Button { Content = Loc.Get("Cipher_RandomIv"), VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetAutomationId(randomIv, "Cipher_RandomIv");
        ComboBox padding = DialogParts.Combo("Cipher_Padding", Loc.Get("Cipher_Padding"), Enum.GetValues<CipherPadding>().Select(CipherPaddingName), 0);
        TextBox aad = DialogParts.Field("Cipher_Aad", Loc.Get("Cipher_Aad"));
        RadioButton tagAppend = DialogParts.Radio("Cipher_TagAppend", Loc.Get("Cipher_TagAppend"), "CipherTag", true);
        RadioButton tagSeparate = DialogParts.Radio("Cipher_TagSeparate", Loc.Get("Cipher_TagSeparate"), "CipherTag", false);
        RadioButton tagFromEnd = DialogParts.Radio("Cipher_TagFromEnd", Loc.Get("Cipher_TagFromEnd"), "CipherTagIn", true);
        RadioButton tagHex = DialogParts.Radio("Cipher_TagHex", Loc.Get("Cipher_TagHex"), "CipherTagIn", false);
        TextBox tag = DialogParts.Field("Cipher_Tag", Loc.Get("Cipher_Tag"));
        RadioButton replace = DialogParts.Radio("Codec_DestReplace", Loc.Get("Codec_DestReplace"), "CodecDest", true);
        RadioButton newTab = DialogParts.Radio("Codec_DestNewTab", Loc.Get("Codec_DestNewTab"), "CodecDest", false);
        TextBlock destNote = DialogParts.Caption("Codec_DestNote");
        TextBlock error = DialogParts.Caption("Cipher_Error");
        error.TextWrapping = TextWrapping.Wrap;
        error.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        TextBlock insecure = DialogParts.Caption("Cipher_Insecure");
        insecure.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        insecure.Text = Loc.Get("Cipher_InsecureText");

        var hexPanel = new StackPanel { Spacing = 4, Children = { key } };
        var passPanel = new StackPanel { Spacing = 4, Children = { passphrase, iterations, salt } };
        var ivPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { iv } };
        if (encrypt)
        {
            ivPanel.Children.Add(randomIv);
        }

        iv.MinWidth = 320;
        var tagPanel = new StackPanel { Spacing = 4 };
        if (encrypt)
        {
            tagPanel.Children.Add(tagAppend);
            tagPanel.Children.Add(tagSeparate);
        }
        else
        {
            tagPanel.Children.Add(tagFromEnd);
            tagPanel.Children.Add(tagHex);
            tagPanel.Children.Add(tag);
        }

        var body = new StackPanel { Spacing = 8, MinWidth = 440 };
        foreach (UIElement e in new UIElement[]
        {
            algorithm, algorithmNote, insecure, mode, new TextBlock { Text = Loc.Get("Cipher_KeySource") }, keyHex, keyPass, hexPanel, passPanel, show,
            ivPanel, padding, aad, tagPanel, new TextBlock { Text = Loc.Get("Codec_Destination") }, replace, newTab, destNote, error,
        })
        {
            body.Children.Add(e);
        }

        ContentDialog dialog = DialogParts.Dialog(Root, "CipherDialog", Loc.Get(encrypt ? "Cipher_EncryptTitle" : "Cipher_DecryptTitle"),
            new ScrollViewer { Content = body, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) }, Loc.Get(encrypt ? "Cipher_Encrypt" : "Cipher_Decrypt"));

        // 前回の選択 (アルゴリズム・モード・パディングだけ)。
        JsonObject state = LoadDialogState(CipherStateKey);
        if (state["algorithm"]?.GetValue<string>() is { } a && Enum.TryParse(a, out CipherAlgorithm savedAlgorithm)
            && CipherCatalog.IsSupported(savedAlgorithm) && CipherCatalog.FitsLength(savedAlgorithm, CipherCatalog.Modes(savedAlgorithm)[0], length))
        {
            algorithm.SelectedIndex = algorithms.ToList().IndexOf(savedAlgorithm);
        }

        if (state["padding"]?.GetValue<string>() is { } pd && Enum.TryParse(pd, out CipherPadding savedPadding))
        {
            padding.SelectedIndex = (int)savedPadding;
        }

        string? savedMode = state["mode"]?.GetValue<string>();
        List<CipherMode> modes = [];
        CipherAlgorithm? modesFor = null;
        bool changesLength = true;
        CipherValidation validation = default;

        CipherAlgorithm CurrentAlgorithm() => algorithms[Math.Max(0, algorithm.SelectedIndex)];
        CipherMode CurrentMode() => mode.SelectedIndex >= 0 && mode.SelectedIndex < modes.Count ? modes[mode.SelectedIndex] : CipherMode.None;

        void FillModes()
        {
            CipherAlgorithm alg = CurrentAlgorithm();
            if (modesFor == alg)
            {
                return;
            }

            CipherMode previous = CurrentMode();
            modesFor = alg;
            modes = [.. CipherCatalog.Modes(alg)];
            mode.Items.Clear();
            foreach (CipherMode m in modes)
            {
                // 認証付き暗号は 1 GiB までのため、超える場合は選べない (「巨大ファイル」2)。
                var item = new ComboBoxItem { Content = CipherModeName(m), IsEnabled = CipherCatalog.FitsLength(alg, m, length) };
                mode.Items.Add(item);
            }

            int index = modes.IndexOf(savedMode is not null && Enum.TryParse(savedMode, out CipherMode s) ? s : previous);
            savedMode = null;
            if (index < 0 || !((ComboBoxItem)mode.Items[index]).IsEnabled)
            {
                index = modes.FindIndex(m => CipherCatalog.FitsLength(alg, m, length));
            }

            mode.SelectedIndex = index;
            mode.Visibility = modes.Count == 1 && modes[0] == CipherMode.None ? Visibility.Collapsed : Visibility.Visible;
        }

        void Update()
        {
            FillModes();
            CipherAlgorithm alg = CurrentAlgorithm();
            CipherMode m = CurrentMode();
            bool aead = CipherCatalog.IsAuthenticated(alg, m);
            int ivLength = CipherCatalog.IvLength(alg, m);
            hexPanel.Visibility = keyHex.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            passPanel.Visibility = keyPass.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            key.PasswordRevealMode = passphrase.PasswordRevealMode = show.IsChecked == true ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
            ivPanel.Visibility = ivLength > 0 ? Visibility.Visible : Visibility.Collapsed;
            iv.Header = Loc.Format(aead ? "Cipher_NonceBytes" : "Cipher_IvBytes", ivLength);
            padding.Visibility = CipherCatalog.UsesPadding(m) ? Visibility.Visible : Visibility.Collapsed;
            aad.Visibility = tagPanel.Visibility = aead ? Visibility.Visible : Visibility.Collapsed;
            tag.Visibility = tagHex.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            insecure.Visibility = CipherCatalog.IsInsecure(alg) ? Visibility.Visible : Visibility.Collapsed;
            algorithmNote.Text = string.Join(" ", algorithms.Where(x => !CipherCatalog.IsSupported(x)).Select(x => Loc.Format("Cipher_Unsupported", CipherAlgorithmName(x)))
                .Concat(CipherCatalog.Modes(alg).Any(x => !CipherCatalog.FitsLength(alg, x, length)) || !CipherCatalog.FitsLength(alg, m, length)
                    ? [Loc.Get("Cipher_AeadLimit")] : []));

            // 長さが変わるか (固定長ドキュメントでは「新しいタブで開く」だけ)。
            CipherPadding pad = (CipherPadding)Math.Max(0, padding.SelectedIndex);
            changesLength = (CipherCatalog.UsesPadding(m) && pad != CipherPadding.None)
                || (aead && (encrypt ? tagAppend.IsChecked == true : tagFromEnd.IsChecked == true));
            bool canReplace = CanReplace(doc, changesLength);
            replace.IsEnabled = canReplace;
            if (!canReplace)
            {
                newTab.IsChecked = true;
            }

            destNote.Text = canReplace ? string.Empty : Loc.Get(doc.Document.IsReadOnly ? "Codec_DestReadOnly" : "Codec_DestFixedLength");

            // 入力の確かめ (鍵・IV の長さなど)。パスフレーズから作る鍵は確定するときに作る。
            error.Text = string.Empty;
            byte[]? keyBytes = keyHex.IsChecked == true ? ParseHex(key.Password) : new byte[CipherCatalog.DerivedKeyLength(alg)];
            byte[]? ivBytes = ivLength > 0 ? ParseHex(iv.Text) : [];
            byte[]? aadBytes = aead ? ParseHex(aad.Text) : [];
            byte[]? tagBytes = !encrypt && aead && tagHex.IsChecked == true ? ParseHex(tag.Text) : null;
            byte[]? saltBytes = keyPass.IsChecked == true ? ParseHex(salt.Text) : [];
            DialogParts.MarkInvalid(iv, ivBytes is null);
            DialogParts.MarkInvalid(aad, aadBytes is null);
            DialogParts.MarkInvalid(tag, tagBytes is null && tagHex.IsChecked == true && aead && !encrypt);
            DialogParts.MarkInvalid(salt, saltBytes is null);
            bool ok = keyBytes is not null && ivBytes is not null && aadBytes is not null && saltBytes is not null
                && (tagBytes is not null || encrypt || !aead || tagHex.IsChecked != true) && mode.SelectedIndex >= 0;
            if (keyBytes is null)
            {
                error.Text = Loc.Get("Cipher_Error_KeyHex");
            }
            else if (ok)
            {
                using var probe = new CipherParameters
                {
                    Algorithm = alg,
                    Mode = m,
                    Padding = pad,
                    Key = keyBytes,
                    Iv = ivBytes!,
                    TagFromSelectionEnd = tagFromEnd.IsChecked == true,
                    Tag = tagBytes,
                };
                validation = CipherCatalog.Validate(probe, length, encrypt);
                if (keyPass.IsChecked == true && validation.Error == CipherInputError.KeyLength)
                {
                    validation = default;
                }

                if (keyPass.IsChecked == true && passphrase.Password.Length == 0)
                {
                    ok = false;
                }

                if (!validation.IsValid)
                {
                    error.Text = ValidationText(validation, alg, m);
                    ok = false;
                }
            }
            else if (keyBytes is not null)
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }

            if (keyHex.IsChecked == true && key.Password.Length == 0)
            {
                ok = false;
            }

            dialog.IsPrimaryButtonEnabled = ok;
        }

        algorithm.SelectionChanged += (_, _) => Update();
        mode.SelectionChanged += (_, _) => Update();
        padding.SelectionChanged += (_, _) => Update();
        foreach (RadioButton r in new[] { keyHex, keyPass, tagAppend, tagSeparate, tagFromEnd, tagHex, replace, newTab })
        {
            r.Checked += (_, _) => Update();
        }

        show.Checked += (_, _) => Update();
        show.Unchecked += (_, _) => Update();
        key.PasswordChanged += (_, _) => Update();
        passphrase.PasswordChanged += (_, _) => Update();
        foreach (TextBox box in new[] { iv, aad, tag, salt })
        {
            box.TextChanged += (_, _) => Update();
        }

        iterations.ValueChanged += (_, _) => Update();
        randomIv.Click += (_, _) => iv.Text = DialogParts.Hex(CipherCatalog.RandomIv(CurrentAlgorithm(), CurrentMode()));
        Update();

        _codecDialogState = () => new JsonObject
        {
            ["open"] = true,
            ["kind"] = encrypt ? "encrypt" : "decrypt",
            ["algorithm"] = CurrentAlgorithm().ToString(),
            ["mode"] = CurrentMode().ToString(),
            ["padding"] = ((CipherPadding)Math.Max(0, padding.SelectedIndex)).ToString(),
            ["keyEmpty"] = key.Password.Length == 0,
            ["passphraseEmpty"] = passphrase.Password.Length == 0,
            ["ivEmpty"] = iv.Text.Length == 0,
            ["keyIsPassword"] = true,
            ["error"] = error.Text,
            ["insecure"] = insecure.Visibility == Visibility.Visible,
            ["replaceEnabled"] = replace.IsEnabled,
            ["runEnabled"] = dialog.IsPrimaryButtonEnabled,
            ["ivLength"] = CipherCatalog.IvLength(CurrentAlgorithm(), CurrentMode()),
        };
        _codecDialogSet = (field, value) =>
        {
            switch (field)
            {
                case "algorithm":
                    algorithm.SelectedIndex = algorithms.ToList().IndexOf(Enum.Parse<CipherAlgorithm>(value));
                    break;
                case "mode":
                    mode.SelectedIndex = modes.IndexOf(Enum.Parse<CipherMode>(value));
                    break;
                case "padding":
                    padding.SelectedIndex = (int)Enum.Parse<CipherPadding>(value);
                    break;
                case "key":
                    keyHex.IsChecked = true;
                    key.Password = value;
                    break;
                case "passphrase":
                    keyPass.IsChecked = true;
                    passphrase.Password = value;
                    break;
                case "salt":
                    salt.Text = value;
                    break;
                case "iterations":
                    iterations.Value = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "iv":
                    iv.Text = value;
                    break;
                case "aad":
                    aad.Text = value;
                    break;
                case "tag":
                    tagHex.IsChecked = true;
                    tag.Text = value;
                    break;
                case "tagPlacement":
                    (value == "separate" ? tagSeparate : value == "hex" ? tagHex : value == "end" ? tagFromEnd : tagAppend).IsChecked = true;
                    break;
                case "newTab":
                    (bool.Parse(value) ? newTab : replace).IsChecked = true;
                    break;
            }
        };

        CipherRequest? request = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            // 確定するときに鍵を作る (パスフレーズからの鍵は時間がかかる)。作れなければダイアログを閉じない。
            try
            {
                request = BuildCipherRequest();
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                error.Text = Loc.Get("Cipher_Error_Settings");
                args.Cancel = true;
            }
        };

        CipherRequest BuildCipherRequest()
        {
            CipherAlgorithm alg = CurrentAlgorithm();
            CipherMode m = CurrentMode();
            bool aead = CipherCatalog.IsAuthenticated(alg, m);
            byte[]? generated = null;
            byte[] keyBytes;
            if (keyHex.IsChecked == true)
            {
                keyBytes = ParseHex(key.Password) ?? throw new ArgumentException("key");
            }
            else
            {
                byte[] saltBytes = ParseHex(salt.Text) ?? throw new ArgumentException("salt");
                bool empty = saltBytes.Length == 0;
                keyBytes = CipherCatalog.DeriveKey(passphrase.Password, ref saltBytes, (int)iterations.Value, CipherCatalog.DerivedKeyLength(alg));
                generated = empty ? saltBytes : null;
            }

            var p = new CipherParameters
            {
                Algorithm = alg,
                Mode = m,
                Padding = (CipherPadding)Math.Max(0, padding.SelectedIndex),
                Key = keyBytes,
                Iv = CipherCatalog.IvLength(alg, m) > 0 ? ParseHex(iv.Text) ?? [] : [],
                Aad = aead ? ParseHex(aad.Text) ?? [] : [],
                AppendTag = tagAppend.IsChecked == true,
                TagFromSelectionEnd = tagFromEnd.IsChecked == true,
                Tag = !encrypt && aead && tagHex.IsChecked == true ? ParseHex(tag.Text) : null,
            };
            return new CipherRequest(p, newTab.IsChecked == true, generated);
        }

        ContentDialogResult result;
        try
        {
            result = await ShowEditDialogAsync(dialog);
        }
        finally
        {
            _codecDialogState = null;
            _codecDialogSet = null;

            // 鍵・パスフレーズ・IV の欄を空にする (仕様 7)。
            key.Password = string.Empty;
            passphrase.Password = string.Empty;
            iv.Text = string.Empty;
            salt.Text = string.Empty;
        }

        if (result != ContentDialogResult.Primary || request is null)
        {
            request?.Parameters.Dispose();
            return null;
        }

        SaveDialogState(CipherStateKey, new JsonObject
        {
            ["algorithm"] = request.Parameters.Algorithm.ToString(),
            ["mode"] = request.Parameters.Mode.ToString(),
            ["padding"] = ((CipherPadding)Math.Max(0, padding.SelectedIndex)).ToString(),
        });
        return request;
    }

    /// <summary>鍵の入力欄 (パスワードの入力欄。「表示」で見えるようにできる。仕様 8)。</summary>
    private static PasswordBox Secret(string automationId, string header)
    {
        var box = new PasswordBox { Header = header, FontFamily = DialogParts.Mono, MinWidth = 360 };
        AutomationProperties.SetAutomationId(box, automationId);
        AutomationProperties.SetName(box, header);
        return box;
    }

    /// <summary>Hex バイト列 (空欄は空)。読めなければ null。</summary>
    private static byte[]? ParseHex(string text) =>
        string.IsNullOrWhiteSpace(text) ? [] : Core.Clipboard.HexText.TryParse(text);

    private static string ValidationText(CipherValidation v, CipherAlgorithm alg, CipherMode mode) => v.Error switch
    {
        CipherInputError.KeyLength => Loc.Format("Cipher_Error_KeyLength", CipherAlgorithmName(alg), v.Expected, v.Actual),
        CipherInputError.IvLength => Loc.Format(CipherCatalog.IsAuthenticated(alg, mode) ? "Cipher_Error_NonceLength" : "Cipher_Error_IvLength",
            CipherModeName(mode), v.Expected, v.Actual),
        CipherInputError.TagLength => Loc.Format("Cipher_Error_TagLength", v.Expected, v.Actual),
        CipherInputError.MissingTag => Loc.Get("Cipher_Error_MissingTag"),
        CipherInputError.NotBlockMultiple => Loc.Format("Cipher_Error_BlockMultiple", v.Expected),
        CipherInputError.TooLarge => Loc.Get("Cipher_AeadLimit"),
        _ => Loc.Get("Cipher_Error_Settings"),
    };
}
