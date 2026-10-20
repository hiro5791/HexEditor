using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HexEditor.Core.Files;

namespace HexEditor.Core.Forensics.Carving;

/// <summary>読み込みで除外した定義 (名前と理由)。</summary>
public sealed record RejectedSignature(string Name, IReadOnlyList<SignatureError> Errors);

/// <summary>JSON の読み込み結果: 取り込める定義と、除外した定義 (FOR-18 の仕様 4)。</summary>
public sealed record SignatureImportResult(IReadOnlyList<CarvingSignature> Accepted, IReadOnlyList<RejectedSignature> Rejected);

/// <summary>JSON ファイル全体が読めない (JSON でない)。</summary>
public sealed class SignatureFileException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>定義の JSON の読み書き。形式: <c>{ "version": 1, "disabledBuiltIns": [...], "signatures": [ { "name": ..., "extension": ..., "headers": [...], ... } ] }</c>。</summary>
public static class SignatureJson
{
    public const int Version = 1;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JSON を読む。不正な定義は除外して理由を返し、他の定義だけを取り込む。ファイル全体が JSON でなければ <see cref="SignatureFileException"/>。</summary>
    public static SignatureImportResult Parse(string json, out IReadOnlyList<string> disabledBuiltIns)
    {
        var accepted = new List<CarvingSignature>();
        var rejected = new List<RejectedSignature>();
        var disabled = new List<string>();
        disabledBuiltIns = disabled;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 32 });
        }
        catch (JsonException ex)
        {
            throw new SignatureFileException(ex.Message, ex);
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            JsonElement list;
            if (root.ValueKind == JsonValueKind.Array)
            {
                list = root;
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("signatures", out JsonElement s) && s.ValueKind == JsonValueKind.Array)
            {
                list = s;
                if (root.TryGetProperty("disabledBuiltIns", out JsonElement d) && d.ValueKind == JsonValueKind.Array)
                {
                    disabled.AddRange(d.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }
            }
            else
            {
                throw new SignatureFileException("The file does not contain a list of signatures.");
            }

            int index = 0;
            foreach (JsonElement item in list.EnumerateArray())
            {
                index++;
                ReadOne(item, index, accepted, rejected);
            }
        }

        return new SignatureImportResult(accepted, rejected);
    }

    private static void ReadOne(JsonElement item, int index, List<CarvingSignature> accepted, List<RejectedSignature> rejected)
    {
        string fallbackName = $"#{index}";
        if (item.ValueKind != JsonValueKind.Object)
        {
            rejected.Add(new RejectedSignature(fallbackName, [new SignatureError("Definition", "Format")]));
            return;
        }

        var errors = new List<SignatureError>();
        string name = Text(item, "name", errors) ?? string.Empty;
        string extension = Text(item, "extension", errors) ?? string.Empty;
        var headers = new List<string>();
        if (item.TryGetProperty("headers", out JsonElement h))
        {
            if (h.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement e in h.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.String)
                    {
                        headers.Add(e.GetString() ?? string.Empty);
                    }
                    else
                    {
                        errors.Add(new SignatureError("Headers", "HeaderFormat"));
                    }
                }
            }
            else if (h.ValueKind == JsonValueKind.String)
            {
                headers.Add(h.GetString() ?? string.Empty);
            }
            else
            {
                errors.Add(new SignatureError("Headers", "HeaderFormat"));
            }
        }
        else if (Text(item, "header", errors) is { } single)
        {
            headers.Add(single);
        }

        long headerOffset = Number(item, "headerOffset", 0, errors);
        string? footer = Text(item, "footer", errors);
        long? footerInclude = item.TryGetProperty("footerInclude", out _) ? Number(item, "footerInclude", 0, errors) : null;
        long maxSize = Number(item, "maxSize", CarvingSignature.DefaultMaxSize, errors);
        long minSize = Number(item, "minSize", 0, errors);
        string? structure = Text(item, "structure", errors);
        SizeFieldRule? sizeField = null;
        if (item.TryGetProperty("sizeField", out JsonElement sf) && sf.ValueKind == JsonValueKind.Object)
        {
            sizeField = new SizeFieldRule
            {
                Offset = Number(sf, "offset", 0, errors),
                Bits = (int)Number(sf, "bits", 32, errors),
                BigEndian = string.Equals(Text(sf, "endian", errors), "big", StringComparison.OrdinalIgnoreCase),
                Add = Number(sf, "add", 0, errors),
            };
        }
        else if (item.TryGetProperty("sizeField", out JsonElement sfv) && sfv.ValueKind != JsonValueKind.Null)
        {
            errors.Add(new SignatureError("SizeField", "SizeFieldInvalid"));
        }

        var signature = new CarvingSignature
        {
            Name = name,
            Extension = extension.TrimStart('.'),
            Headers = headers,
            HeaderOffset = headerOffset,
            Footer = string.IsNullOrWhiteSpace(footer) ? null : footer,
            FooterInclude = footerInclude,
            MaxSize = maxSize,
            MinSize = minSize,
            SizeField = sizeField,
            Structure = string.IsNullOrWhiteSpace(structure) ? null : structure,
            SectorAligned = Bool(item, "sectorAligned", true),
            Enabled = Bool(item, "enabled", true),
        };
        errors.AddRange(signature.Validate());
        if (errors.Count > 0)
        {
            rejected.Add(new RejectedSignature(string.IsNullOrWhiteSpace(name) ? fallbackName : name, [.. errors.DistinctBy(e => (e.Field, e.Code))]));
        }
        else
        {
            accepted.Add(signature);
        }
    }

    private static string? Text(JsonElement e, string name, List<SignatureError> errors)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }

        errors.Add(new SignatureError(name, "Format"));
        return null;
    }

    private static bool Bool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    private static long Number(JsonElement e, string name, long fallback, List<SignatureError> errors)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind == JsonValueKind.Null)
        {
            return fallback;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n))
        {
            return n;
        }

        if (v.ValueKind == JsonValueKind.String && SizeInput.TryParse(v.GetString(), out long parsed))
        {
            return parsed;
        }

        errors.Add(new SignatureError(name, "Format"));
        return fallback;
    }

    /// <summary>JSON にする (組み込みの無効化の一覧は <paramref name="disabledBuiltIns"/>)。</summary>
    public static string Serialize(IEnumerable<CarvingSignature> signatures, IEnumerable<string>? disabledBuiltIns = null)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            if (disabledBuiltIns is not null)
            {
                w.WriteStartArray("disabledBuiltIns");
                foreach (string name in disabledBuiltIns)
                {
                    w.WriteStringValue(name);
                }

                w.WriteEndArray();
            }

            w.WriteStartArray("signatures");
            foreach (CarvingSignature s in signatures)
            {
                WriteOne(w, s);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteOne(Utf8JsonWriter w, CarvingSignature s)
    {
        w.WriteStartObject();
        w.WriteString("name", s.Name);
        w.WriteString("extension", s.Extension);
        w.WriteStartArray("headers");
        foreach (string header in s.Headers)
        {
            w.WriteStringValue(header);
        }

        w.WriteEndArray();
        w.WriteNumber("headerOffset", s.HeaderOffset);
        if (!string.IsNullOrWhiteSpace(s.Footer))
        {
            w.WriteString("footer", s.Footer);
        }

        if (s.FooterInclude is { } include)
        {
            w.WriteNumber("footerInclude", include);
        }

        w.WriteNumber("maxSize", s.MaxSize);
        w.WriteNumber("minSize", s.MinSize);
        if (s.SizeField is { } f)
        {
            w.WriteStartObject("sizeField");
            w.WriteNumber("offset", f.Offset);
            w.WriteNumber("bits", f.Bits);
            w.WriteString("endian", f.BigEndian ? "big" : "little");
            w.WriteNumber("add", f.Add);
            w.WriteEndObject();
        }

        if (!string.IsNullOrWhiteSpace(s.Structure))
        {
            w.WriteString("structure", s.Structure);
        }

        w.WriteBoolean("sectorAligned", s.SectorAligned);
        w.WriteBoolean("enabled", s.Enabled);
        w.WriteEndObject();
    }
}

/// <summary>
/// 定義の管理 (FOR-18)。組み込みの定義 (<see cref="BuiltInSignatures"/>) は編集できず、無効にすることと複製して編集することだけができる。
/// 利用者の定義と組み込みの無効化は設定フォルダの <c>carving-signatures.json</c> に保存する (一時ファイル経由)。
/// </summary>
public sealed class SignatureStore
{
    public const string FileName = "carving-signatures.json";

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly List<CarvingSignature> _user = [];
    private readonly HashSet<string> _disabledBuiltIns = new(StringComparer.OrdinalIgnoreCase);

    public SignatureStore(string? settingsFolder)
    {
        _path = settingsFolder is null ? null : Path.Combine(settingsFolder, FileName);
    }

    public event EventHandler? Changed;

    /// <summary>ファイルを読む。なければ組み込みだけ。読めない定義は除外して返す (ファイルは書き換えない)。壊れたファイルは <see cref="SignatureFileException"/>。</summary>
    public SignatureImportResult Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return new SignatureImportResult([], []);
        }

        SignatureImportResult result = SignatureJson.Parse(File.ReadAllText(_path), out IReadOnlyList<string> disabled);
        lock (_gate)
        {
            _user.Clear();
            _user.AddRange(result.Accepted.Where(s => !BuiltInSignatures.IsBuiltInName(s.Name)));
            _disabledBuiltIns.Clear();
            foreach (string name in disabled.Where(BuiltInSignatures.IsBuiltInName))
            {
                _disabledBuiltIns.Add(name);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>組み込みの定義 (無効の設定を反映) と利用者の定義。</summary>
    public IReadOnlyList<CarvingSignature> All
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. BuiltInSignatures.All.Select(b => b with { Enabled = !_disabledBuiltIns.Contains(b.Name) }),
                    .. _user,
                ];
            }
        }
    }

    /// <summary>スキャンに使う (有効な) 定義。</summary>
    public IReadOnlyList<CarvingSignature> Enabled => [.. All.Where(s => s.Enabled)];

    public CarvingSignature? Find(string name) => All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>名前が使われていないか (大文字小文字は区別しない)。</summary>
    public bool IsNameFree(string name, string? exceptName = null) =>
        !All.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) && !string.Equals(s.Name, exceptName, StringComparison.OrdinalIgnoreCase));

    /// <summary>利用者の定義を加える。誤りがあれば例外 (<see cref="SignatureInvalidException"/>)。</summary>
    public CarvingSignature Add(CarvingSignature signature)
    {
        signature = Normalize(signature);
        Check(signature, null);
        lock (_gate)
        {
            _user.Add(signature);
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return signature;
    }

    /// <summary>利用者の定義を置き換える (名前の変更を含む)。組み込みは編集できず <see cref="InvalidOperationException"/>。</summary>
    public CarvingSignature Update(string oldName, CarvingSignature signature)
    {
        if (BuiltInSignatures.IsBuiltInName(oldName))
        {
            throw new InvalidOperationException("Built-in signatures cannot be edited.");
        }

        signature = Normalize(signature);
        Check(signature, oldName);
        lock (_gate)
        {
            int index = _user.FindIndex(s => string.Equals(s.Name, oldName, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new KeyNotFoundException(oldName);
            }

            _user[index] = signature;
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return signature;
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            if (_user.RemoveAll(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return false;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>有効・無効を切り替える (組み込みも切り替えられる)。</summary>
    public void SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            if (BuiltInSignatures.IsBuiltInName(name))
            {
                if (enabled)
                {
                    _disabledBuiltIns.Remove(name);
                }
                else
                {
                    _disabledBuiltIns.Add(name);
                }
            }
            else
            {
                int index = _user.FindIndex(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    _user[index] = _user[index] with { Enabled = enabled };
                }
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>複製して利用者の定義にする (編集できる)。名前は <c>JPEG (copy)</c>、<c>JPEG (copy 2)</c> …。</summary>
    public CarvingSignature Duplicate(string name)
    {
        CarvingSignature source = Find(name) ?? throw new KeyNotFoundException(name);
        string candidate = source.Name + " (copy)";
        for (int i = 2; !IsNameFree(candidate); i++)
        {
            candidate = $"{source.Name} (copy {i.ToString(CultureInfo.InvariantCulture)})";
        }

        return Add(source with { Name = candidate, BuiltIn = false, Enabled = true });
    }

    /// <summary>JSON を取り込む。不正な定義と、名前が既にあるものは除外して理由を返す。</summary>
    public SignatureImportResult Import(string json)
    {
        SignatureImportResult parsed = SignatureJson.Parse(json, out _);
        var accepted = new List<CarvingSignature>();
        var rejected = new List<RejectedSignature>(parsed.Rejected);
        foreach (CarvingSignature s in parsed.Accepted)
        {
            if (!IsNameFree(s.Name))
            {
                rejected.Add(new RejectedSignature(s.Name, [new SignatureError("Name", "NameInUse")]));
            }
            else
            {
                accepted.Add(Add(s));
            }
        }

        return new SignatureImportResult(accepted, rejected);
    }

    /// <summary>JSON にする (<paramref name="names"/> が null なら利用者の定義すべて)。</summary>
    public string Export(IEnumerable<string>? names = null)
    {
        HashSet<string>? set = names is null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        IEnumerable<CarvingSignature> chosen = set is null ? All.Where(s => !s.BuiltIn) : All.Where(s => set.Contains(s.Name));
        return SignatureJson.Serialize(chosen.Select(s => s with { BuiltIn = false }));
    }

    private static CarvingSignature Normalize(CarvingSignature s) => s with
    {
        Name = s.Name.Trim(),
        Extension = s.Extension.Trim().TrimStart('.'),
        Headers = [.. s.Headers.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim())],
        Footer = string.IsNullOrWhiteSpace(s.Footer) ? null : s.Footer.Trim(),
        Structure = string.IsNullOrWhiteSpace(s.Structure) ? null : s.Structure.Trim(),
        BuiltIn = false,
    };

    private void Check(CarvingSignature signature, string? exceptName)
    {
        var errors = new List<SignatureError>(signature.Validate());
        if (signature.Name.Length > 0 && !IsNameFree(signature.Name, exceptName))
        {
            errors.Add(new SignatureError("Name", "NameInUse"));
        }

        if (errors.Count > 0)
        {
            throw new SignatureInvalidException(errors);
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        JsonFile.WriteText(_path, SignatureJson.Serialize(_user, _disabledBuiltIns.Order(StringComparer.OrdinalIgnoreCase)));
    }
}

/// <summary>定義に誤りがある (保存しない)。</summary>
public sealed class SignatureInvalidException(IReadOnlyList<SignatureError> errors) : Exception("The signature definition is invalid.")
{
    public IReadOnlyList<SignatureError> Errors { get; } = errors;
}
