using System.Text.Json;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace HexEditor.App.Services;

/// <summary>貼り付けの結果。UI はこれを見て InfoBar を出す。</summary>
public enum PasteOutcome
{
    Done,
    Nothing,
    NotHex,

    /// <summary>Hex 列に Hex として解釈できないテキストをテキストとして貼った (EDIT-23 の仕様 2。InfoBar と「元に戻す」)。</summary>
    PastedAsText,
    NotEncodable,
    Truncated,
    FixedLength,
    NotEditable,

    /// <summary>
    /// Hex 列に Hex として読めないテキストを貼ろうとしたが、他の形式 (Base64 など) に当てはまる (EDIT-23 の仕様 2)。何もしていない。
    /// 呼び出し側は「形式を選択して貼り付け」のダイアログを、<see cref="ClipboardService.LastSpecialText"/> で開く。
    /// </summary>
    NeedsSpecialPaste,

    /// <summary>エクスプローラーでコピーしたファイルがある (EDIT-23 の仕様 1 の 4)。<see cref="ClipboardService.LastFiles"/> の内容を挿入する。</summary>
    Files,

    /// <summary>
    /// 矩形の挿入の貼り付けで、行数 (<see cref="ClipboardService.LastRectangleInsertRows"/>) が上限を超える (EDIT-17 の仕様 6)。何もしていない。
    /// </summary>
    TooManyRows,
}

/// <summary>「形式を選択して貼り付け」で使うクリップボードの内容 (EDIT-26)。</summary>
/// <param name="Text">テキスト形式。</param>
/// <param name="Binary">バイナリ形式 (`HexEditor.Binary`、アプリ内クリップボード、他のエディタの形式)。</param>
/// <param name="Files">エクスプローラーでコピーしたファイルのパス。</param>
public sealed record SpecialClipboard(string? Text, byte[]? Binary, IReadOnlyList<string> Files);

/// <summary>
/// システムのクリップボードとアプリ内クリップボード (EDIT-22〜EDIT-24)。システムのクリップボードには上限 (既定 64 MiB) までの
/// 実データと、どのコピーかを示す `HexEditor.Meta` を入れる。上限を超える範囲はアプリ内クリップボードの参照だけで貼り付ける。
/// </summary>
public sealed partial class ClipboardService
{
    /// <summary>システムのクリップボードに入れる最大サイズ (EDIT-22 の仕様 4。設定「クリップボードに入れる最大サイズ」、既定 64 MiB)。</summary>
    public static long SystemLimit => App.Settings is { } settings
        ? EditingSettings.ClipboardLimitBytes(settings.GetInt(EditingSettings.ClipboardMaxSizeKey, EditingSettings.DefaultClipboardMaxMiB))
        : ClipboardPlan.DefaultLimit;

    private const string BinaryFormat = ClipboardPlan.BinaryFormat;
    private const string MetaFormat = ClipboardPlan.MetaFormat;

    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    /// <summary>アプリ内クリップボード (EDIT-24)。範囲の参照だけを持つ。</summary>
    public InAppClipboard InApp { get; } = new();

    /// <summary>直前の貼り付けで、末尾を越えるため書かなかったバイト数 (EDIT-23 の仕様 5 の InfoBar の N)。</summary>
    public long LastTruncatedBytes { get; private set; }

    /// <summary><see cref="PasteOutcome.NeedsSpecialPaste"/> のときのテキスト。</summary>
    public string? LastSpecialText { get; private set; }

    /// <summary><see cref="PasteOutcome.Files"/> のときのファイル。</summary>
    public IReadOnlyList<string> LastFiles { get; private set; } = [];

    /// <summary>設定「他のエディタ互換の形式でもコピーする」(EDIT-27 の仕様 4。既定オン)。</summary>
    public bool CompatFormatsEnabled { get; set; } = true;

    /// <summary>設定「Hex 列で自動判別したときに確認しない」(EDIT-23 の仕様 2。既定オフ)。</summary>
    public bool PasteDetectedWithoutConfirmation { get; set; }

    /// <summary>
    /// 選択範囲をコピーする。Hex 列なら Hex 文字列、テキスト列なら文字列もテキストとして入れる。
    /// </summary>
    /// <returns>
    /// 入れた形式 (<see cref="ClipboardPlan"/>)。<see cref="ClipboardPlan.InAppOnly"/> (上限を超えた) と
    /// <see cref="ClipboardPlan.TextOmitted"/> (テキスト形式だけが上限を超えた) は、呼び出し側が InfoBar で知らせる。
    /// 選択範囲がなければ null。
    /// </returns>
    public async Task<ClipboardPlan?> CopyAsync(EditorState editor)
    {
        if (!editor.HasSelection)
        {
            return null;
        }

        long offset = editor.SelectionStart;
        long length = editor.SelectionLength;
        long serial = InApp.Copy(editor.Document, offset, length).Serial;
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        ClipboardPlan plan = await FillCopyFormatsAsync(package, editor, offset, length, serial);
        SetContentWithRetry(package);
        return plan;
    }

    /// <summary>
    /// コピー (EDIT-22 の仕様 2) と同じ形式を <paramref name="package"/> に入れる: `HexEditor.Meta`、上限以内なら `HexEditor.Binary`・
    /// 他のエディタ互換の形式 (EDIT-27)・テキスト、上限を超えればテキストの 1 行だけ (仕様 5)。テキスト形式だけが上限を超えればテキストを省く
    /// (仕様 6)。コピーと、他のアプリへのドラッグ (EDIT-18 の仕様 5。大きさの上限も同じ) で使う。
    /// </summary>
    /// <param name="serial">アプリ内クリップボードの通し番号 (ドラッグでは 0。どのコピーとも一致しない)。</param>
    public async Task<ClipboardPlan> FillCopyFormatsAsync(DataPackage package, EditorState editor, long offset, long length, long serial)
    {
        DocumentSnapshot snapshot = editor.Document.Current;
        string meta = JsonSerializer.Serialize(new { instance = InstanceId, serial, offset, length, name = editor.Document.Source.DisplayName });
        package.SetData(MetaFormat, meta);
        // 上限以内なら実データを読む (読み込みを待つことがあるため UI スレッドでは読まない)。
        byte[]? bytes = length <= SystemLimit ? await Task.Run(() => ReadSelection(snapshot, offset, length)) : null;
        string? text = null;
        ClipboardPlan plan = ClipboardPlan.For(length, () =>
        {
            // Hex 列の文字数は変換せずに分かる (上限を超える Hex 文字列を作らない)。テキスト列は文字コードで変換してから数える。
            if (editor.ActiveColumn == ActiveColumn.Hex)
            {
                return editor.Options.HexCopy.TextLength(length);
            }

            text = editor.FormatForClipboard(bytes!);
            return text.Length;
        }, SystemLimit);
        if (plan.Binary && bytes is not null)
        {
            package.SetData(BinaryFormat, await ToStreamAsync(bytes));
            await AddCompatFormatsAsync(package, bytes);
            if (plan.Text == ClipboardTextKind.Data)
            {
                package.SetText(text ?? editor.FormatForClipboard(bytes));
            }
        }
        else
        {
            package.SetText(Loc.Format("Clipboard_TooLarge", length.ToString("N0")));
        }

        return plan;
    }

    private static byte[] ReadSelection(DocumentSnapshot snapshot, long offset, long length)
    {
        byte[] bytes = new byte[length];
        snapshot.Read(offset, bytes);
        return bytes;
    }

    /// <summary>貼り付け (EDIT-23)。<paramref name="overwrite"/> は上書き貼り付け (Ctrl+B)。</summary>
    /// <param name="confirmTruncate">
    /// 固定長のドキュメントで末尾を越える場合に、越える N バイトを捨てて末尾まで貼るかを確かめる (ENG-07 の仕様 5)。
    /// null なら確かめずに末尾まで貼る。
    /// </param>
    public async Task<PasteOutcome> PasteAsync(EditorState editor, bool overwrite, Func<long, Task<bool>>? confirmTruncate = null)
    {
        LastTruncatedBytes = 0;
        LastRectangleInsertRows = 0;
        DataPackageView view = SystemClipboard.GetContent();

        // マルチ選択・矩形からコピーした内容 (要素ごと・行ごとに貼る。EDIT-07 の仕様 7、EDIT-17 の仕様 3)。
        if (await PasteRangesAsync(view, editor, overwrite) is { } ranges)
        {
            return Map(ranges);
        }

        // (1) アプリ内クリップボード: Meta が今のアプリの最後のコピーと一致すれば、範囲の参照で貼る (一致しなければ破棄する)。
        if (view.Contains(MetaFormat) && InApp.Current is not null && await view.GetDataAsync(MetaFormat) is string meta)
        {
            using JsonDocument json = JsonDocument.Parse(meta);
            if (json.RootElement.GetProperty("instance").GetString() == InstanceId
                && InApp.Match(json.RootElement.GetProperty("serial").GetInt64()) is { } clip)
            {
                // マルチ選択・矩形からの大きなコピーは、要素を連結して貼る (EDIT-22 の仕様 5・8)。
                return Map(await TruncateAsync(editor, clip.Length, confirmTruncate, allow => clip.Parts is { } parts
                    ? editor.Paste(clip.Range, parts, overwrite, allow)
                    : editor.Paste(clip.Range, overwrite, allow)));
            }
        }
        else
        {
            InApp.Clear();
        }

        // (2) バイナリ形式
        if (view.Contains(BinaryFormat) && await view.GetDataAsync(BinaryFormat) is IRandomAccessStream stream)
        {
            byte[] data = await ReadAllAsync(stream);
            return Map(await TruncateAsync(editor, data.Length, confirmTruncate, allow => editor.Paste(data, overwrite, allow)));
        }

        // (3) 他のエディタ互換の形式 (EDIT-27 の仕様 3)。
        if (await ReadCompatAsync(view) is { } compat)
        {
            return Map(await TruncateAsync(editor, compat.Length, confirmTruncate, allow => editor.Paste(compat, overwrite, allow)));
        }

        // (4) エクスプローラーでコピーしたファイル: ファイルの内容の挿入 (EDIT-30) にする。
        if (await ReadFilesAsync(view) is { Count: > 0 } files)
        {
            LastFiles = files;
            return PasteOutcome.Files;
        }

        // Hex 列で Hex として読めないテキストは、他の形式に当てはまれば確認のダイアログを出す (EDIT-23 の仕様 2・EDIT-26)。
        if (view.Contains(StandardDataFormats.Text) && editor.ActiveColumn == ActiveColumn.Hex)
        {
            string candidate = await view.GetTextAsync();
            if (HexText.TryParse(candidate) is null && candidate.Length <= PasteDetector.MaxTextChars
                && PasteDetector.Best(PasteDetector.Detect(candidate)) is { Format: not PasteFormat.Text } best)
            {
                if (!PasteDetectedWithoutConfirmation)
                {
                    LastSpecialText = candidate;
                    return PasteOutcome.NeedsSpecialPaste;
                }

                byte[] detected = best.Format is PasteFormat.Text ? best.Bytes! : PasteDetector.Parse(best.Format, candidate).Bytes ?? best.Bytes!;
                return Map(await TruncateAsync(editor, detected.Length, confirmTruncate, allow => editor.Paste(detected, overwrite, allow)));
            }
        }

        // (3) テキスト: テキスト列なら文字コードで変換、Hex 列なら Hex 文字列として読み、読めなければテキストとして貼る。
        if (view.Contains(StandardDataFormats.Text))
        {
            string text = await view.GetTextAsync();
            long length = HexText.TryParse(text)?.Length ?? (editor.TextEncoding.TryEncode(text, out byte[]? encoded) ? encoded!.Length : text.Length);
            return Map(await TruncateAsync(editor, length, confirmTruncate, allow => editor.PasteText(text, overwrite, allow)));
        }

        return PasteOutcome.Nothing;
    }

    /// <summary>
    /// 「形式を選択して貼り付け」(EDIT-26) で使う内容を読む。バイナリ形式は、アプリ内クリップボード (上限以内)、`HexEditor.Binary`、
    /// 他のエディタの形式の順に探す。
    /// </summary>
    public async Task<SpecialClipboard> ReadSpecialAsync()
    {
        DataPackageView view = SystemClipboard.GetContent();
        string? text = view.Contains(StandardDataFormats.Text) ? await view.GetTextAsync() : null;
        byte[]? binary = null;
        if (view.Contains(BinaryFormat) && await view.GetDataAsync(BinaryFormat) is IRandomAccessStream stream)
        {
            binary = await ReadAllAsync(stream);
        }

        binary ??= await ReadCompatAsync(view);
        return new SpecialClipboard(text, binary, await ReadFilesAsync(view));
    }

    /// <summary>
    /// 塗りつぶし (EDIT-29) の「クリップボードの内容」: EDIT-23 の優先順位で読む。アプリ内クリップボードは参照のまま返す
    /// (大きな範囲もデータをコピーしない)。どちらでもなければ、テキストを <paramref name="editor"/> の列と文字コードで読む。
    /// </summary>
    public async Task<(byte[]? Bytes, IByteSource? Source)> ReadForFillAsync(EditorState editor)
    {
        DataPackageView view = SystemClipboard.GetContent();
        if (view.Contains(MetaFormat) && InApp.Current is { } clip && await view.GetDataAsync(MetaFormat) is string meta)
        {
            using JsonDocument json = JsonDocument.Parse(meta);
            if (json.RootElement.GetProperty("instance").GetString() == InstanceId && json.RootElement.GetProperty("serial").GetInt64() == clip.Serial)
            {
                return (null, clip.Source);
            }
        }

        if (view.Contains(BinaryFormat) && await view.GetDataAsync(BinaryFormat) is IRandomAccessStream stream)
        {
            return (await ReadAllAsync(stream), null);
        }

        if (await ReadCompatAsync(view) is { } compat)
        {
            return (compat, null);
        }

        if (view.Contains(StandardDataFormats.Text))
        {
            string text = await view.GetTextAsync();
            if (editor.ActiveColumn == ActiveColumn.Hex && HexText.TryParse(text) is { } hex)
            {
                return (hex, null);
            }

            return (editor.TextEncoding.TryEncode(text, out byte[] bytes) ? bytes : null, null);
        }

        return (null, null);
    }

    /// <summary>
    /// 「形式を選択してコピー」(EDIT-25) の出力を入れる。HTML は HTML 形式 (CF_HTML) とテキスト、RTF は RTF とテキストの両方で入れる。
    /// </summary>
    public void CopyFormatted(string text, string? htmlFragment = null, string? rtf = null)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        if (htmlFragment is not null)
        {
            package.SetHtmlFormat(HtmlClipboard.Wrap(htmlFragment));
        }

        if (rtf is not null)
        {
            package.SetRtf(rtf);
        }

        InApp.Clear();
        SetContentWithRetry(package);
    }

    /// <summary>他のエディタ互換の形式を入れる (設定がオンのとき。EDIT-27 の仕様 4)。</summary>
    private async Task AddCompatFormatsAsync(DataPackage package, byte[] bytes)
    {
        if (!CompatFormatsEnabled)
        {
            return;
        }

        foreach ((string name, byte[] data) in CompatClipboardFormats.Encode(bytes))
        {
            package.SetData(name, await ToStreamAsync(data));
        }
    }

    /// <summary>他のエディタの形式を表の順に探す。壊れた形式は飛ばす (EDIT-27 の「エラー」)。</summary>
    private static async Task<byte[]?> ReadCompatAsync(DataPackageView view)
    {
        var raw = new Dictionary<string, byte[]>();
        foreach (CompatClipboardFormat format in CompatClipboardFormats.Formats)
        {
            if (view.Contains(format.FormatName) && await view.GetDataAsync(format.FormatName) is IRandomAccessStream s)
            {
                raw[format.FormatName] = await ReadAllAsync(s);
            }
        }

        return CompatClipboardFormats.FindForPaste(name => raw.GetValueOrDefault(name))?.Data;
    }

    private static async Task<IReadOnlyList<string>> ReadFilesAsync(DataPackageView view)
    {
        if (!view.Contains(StandardDataFormats.StorageItems))
        {
            return [];
        }

        try
        {
            IReadOnlyList<Windows.Storage.IStorageItem> items = await view.GetStorageItemsAsync();
            return [.. items.OfType<Windows.Storage.StorageFile>().Select(f => f.Path).Where(p => !string.IsNullOrEmpty(p))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return [];
        }
    }

    /// <summary>
    /// 固定長ドキュメントで末尾を越える貼り付け (ENG-07 の仕様 5)。越える分があれば確かめ、了承されたら末尾まで貼る。
    /// </summary>
    private async Task<EditResult> TruncateAsync(EditorState editor, long length, Func<long, Task<bool>>? confirm,
        Func<bool, EditResult> paste)
    {
        EditResult result = paste(false);
        if (result != EditResult.NeedsTruncateConfirmation)
        {
            return result;
        }

        long overflow = editor.PasteOverflow(length);
        if (confirm is not null && !await confirm(overflow))
        {
            return EditResult.Ignored;
        }

        LastTruncatedBytes = overflow;
        return paste(true);
    }

    private static PasteOutcome Map(EditResult result) => result switch
    {
        EditResult.Done => PasteOutcome.Done,
        EditResult.PastedAsText => PasteOutcome.PastedAsText,
        EditResult.NotEncodable => PasteOutcome.NotEncodable,
        EditResult.Truncated => PasteOutcome.Truncated,
        EditResult.FixedLength => PasteOutcome.FixedLength,
        EditResult.NotEditable => PasteOutcome.NotEditable,
        EditResult.TooManyRows => PasteOutcome.TooManyRows,
        EditResult.NeedsTruncateConfirmation => PasteOutcome.Nothing,
        _ => PasteOutcome.Nothing,
    };

    /// <summary>他のアプリがクリップボードを開いている場合に備え、50 ms 間隔で最大 10 回試す (EDIT-22 の仕様 9)。</summary>
    private static void SetContentWithRetry(DataPackage package)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                SystemClipboard.SetContent(package);
                return;
            }
            catch (Exception) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static async Task<IRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        return stream;
    }

    private static async Task<byte[]> ReadAllAsync(IRandomAccessStream stream)
    {
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size = (uint)stream.Size;
        await reader.LoadAsync(size);
        byte[] bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
