using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Files;
using HexEditor.Core.Inspector;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>
/// タブ 1 つ分の、ファイル本体とは別に保存するデータ (00-overview 10 章): ブックマーク (INSP-23) と、インスペクタのエンディアンの
/// 選択 (INSP-02 の仕様 1)。設定フォルダの <c>documents/</c> に保存する。無題のドキュメントでは保存しない。
/// </summary>
public sealed class DocumentAnnotations
{
    /// <summary>インスペクタの設定の付随データの種類。</summary>
    public const string InspectorKind = "inspector";

    private readonly DocumentDataStore _store;
    private InspectorEndianMode _endian;

    public DocumentAnnotations(DocumentViewModel document, DocumentDataStore store)
    {
        Document = document;
        _store = store;
        Bookmarks = BookmarkCollection.Attach(document.Document);
    }

    public DocumentViewModel Document { get; }

    public BookmarkCollection Bookmarks { get; }

    /// <summary>保存していないブックマークの変更がある。</summary>
    public bool Dirty { get; set; }

    /// <summary>インスペクタのエンディアン (ドキュメントごと)。変えたらすぐ保存する。</summary>
    public InspectorEndianMode InspectorEndian
    {
        get => _endian;
        set
        {
            if (_endian != value)
            {
                _endian = value;
                SaveInspector();
            }
        }
    }

    /// <summary>ブックマークが記録されていたが、ファイルが変わっていたので適用を待っているもの。</summary>
    public LoadedBookmarks? Pending { get; private set; }

    private string? Path => Document.FilePath;

    private FileStamp? Stamp => (Document.Document.Source as FileByteSource)?.Stamp;

    /// <summary>
    /// 記録した付随データを読む。ファイルのサイズ・更新日時が記録と違えば、ブックマークは適用せずに <see cref="Pending"/> に残す
    /// (利用者に確認してから <see cref="ApplyPending"/> する)。
    /// </summary>
    public void Load()
    {
        if (Path is not { } path)
        {
            return;
        }

        try
        {
            if (_store.ReadObject(path, InspectorKind) is { } inspector
                && Enum.TryParse(inspector.Value["endian"]?.GetValue<string>(), ignoreCase: true, out InspectorEndianMode mode))
            {
                _endian = mode;
            }

            if (BookmarkStore.Load(_store, path) is { } loaded)
            {
                if (loaded.Header.Matches(Stamp))
                {
                    BookmarkStore.Apply(Bookmarks, loaded, Document.Document.Length);
                }
                else
                {
                    Pending = loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            AppLog.Warning($"Document data: cannot read ({ex.GetType().Name}: {ex.Message})");
        }

        Dirty = false;
    }

    /// <summary>ファイルが変わっていたブックマークを、利用者の確認の後で適用する。</summary>
    public void ApplyPending()
    {
        if (Pending is { } pending)
        {
            Pending = null;
            BookmarkStore.Apply(Bookmarks, pending, Document.Document.Length);
            Dirty = true;
        }
    }

    public void DiscardPending() => Pending = null;

    /// <summary>ブックマークを保存する (変更があるとき、または <paramref name="force"/>)。</summary>
    public void SaveBookmarks(bool force = false)
    {
        if (Path is not { } path || !force && !Dirty || Pending is not null)
        {
            return;
        }

        try
        {
            BookmarkStore.Save(_store, path, Stamp, Bookmarks);
            Dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Document data: cannot write bookmarks ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private void SaveInspector()
    {
        if (Path is not { } path)
        {
            return;
        }

        try
        {
            if (_endian == InspectorEndianMode.Document)
            {
                _store.Delete(path, InspectorKind);
            }
            else
            {
                _store.WriteObject(path, InspectorKind, Stamp, new JsonObject { ["endian"] = _endian.ToString().ToLowerInvariant() });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Document data: cannot write inspector settings ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
