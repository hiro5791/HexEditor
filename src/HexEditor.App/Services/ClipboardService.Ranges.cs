using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Selection;
using HexEditor.Core.View;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace HexEditor.App.Services;

/// <summary>
/// マルチ選択・矩形選択のコピーと貼り付け (EDIT-07 の仕様 7、EDIT-17 の仕様 2・3)。バイナリ形式には要素を連結したバイト列を入れ、
/// `HexEditor.Meta` に要素ごとの長さ (<c>parts</c>) と、矩形なら矩形の情報 (<c>rect</c>: 行数・1 行のバイト数) を入れる。
/// </summary>
public sealed partial class ClipboardService
{
    /// <summary>要素ごとの長さを Meta に入れる要素数の上限 (それ以上は連結したデータだけを入れる)。</summary>
    private const int MaxMetaParts = 100_000;

    /// <summary>直前のマルチ選択のコピーで、連結したデータ (クリップボード履歴に入れる)。上限を超えたら null。</summary>
    public byte[]? LastCopiedRanges { get; private set; }

    /// <summary>
    /// マルチ選択・矩形をコピーする。上限 (既定 64 MiB) を超える場合は、要素を参照で持つアプリ内クリップボード (EDIT-24) に入れ、システムの
    /// クリップボードには `HexEditor.Meta` とテキストの 1 行だけを入れて <see cref="ClipboardPlan.InAppOnly"/> を返す (EDIT-22 の仕様 5・8。
    /// 呼び出し側は InfoBar と「ファイルに書き出す」を出す)。矩形の行数がマルチ選択の要素数の上限を超える場合は要素の一覧を作らずに何もせず null。
    /// </summary>
    public async Task<ClipboardPlan?> CopyRangesAsync(EditorState editor)
    {
        SelectionSnapshot selection = editor.CaptureSelection();
        long total = selection.TotalLength;
        LastCopiedRanges = null;
        if (selection.Rectangle is not null && selection.Count > editor.MaxSelectionElements)
        {
            return null;
        }

        if (total > SystemLimit)
        {
            List<ByteRange> parts = [.. selection.Ranges];
            InAppClip clip = InApp.CopyRanges(editor.Document, parts);
            var inAppMeta = new JsonObject
            {
                ["instance"] = InstanceId,
                ["serial"] = clip.Serial,
                ["offset"] = selection.Bounds.Start,
                ["length"] = total,
                ["name"] = editor.Document.Source.DisplayName,
            };
            var inAppPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            inAppPackage.SetData(MetaFormat, inAppMeta.ToJsonString());
            inAppPackage.SetText(Loc.Format("Clipboard_TooLarge", total.ToString("N0")));
            SetContentWithRetry(inAppPackage);
            return new ClipboardPlan(false, ClipboardTextKind.TooLargeLine);
        }

        DocumentSnapshot snapshot = editor.Document.Current;
        List<ByteRange> ranges = [.. selection.Ranges];
        byte[] bytes = await Task.Run(() =>
        {
            byte[] data = new byte[total];
            long at = 0;
            foreach (ByteRange r in ranges)
            {
                snapshot.Read(r.Start, data.AsSpan((int)at, (int)r.Length));
                at += r.Length;
            }

            return data;
        });

        // テキスト形式: Hex 列なら Hex 文字列、テキスト列なら文字コードで解釈した文字列。矩形は行ごとに改行で区切る (EDIT-17 の仕様 2)。
        string text;
        if (selection.Rectangle is not null)
        {
            var builder = new StringBuilder();
            long at = 0;
            foreach (ByteRange r in ranges)
            {
                if (at > 0)
                {
                    builder.Append("\r\n");
                }

                builder.Append(editor.FormatForClipboard(bytes.AsSpan((int)at, (int)r.Length)));
                at += r.Length;
            }

            text = builder.ToString();
        }
        else
        {
            text = editor.FormatForClipboard(bytes);
        }

        var meta = new JsonObject
        {
            ["instance"] = InstanceId,
            ["serial"] = 0,
            ["offset"] = selection.Bounds.Start,
            ["length"] = total,
            ["name"] = editor.Document.Source.DisplayName,
        };
        if (ranges.Count <= MaxMetaParts)
        {
            meta["parts"] = new JsonArray([.. ranges.Select(r => (JsonNode?)r.Length)]);
        }

        if (selection.Rectangle is { } rect)
        {
            meta["rect"] = new JsonObject { ["rows"] = ranges.Count, ["width"] = rect.Width };
        }

        InApp.Clear();
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetData(MetaFormat, meta.ToJsonString());
        package.SetData(BinaryFormat, await ToStreamAsync(bytes));
        await AddCompatFormatsAsync(package, bytes);
        bool textFits = text.Length <= SystemLimit / 2;
        if (textFits)
        {
            package.SetText(text);
        }

        SetContentWithRetry(package);
        LastCopiedRanges = bytes;
        return new ClipboardPlan(true, textFits ? ClipboardTextKind.Data : ClipboardTextKind.None);
    }

    /// <summary>
    /// クリップボードの内容がこのアプリのマルチ選択・矩形のコピーなら、それに合わせて貼る。矩形の情報があれば主カーソルの列を左端にして
    /// 1 行ずつ下の行に貼り (EDIT-17 の仕様 3)、要素の数が今のマルチ選択の要素の数と同じなら各要素に対応する部分を貼る (EDIT-07 の仕様 7)。
    /// どちらでもなければ null (通常の貼り付けにする)。
    /// </summary>
    private async Task<EditResult?> PasteRangesAsync(DataPackageView view, EditorState editor, bool overwrite)
    {
        if (!view.Contains(MetaFormat) || !view.Contains(BinaryFormat) || await view.GetDataAsync(MetaFormat) is not string metaText)
        {
            return null;
        }

        using JsonDocument meta = JsonDocument.Parse(metaText);
        JsonElement root = meta.RootElement;
        if (root.GetProperty("instance").GetString() != InstanceId || !root.TryGetProperty("parts", out JsonElement partsElement))
        {
            return null;
        }

        long[] parts = [.. partsElement.EnumerateArray().Select(p => p.GetInt64())];
        bool rectangle = root.TryGetProperty("rect", out _);
        bool perElement = editor.HasMultipleRanges && editor.SelectedRangeCount == parts.Length && parts.Length > 1;
        if (!rectangle && !perElement)
        {
            return null;
        }

        if (await view.GetDataAsync(BinaryFormat) is not IRandomAccessStream stream)
        {
            return null;
        }

        byte[] data = await ReadAllAsync(stream);
        var pieces = new List<byte[]>(parts.Length);
        long at = 0;
        foreach (long n in parts)
        {
            pieces.Add(data.AsSpan((int)at, (int)n).ToArray());
            at += n;
        }

        if (perElement)
        {
            return editor.ForEachSelectionElement(i => editor.Paste(pieces[i], overwrite), overwrite ? "上書き貼り付け" : "貼り付け");
        }

        // 矩形の貼り付け: 上書き貼り付け (Ctrl+B) は各行を上書き、通常の貼り付けは入力モードに従う。
        bool rowsOverwrite = overwrite || !editor.InsertMode;
        if (editor.RectanglePasteInserts(rowsOverwrite))
        {
            // 挿入は長さが変わる操作: 行数の上限 (EDIT-17 の仕様 6) を超えれば行わず、10,000 行を超えれば長時間処理にする。
            LastRectangleInsertRows = pieces.Count;
            if (pieces.Count > editor.MaxRectangleRows)
            {
                return EditResult.TooManyRows;
            }

            if (pieces.Count > EditorState.LongRunningElements && LongRectangleInsert is { } longInsert)
            {
                return await longInsert(editor, pieces);
            }
        }

        return editor.PasteRectangle(pieces, rowsOverwrite);
    }

    /// <summary>直前の貼り付けで、矩形の各行に挿入した (しようとした) 行数。挿入していなければ 0 (UI は行数と注記を示す。EDIT-17 の「画面」)。</summary>
    public long LastRectangleInsertRows { get; private set; }

    /// <summary>
    /// 行数の多い (10,000 行を超える) 矩形の挿入の貼り付けを長時間処理で行う (EDIT-17 の「巨大ファイル・長時間処理」)。ウィンドウが設定する。
    /// 引数は貼り付け先と各行の内容。
    /// </summary>
    public Func<EditorState, IReadOnlyList<byte[]>, Task<EditResult>>? LongRectangleInsert { get; set; }
}
