using System.Text.Json;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
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
}

/// <summary>
/// システムのクリップボードとアプリ内クリップボード (EDIT-22〜EDIT-24)。システムのクリップボードには上限 (既定 64 MiB) までの
/// 実データと、どのコピーかを示す `HexEditor.Meta` を入れる。上限を超える範囲はアプリ内クリップボードの参照だけで貼り付ける。
/// </summary>
public sealed class ClipboardService
{
    /// <summary>システムのクリップボードに入れる最大サイズ (EDIT-22 の仕様 4 の既定値)。</summary>
    public const long SystemLimit = ClipboardPlan.DefaultLimit;

    private const string BinaryFormat = ClipboardPlan.BinaryFormat;
    private const string MetaFormat = ClipboardPlan.MetaFormat;

    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    /// <summary>アプリ内クリップボード (EDIT-24)。範囲の参照だけを持つ。</summary>
    public InAppClipboard InApp { get; } = new();

    /// <summary>直前の貼り付けで、末尾を越えるため書かなかったバイト数 (EDIT-23 の仕様 5 の InfoBar の N)。</summary>
    public long LastTruncatedBytes { get; private set; }

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
        DocumentSnapshot snapshot = editor.Document.Current;
        long serial = InApp.Copy(editor.Document, offset, length).Serial;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
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
                return ClipboardPlan.HexTextLength(length);
            }

            text = editor.FormatForClipboard(bytes!);
            return text.Length;
        }, SystemLimit);
        if (plan.Binary && bytes is not null)
        {
            package.SetData(BinaryFormat, await ToStreamAsync(bytes));
            if (plan.Text == ClipboardTextKind.Data)
            {
                package.SetText(text ?? editor.FormatForClipboard(bytes));
            }
        }
        else
        {
            package.SetText(Loc.Format("Clipboard_TooLarge", length.ToString("N0")));
        }

        SetContentWithRetry(package);
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
        DataPackageView view = SystemClipboard.GetContent();

        // (1) アプリ内クリップボード: Meta が今のアプリの最後のコピーと一致すれば、範囲の参照で貼る (一致しなければ破棄する)。
        if (view.Contains(MetaFormat) && InApp.Current is not null && await view.GetDataAsync(MetaFormat) is string meta)
        {
            using JsonDocument json = JsonDocument.Parse(meta);
            if (json.RootElement.GetProperty("instance").GetString() == InstanceId
                && InApp.Match(json.RootElement.GetProperty("serial").GetInt64()) is { } clip)
            {
                return Map(await TruncateAsync(editor, clip.Range.Length, confirmTruncate, allow => editor.Paste(clip.Range, overwrite, allow)));
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
