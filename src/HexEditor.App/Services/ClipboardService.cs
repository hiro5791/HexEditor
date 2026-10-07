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
    public const long SystemLimit = 64L * 1024 * 1024;

    private const string BinaryFormat = "HexEditor.Binary";
    private const string MetaFormat = "HexEditor.Meta";

    private static readonly string InstanceId = Guid.NewGuid().ToString("N");

    /// <summary>アプリ内クリップボード (EDIT-24)。範囲の参照だけを持つ。</summary>
    public InAppClipboard InApp { get; } = new();

    /// <summary>
    /// 選択範囲をコピーする。Hex 列なら Hex 文字列、テキスト列なら文字列もテキストとして入れる。
    /// </summary>
    /// <returns>システムのクリップボードに実データを入れられなかった (上限を超えた) 場合は false。</returns>
    public async Task<bool> CopyAsync(EditorState editor)
    {
        if (!editor.HasSelection)
        {
            return true;
        }

        long offset = editor.SelectionStart;
        long length = editor.SelectionLength;
        DocumentSnapshot snapshot = editor.Document.Current;
        long serial = InApp.Copy(editor.Document, offset, length).Serial;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        string meta = JsonSerializer.Serialize(new { instance = InstanceId, serial, offset, length, name = editor.Document.Source.DisplayName });
        package.SetData(MetaFormat, meta);
        bool withinLimit = length <= SystemLimit;
        if (withinLimit)
        {
            byte[] bytes = new byte[length];
            await Task.Run(() => snapshot.Read(offset, bytes));
            package.SetData(BinaryFormat, await ToStreamAsync(bytes));
            string text = editor.FormatForClipboard(bytes);

            // テキスト形式は生成後の文字数 × 2 バイトで上限と比べる (EDIT-22 の仕様 4・6)。
            if ((long)text.Length * 2 <= SystemLimit)
            {
                package.SetText(text);
            }
        }
        else
        {
            package.SetText(Loc.Format("Clipboard_TooLarge", length.ToString("N0")));
        }

        SetContentWithRetry(package);
        return withinLimit;
    }

    /// <summary>貼り付け (EDIT-23)。<paramref name="overwrite"/> は上書き貼り付け (Ctrl+B)。</summary>
    public async Task<PasteOutcome> PasteAsync(EditorState editor, bool overwrite)
    {
        DataPackageView view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();

        // (1) アプリ内クリップボード: Meta が今のアプリの最後のコピーと一致すれば、範囲の参照で貼る (一致しなければ破棄する)。
        if (view.Contains(MetaFormat) && InApp.Current is not null && await view.GetDataAsync(MetaFormat) is string meta)
        {
            using JsonDocument json = JsonDocument.Parse(meta);
            if (json.RootElement.GetProperty("instance").GetString() == InstanceId
                && InApp.Match(json.RootElement.GetProperty("serial").GetInt64()) is { } clip)
            {
                return Map(Truncate(editor, allow => editor.Paste(clip.Range, overwrite, allow)));
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
            return Map(Truncate(editor, allow => editor.Paste(data, overwrite, allow)));
        }

        // (3) テキスト: テキスト列なら文字コードで変換、Hex 列なら Hex 文字列として読み、読めなければテキストとして貼る。
        if (view.Contains(StandardDataFormats.Text))
        {
            string text = await view.GetTextAsync();
            return Map(Truncate(editor, allow => editor.PasteText(text, overwrite, allow)));
        }

        return PasteOutcome.Nothing;
    }

    /// <summary>
    /// 固定長ドキュメントで末尾を越える貼り付け。ENG-07 の仕様 5 の確認ダイアログ (EditorState.PasteOverflow の N バイト) は
    /// UI で未実装のため、今は確認せずに末尾まで貼る (以前の動作)。
    /// </summary>
    private static EditResult Truncate(EditorState editor, Func<bool, EditResult> paste)
    {
        EditResult result = paste(false);
        return result == EditResult.NeedsTruncateConfirmation ? paste(true) : result;
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
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
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
