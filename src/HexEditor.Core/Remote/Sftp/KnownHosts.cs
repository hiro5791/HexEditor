using HexEditor.Core.Files;

namespace HexEditor.Core.Remote.Sftp;

/// <summary>信頼したホスト鍵 1 つ。</summary>
public sealed record KnownHostEntry
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string Algorithm { get; init; }

    /// <summary>SHA-256 のフィンガープリント (<c>SHA256:…</c>)。</summary>
    public required string Fingerprint { get; init; }

    public DateTime AddedUtc { get; init; }
}

/// <summary>
/// 信頼したホスト鍵の表 (ENG-41 の仕様 2)。アプリの設定フォルダの <c>known_hosts.json</c> に保存する。照合はホスト名 (大文字・小文字を区別しない) と
/// ポートの組で行い、同じ組に違う鍵 (種類が違う場合も) が記録されていれば <see cref="HostKeyStatus.Changed"/> にする (黙って受け入れない)。
/// パスワードなどの秘密は記録しない。スレッドセーフ。
/// </summary>
public sealed class KnownHosts
{
    public const string FileName = "known_hosts.json";

    private readonly object _lock = new();
    private readonly string? _path;
    private List<KnownHostEntry> _entries;

    /// <summary><paramref name="path"/> の表を読む (null ならメモリの中だけ。テスト用)。</summary>
    public KnownHosts(string? path)
    {
        _path = path;
        _entries = path is not null && File.Exists(path) ? JsonFile.Read<List<KnownHostEntry>>(path) ?? [] : [];
    }

    public IReadOnlyList<KnownHostEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    /// <summary>照合する。<paramref name="previous"/> は以前に信頼した鍵 (違う場合)。</summary>
    public HostKeyStatus Check(string host, int port, HostKeyInfo key, out HostKeyInfo? previous)
    {
        lock (_lock)
        {
            previous = null;
            List<KnownHostEntry> same = [.. _entries.Where(e => Matches(e, host, port))];
            if (same.Count == 0)
            {
                return HostKeyStatus.Unknown;
            }

            if (same.Any(e => e.Algorithm == key.Algorithm && e.Fingerprint == key.Fingerprint))
            {
                return HostKeyStatus.Trusted;
            }

            KnownHostEntry old = same.FirstOrDefault(e => e.Algorithm == key.Algorithm) ?? same[0];
            previous = new HostKeyInfo(old.Algorithm, old.Fingerprint);
            return HostKeyStatus.Changed;
        }
    }

    /// <summary>
    /// 鍵を信頼する (「信頼して接続」)。同じホスト・ポートの以前の鍵は置き換える (鍵が変わったことを利用者が承認した場合)。保存する。
    /// </summary>
    public void Trust(string host, int port, HostKeyInfo key, DateTime nowUtc)
    {
        lock (_lock)
        {
            _entries = [.. _entries.Where(e => !Matches(e, host, port))];
            _entries.Add(new KnownHostEntry { Host = host, Port = port, Algorithm = key.Algorithm, Fingerprint = key.Fingerprint, AddedUtc = nowUtc });
            Save();
        }
    }

    /// <summary>信頼をやめる。</summary>
    public void Forget(string host, int port)
    {
        lock (_lock)
        {
            _entries = [.. _entries.Where(e => !Matches(e, host, port))];
            Save();
        }
    }

    private static bool Matches(KnownHostEntry e, string host, int port) =>
        e.Port == port && string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase);

    private void Save()
    {
        if (_path is not null)
        {
            JsonFile.Write(_path, _entries);
        }
    }
}
