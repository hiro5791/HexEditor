using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexEditor.Core.Scripting;

/// <summary>信頼済みの一覧に登録した理由 (AUTO-34 の仕様 1)。</summary>
public enum TrustSource
{
    /// <summary>確認ダイアログで「常に許可する」を選んだ。</summary>
    UserAllowed,

    /// <summary>アプリ内のスクリプトエディタで利用者が保存した (AUTO-20 の仕様 6)。</summary>
    SavedInApp,

    /// <summary>アプリ内で記録したマクロを保存した (AUTO-28 の仕様 7)。</summary>
    RecordedMacro,
}

/// <summary>
/// 信頼済みの一覧の 1 件。スクリプトは「パス + 内容の SHA-256」、プラグインは「id + 全ファイルのハッシュ」で識別する (AUTO-34 の仕様 1)。
/// <see cref="Permissions"/> は「常に許可する」で与えた許可 (仕様 5。設定「信頼と許可」に一覧し、取り消せる)。
/// </summary>
public sealed record TrustRecord(string Kind, string Key, string Hash, TrustSource Source, IReadOnlyList<string> Permissions, DateTimeOffset Time)
{
    /// <summary>表示用のパス (大文字・小文字を元のまま。<see cref="Key"/> は比較用に大文字にしている)。古い記録では null。</summary>
    public string? DisplayPath { get; init; }
}

/// <summary>
/// 信頼済みの一覧 (<c>&lt;設定フォルダ&gt;/scripts/trust.json</c>)。アプリ全体で 1 つ。
/// </summary>
public sealed class TrustStore
{
    public const string ScriptKind = "script";
    public const string PluginKind = "plugin";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string? _path;
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private List<TrustRecord> _records = [];

    /// <param name="path">保存先。null ならメモリ上だけ (テスト・ヘッドレスの一時的な実行)。</param>
    public TrustStore(string? path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<TrustRecord> Records
    {
        get
        {
            lock (_lock)
            {
                return [.. _records];
            }
        }
    }

    /// <summary>スクリプトの識別子 (正規化したフルパス。大文字と小文字を区別しない)。</summary>
    public static string ScriptKey(string path) => Path.GetFullPath(path).ToUpperInvariant();

    /// <summary>内容の SHA-256 (小文字の Hex)。</summary>
    public static string HashOf(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>識別子と内容のハッシュが一致する記録。内容が変わったものは見つからない (再確認になる。仕様 1)。</summary>
    public TrustRecord? Find(string kind, string key, string hash)
    {
        lock (_lock)
        {
            return _records.FirstOrDefault(r => r.Kind == kind && r.Key == key && r.Hash == hash);
        }
    }

    /// <summary>
    /// 確認ダイアログで「常に許可する」を選んだスクリプトの記録 (設定「信頼と許可」に一覧し、取り消せる。仕様 5)。新しい順。
    /// アプリ内で保存したスクリプト・記録したマクロの記録は含めない (利用者が書いたもの。仕様 1)。
    /// </summary>
    public IReadOnlyList<TrustRecord> AllowedScripts =>
        [.. Records.Where(r => r.Kind == ScriptKind && r.Source == TrustSource.UserAllowed).OrderByDescending(r => r.Time)];

    /// <summary>記録を加える (同じ識別子の古い記録は置き換える)。</summary>
    public TrustRecord Add(string kind, string key, string hash, TrustSource source, IEnumerable<string> permissions, string? displayPath = null)
    {
        var record = new TrustRecord(kind, key, hash, source, [.. permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], _time.GetUtcNow())
        {
            DisplayPath = displayPath,
        };
        lock (_lock)
        {
            _records.RemoveAll(r => r.Kind == kind && r.Key == key);
            _records.Add(record);
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return record;
    }

    /// <summary>スクリプトをアプリ内で保存した・マクロを記録して保存したときに登録する (確認を出さないため)。</summary>
    public TrustRecord TrustSavedScript(string path, string content, TrustSource source, IEnumerable<string>? permissions = null) =>
        Add(ScriptKind, ScriptKey(path), HashOf(content), source, permissions ?? ScriptMetadata.Parse(content).Permissions, Path.GetFullPath(path));

    /// <summary>取り消す (設定「信頼と許可」。仕様 5)。</summary>
    public bool Revoke(string kind, string key)
    {
        int removed;
        lock (_lock)
        {
            removed = _records.RemoveAll(r => r.Kind == kind && r.Key == key);
            if (removed > 0)
            {
                Save();
            }
        }

        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed > 0;
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            _records = JsonSerializer.Deserialize<List<TrustRecord>>(File.ReadAllText(_path), Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 壊れた一覧は空として扱う (信頼は安全側。もう一度確認を出す)。
            _records = [];
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_records, Json));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>Mark of the Web (インターネットから取得したファイル) の判定 (AUTO-34 の仕様 2)。代替データストリームを読むだけ。</summary>
public static class MarkOfTheWeb
{
    /// <summary>ゾーン 3 (インターネット) か 4 (制限付き) の印があるか。</summary>
    public static bool IsFromInternet(string path)
    {
        try
        {
            string stream = path + ":Zone.Identifier";
            if (!File.Exists(stream))
            {
                return false;
            }

            foreach (string line in File.ReadLines(stream))
            {
                string t = line.Trim();
                if (t.StartsWith("ZoneId=", StringComparison.OrdinalIgnoreCase) && int.TryParse(t[7..], out int zone))
                {
                    return zone >= 3;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }

        return false;
    }
}
