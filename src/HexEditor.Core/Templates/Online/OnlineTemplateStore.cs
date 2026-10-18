using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexEditor.Core.Templates.Online;

/// <summary>インストールしたオンラインのテンプレートの記録 (<c>templates-online/installed.json</c>)。</summary>
public sealed record InstalledOnlineTemplate
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    /// <summary>入手したリポジトリの索引の URL。</summary>
    public required string Repository { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>フォルダの中のファイル名 (<c>&lt;id&gt;.hxt</c> など)。</summary>
    public required string File { get; init; }

    /// <summary>表示名 (英語。一覧で使う)。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>署名のないリポジトリから入手した (一覧に「署名なし」と表示する)。</summary>
    public bool Unsigned { get; init; }

    public bool UsesExtensions { get; init; }

    public DateTimeOffset InstalledUtc { get; init; }
}

/// <summary>新しい版のあるテンプレート (TPL-36 の仕様 5)。</summary>
public sealed record OnlineTemplateUpdate(InstalledOnlineTemplate Installed, TemplateRepositoryListing Listing, OnlineTemplateEntry Latest);

/// <summary>
/// オンラインのテンプレートのインストール先 (設定フォルダの <c>templates-online/</c>。利用者のテンプレートとは分ける。TPL-36 の仕様 6)。
/// インストールは一時ファイルに書いてから置き換える。ハッシュが合わないものはフォルダに残さない。
/// </summary>
public sealed class OnlineTemplateStore
{
    public const string ManifestName = "installed.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Lock _lock = new();
    private readonly TimeProvider _time;

    public OnlineTemplateStore(string folder, TimeProvider? time = null)
    {
        Folder = folder;
        _time = time ?? TimeProvider.System;
    }

    public string Folder { get; }

    /// <summary>インストール・削除をした。</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<InstalledOnlineTemplate> Installed
    {
        get
        {
            lock (_lock)
            {
                return [.. Load().Where(t => System.IO.File.Exists(Path.Combine(Folder, t.File)))];
            }
        }
    }

    public InstalledOnlineTemplate? Find(string id) => Installed.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>テンプレートのファイルのパスからインストールの記録を探す (一覧の「署名なし」の表示など)。</summary>
    public InstalledOnlineTemplate? FindByPath(string path) =>
        Installed.FirstOrDefault(t => string.Equals(Path.GetFullPath(Path.Combine(Folder, t.File)), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// インストールする (同じ ID の古い版は置き換える)。<paramref name="data"/> は <see cref="TemplateRepositoryClient.DownloadAsync"/>
    /// で照合したものだが、書く前にもう一度 SHA-256 を確かめる。
    /// </summary>
    public InstalledOnlineTemplate Install(TemplateRepositoryListing listing, OnlineTemplateEntry entry, byte[] data)
    {
        if (!TemplateRepositoryIndex.IsSafeId(entry.Id))
        {
            throw new TemplateTamperedException("The template id is not allowed.");
        }

        if (data.LongLength > TemplateRepositoryIndex.MaxTemplateBytes
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), entry.Sha256, StringComparison.Ordinal))
        {
            throw new TemplateTamperedException("The SHA-256 of the file does not match the index.");
        }

        string file = entry.Id + entry.FileExtension;
        var record = new InstalledOnlineTemplate
        {
            Id = entry.Id,
            Version = entry.Version,
            Repository = listing.Repository.IndexUrl.AbsoluteUri,
            Sha256 = entry.Sha256,
            File = file,
            Name = entry.Names.TryGetValue("en", out string? en) ? en : entry.Id,
            Unsigned = !listing.Signed,
            UsesExtensions = entry.UsesExtensions,
            InstalledUtc = _time.GetUtcNow(),
        };

        lock (_lock)
        {
            Directory.CreateDirectory(Folder);
            string target = Path.Combine(Folder, file);
            string temp = Path.Combine(Folder, "." + entry.Id + ".download");
            try
            {
                System.IO.File.WriteAllBytes(temp, data);
                System.IO.File.Move(temp, target, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }

            List<InstalledOnlineTemplate> list = Load();
            foreach (InstalledOnlineTemplate old in list.Where(t => string.Equals(t.Id, entry.Id, StringComparison.OrdinalIgnoreCase) && t.File != file))
            {
                TryDelete(Path.Combine(Folder, old.File));
            }

            list.RemoveAll(t => string.Equals(t.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            list.Add(record);
            Save(list);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return record;
    }

    /// <summary>削除する。</summary>
    public bool Remove(string id)
    {
        bool removed;
        lock (_lock)
        {
            List<InstalledOnlineTemplate> list = Load();
            InstalledOnlineTemplate[] matches = [.. list.Where(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))];
            foreach (InstalledOnlineTemplate t in matches)
            {
                TryDelete(Path.Combine(Folder, t.File));
                list.Remove(t);
            }

            removed = matches.Length > 0;
            if (removed)
            {
                Save(list);
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// 新しい版のあるもの (同じリポジトリの同じ ID で、版が大きいもの。最新の版を返す)。読み込めなかったリポジトリは除く。
    /// </summary>
    public IReadOnlyList<OnlineTemplateUpdate> FindUpdates(IEnumerable<TemplateRepositoryListing> listings, Func<OnlineTemplateEntry, bool>? supported = null)
    {
        var updates = new List<OnlineTemplateUpdate>();
        TemplateRepositoryListing[] ok = [.. listings.Where(l => l.Ok)];
        foreach (InstalledOnlineTemplate installed in Installed)
        {
            foreach (TemplateRepositoryListing listing in ok.Where(l => l.Repository.IndexUrl.AbsoluteUri == installed.Repository))
            {
                OnlineTemplateEntry? latest = listing.Templates
                    .Where(e => string.Equals(e.Id, installed.Id, StringComparison.OrdinalIgnoreCase) && (supported?.Invoke(e) ?? true))
                    .OrderByDescending(e => e.Version, Comparer<string>.Create(TemplateVersion.Compare))
                    .FirstOrDefault();
                if (latest is not null && TemplateVersion.Compare(latest.Version, installed.Version) > 0)
                {
                    updates.Add(new OnlineTemplateUpdate(installed, listing, latest));
                }
            }
        }

        return updates;
    }

    private List<InstalledOnlineTemplate> Load()
    {
        string path = Path.Combine(Folder, ManifestName);
        try
        {
            return System.IO.File.Exists(path) ? JsonSerializer.Deserialize<List<InstalledOnlineTemplate>>(System.IO.File.ReadAllText(path), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    private void Save(List<InstalledOnlineTemplate> list)
    {
        string path = Path.Combine(Folder, ManifestName);
        string temp = path + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
        System.IO.File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>更新の確認の間隔 (起動時に 1 日 1 回。TPL-36 の仕様 5)。</summary>
public static class OnlineTemplateUpdateSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) => lastCheck is not { } last || now - last >= Interval || now < last;
}
