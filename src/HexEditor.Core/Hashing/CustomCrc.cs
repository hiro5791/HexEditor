using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Hashing;

/// <summary>カスタム CRC の入力欄・JSON の項目 (ANA-20 の仕様 1・5)。誤りのある欄を赤枠にするのに使う。</summary>
public enum CustomCrcField
{
    Name,
    Width,
    Poly,
    Init,
    RefIn,
    RefOut,
    XorOut,
    Check,

    /// <summary>項目全体 (JSON の項目がオブジェクトでないなど)。</summary>
    Item,
}

/// <summary>カスタム CRC の定義・インポートの誤りの種類 (ANA-20 の「エラー」)。表示する文言は UI がリソースから作る。</summary>
public enum CustomCrcErrorCode
{
    /// <summary>名前が空。</summary>
    NameRequired,

    /// <summary>名前がプリセット (名前・別名) か他のカスタム CRC と重複する (大文字・小文字を区別しない序数比較)。</summary>
    DuplicateName,

    /// <summary>幅が 1〜64 の外。</summary>
    WidthOutOfRange,

    /// <summary>多項式・初期値・最終 XOR が幅に収まらない (「多項式は 16 bit 以内で指定してください」。幅は <see cref="CustomCrcError.Width"/>)。</summary>
    ValueTooWide,

    /// <summary>JSON の項目に必須の値 (<c>name</c>、<c>width</c>、<c>poly</c>) がない。</summary>
    MissingField,

    /// <summary>JSON の値の形式が不正 (数値・真偽値として読めない)。</summary>
    InvalidValue,

    /// <summary>JSON の <c>check</c> がパラメータから計算した値と違う。</summary>
    CheckMismatch,

    /// <summary>JSON の項目がオブジェクトでない。</summary>
    NotAnObject,

    /// <summary>JSON として読めない (ファイル全体。項目番号は 0)。</summary>
    InvalidJson,

    /// <summary>JSON の最上位が配列でない (ファイル全体。項目番号は 0)。</summary>
    NotAnArray,
}

/// <summary>カスタム CRC の誤り 1 つ。<paramref name="Width"/> は <see cref="CustomCrcErrorCode.ValueTooWide"/> の文言に使う幅。</summary>
public sealed record CustomCrcError(CustomCrcErrorCode Code, CustomCrcField Field, int Width = 0);

/// <summary>インポートで読み込めなかった項目 (「n 件目の項目を読み込めません: 理由」。<paramref name="ItemNumber"/> は 1 から。ファイル全体の誤りは 0)。</summary>
public sealed record CustomCrcImportError(int ItemNumber, CustomCrcError Error);

/// <summary>インポートの結果: 正しい項目と、読み込めなかった項目の誤り。</summary>
public sealed record CustomCrcImportResult(IReadOnlyList<CustomCrcDefinition> Items, IReadOnlyList<CustomCrcImportError> Errors);

/// <summary>
/// 利用者が定義する CRC (ANA-20)。パラメータは CRC カタログの定義 (多項式は最上位ビットを省いた通常表記)。
/// </summary>
public sealed record CustomCrcDefinition(string Name, int Width, ulong Poly, ulong Init = 0, bool RefIn = false, bool RefOut = false, ulong XorOut = 0)
{
    /// <summary>カスタム CRC の識別子の接頭辞 (設定・スクリプトで使う識別子は「custom:名前」)。</summary>
    public const string IdPrefix = "custom:";

    public const int MinWidth = 1;
    public const int MaxWidth = 64;

    /// <summary>設定・スクリプトで使う識別子。</summary>
    public string Id => IdPrefix + Name.Trim();

    /// <summary>幅が範囲内か。</summary>
    public bool IsWidthValid => Width is >= MinWidth and <= MaxWidth;

    /// <summary>パラメータ (名前以外) が正しいか。</summary>
    public bool AreParametersValid => ValidateParameters().Count == 0;

    /// <summary>パラメータ (名前以外) の誤り (幅の範囲、値が幅に収まるか)。</summary>
    public IReadOnlyList<CustomCrcError> ValidateParameters()
    {
        if (!IsWidthValid)
        {
            return [new CustomCrcError(CustomCrcErrorCode.WidthOutOfRange, CustomCrcField.Width, Width)];
        }

        ulong mask = MaskOf(Width);
        var errors = new List<CustomCrcError>();
        if ((Poly & ~mask) != 0)
        {
            errors.Add(new CustomCrcError(CustomCrcErrorCode.ValueTooWide, CustomCrcField.Poly, Width));
        }

        if ((Init & ~mask) != 0)
        {
            errors.Add(new CustomCrcError(CustomCrcErrorCode.ValueTooWide, CustomCrcField.Init, Width));
        }

        if ((XorOut & ~mask) != 0)
        {
            errors.Add(new CustomCrcError(CustomCrcErrorCode.ValueTooWide, CustomCrcField.XorOut, Width));
        }

        return errors;
    }

    /// <summary>
    /// 名前を含めたすべての誤り。<paramref name="otherCustomNames"/> は他のカスタム CRC の名前 (編集中の項目自身は含めない)。
    /// </summary>
    public IReadOnlyList<CustomCrcError> Validate(IEnumerable<string> otherCustomNames)
    {
        var errors = new List<CustomCrcError>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add(new CustomCrcError(CustomCrcErrorCode.NameRequired, CustomCrcField.Name));
        }
        else if (IsNameTaken(Name, otherCustomNames))
        {
            errors.Add(new CustomCrcError(CustomCrcErrorCode.DuplicateName, CustomCrcField.Name));
        }

        errors.AddRange(ValidateParameters());
        return errors;
    }

    /// <summary>
    /// 名前がプリセット (最初から用意するアルゴリズムの名前・別名・識別子) か、他のカスタム CRC の名前と重複するか
    /// (前後の空白を除き、大文字・小文字を区別しない序数比較)。
    /// </summary>
    public static bool IsNameTaken(string name, IEnumerable<string> otherCustomNames)
    {
        string n = name.Trim();
        return HashCatalog.BuiltIn.Any(a => Same(a.Name, n) || Same(a.Id, n) || a.Aliases.Any(x => Same(x, n)))
            || otherCustomNames.Any(x => Same(x.Trim(), n));
    }

    /// <summary>パラメータ (check は計算した値)。パラメータが不正なら例外。</summary>
    public CrcParameters ToParameters()
    {
        if (!AreParametersValid)
        {
            throw new InvalidOperationException("カスタム CRC のパラメータが不正です。");
        }

        var p = new CrcParameters(Width, Poly, Init, RefIn, RefOut, XorOut, 0);
        return p with { Check = CrcHasher.ComputeCheck(p) };
    }

    /// <summary><c>123456789</c> に対する値 (ANA-20 の仕様 3)。パラメータが不正なら null。</summary>
    public ulong? Check => AreParametersValid ? ToParameters().Check : null;

    /// <summary>剰余 (residue。ANA-20 の仕様 3)。パラメータが不正なら null。</summary>
    public ulong? Residue => AreParametersValid ? CrcHasher.ComputeResidue(ToParameters()) : null;

    /// <summary>値の Hex 表記 (幅に合わせた桁数、<c>0x</c> なし。例: 幅 16 の <c>BB3D</c>)。</summary>
    public string FormatValue(ulong value) => FormatHex(value, Width);

    /// <summary>ハッシュパネルの一覧に出すアルゴリズム (CRC のグループ。<see cref="HashAlgorithmInfo.IsCustom"/> が真)。</summary>
    public HashAlgorithmInfo ToAlgorithm()
    {
        CrcParameters p = ToParameters();
        return new HashAlgorithmInfo(Id, Name.Trim(), HashGroup.Crc, Width, _ => new CrcHasher(p), crc: p) { IsCustom = true };
    }

    /// <summary>カタログのパラメータから定義を作る (名前は指定したもの)。</summary>
    public static CustomCrcDefinition FromParameters(string name, CrcParameters p) =>
        new(name, p.Width, p.Poly, p.Init, p.RefIn, p.RefOut, p.XorOut);

    /// <summary>
    /// プリセットを「複製して編集」する (ANA-20 の仕様 4)。名前は「プリセットの名前 (2)」のように、プリセットとも
    /// <paramref name="existingCustomNames"/> とも重複しないものにする。
    /// </summary>
    public static CustomCrcDefinition FromPreset(HashAlgorithmInfo preset, IEnumerable<string>? existingCustomNames = null)
    {
        CrcParameters p = preset.Crc ?? throw new ArgumentException("CRC のプリセットではありません。", nameof(preset));
        return FromParameters(UniqueName(preset.Name, existingCustomNames ?? []), p);
    }

    /// <summary>重複しない名前 (「名前 (2)」「名前 (3)」…)。</summary>
    public static string UniqueName(string baseName, IEnumerable<string> existingCustomNames)
    {
        string[] existing = [.. existingCustomNames];
        string name = baseName.Trim();
        for (int n = 2; IsNameTaken(name, existing); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{baseName.Trim()} ({n})");
        }

        return name;
    }

    internal static ulong MaskOf(int width) => width >= 64 ? ulong.MaxValue : (1UL << width) - 1;

    internal static string FormatHex(ulong value, int width) =>
        value.ToString("X" + Math.Max(1, (width + 3) / 4).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// カスタム CRC の JSON (ANA-20 の仕様 5)。CRC カタログの項目名 (<c>name</c>、<c>width</c>、<c>poly</c>、<c>init</c>、<c>refin</c>、
/// <c>refout</c>、<c>xorout</c>、<c>check</c>。参考に <c>residue</c> も書く) を持つオブジェクトの配列。数値は <c>"0x…"</c> の 16 進の文字列で
/// 書く。読み込みでは 16 進の文字列 (<c>0x</c> 付き)、10 進の文字列、JSON の数値を受け付ける。設定への保存にも同じ形式を使う。
/// </summary>
public static class CustomCrcJson
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>JSON の文字列にする (パラメータが不正な項目は書かない)。</summary>
    public static string Serialize(IEnumerable<CustomCrcDefinition> items)
    {
        var array = new JsonArray();
        foreach (CustomCrcDefinition d in items.Where(d => d.AreParametersValid))
        {
            CrcParameters p = d.ToParameters();
            array.Add(new JsonObject
            {
                ["name"] = d.Name.Trim(),
                ["width"] = d.Width,
                ["poly"] = Hex(d.Poly, d.Width),
                ["init"] = Hex(d.Init, d.Width),
                ["refin"] = d.RefIn,
                ["refout"] = d.RefOut,
                ["xorout"] = Hex(d.XorOut, d.Width),
                ["check"] = Hex(p.Check, d.Width),
                ["residue"] = Hex(CrcHasher.ComputeResidue(p), d.Width),
            });
        }

        return array.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// JSON を読む。正しい項目と、読み込めない項目の誤り (1 から数えた項目番号と理由) を返す。名前は
    /// <paramref name="existingCustomNames"/>、プリセット、同じファイルの前の項目と重複してはいけない。
    /// </summary>
    public static CustomCrcImportResult Parse(string? json, IEnumerable<string>? existingCustomNames = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json ?? string.Empty, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return new([], [new CustomCrcImportError(0, new CustomCrcError(CustomCrcErrorCode.InvalidJson, CustomCrcField.Item))]);
        }

        if (root is not JsonArray array)
        {
            return new([], [new CustomCrcImportError(0, new CustomCrcError(CustomCrcErrorCode.NotAnArray, CustomCrcField.Item))]);
        }

        var names = new List<string>(existingCustomNames ?? []);
        var items = new List<CustomCrcDefinition>();
        var errors = new List<CustomCrcImportError>();
        for (int i = 0; i < array.Count; i++)
        {
            CustomCrcError? error = ParseItem(array[i], names, out CustomCrcDefinition? item);
            if (error is not null)
            {
                errors.Add(new CustomCrcImportError(i + 1, error));
                continue;
            }

            items.Add(item!);
            names.Add(item!.Name);
        }

        return new(items, errors);
    }

    private static CustomCrcError? ParseItem(JsonNode? node, IReadOnlyList<string> names, out CustomCrcDefinition? item)
    {
        item = null;
        if (node is not JsonObject o)
        {
            return new CustomCrcError(CustomCrcErrorCode.NotAnObject, CustomCrcField.Item);
        }

        // 名前。
        if (o["name"] is null)
        {
            return new CustomCrcError(CustomCrcErrorCode.MissingField, CustomCrcField.Name);
        }

        if (o["name"] is not JsonValue nameValue || !nameValue.TryGetValue(out string? name))
        {
            return new CustomCrcError(CustomCrcErrorCode.InvalidValue, CustomCrcField.Name);
        }

        // 幅。
        if (o["width"] is null)
        {
            return new CustomCrcError(CustomCrcErrorCode.MissingField, CustomCrcField.Width);
        }

        if (!TryReadNumber(o["width"], out ulong widthValue) || widthValue > int.MaxValue)
        {
            return new CustomCrcError(CustomCrcErrorCode.InvalidValue, CustomCrcField.Width);
        }

        int width = (int)widthValue;
        if (width is < CustomCrcDefinition.MinWidth or > CustomCrcDefinition.MaxWidth)
        {
            return new CustomCrcError(CustomCrcErrorCode.WidthOutOfRange, CustomCrcField.Width, width);
        }

        // 多項式 (必須)、初期値・反転・最終 XOR (省略時は既定値)。
        if (o["poly"] is null)
        {
            return new CustomCrcError(CustomCrcErrorCode.MissingField, CustomCrcField.Poly, width);
        }

        if (!TryReadNumber(o["poly"], out ulong poly))
        {
            return new CustomCrcError(CustomCrcErrorCode.InvalidValue, CustomCrcField.Poly, width);
        }

        ulong init = 0, xorOut = 0;
        bool refIn = false, refOut = false;
        if (o["init"] is { } initNode && !TryReadNumber(initNode, out init))
        {
            return new CustomCrcError(CustomCrcErrorCode.InvalidValue, CustomCrcField.Init, width);
        }

        if (o["refin"] is { } refInNode && !TryReadBool(refInNode, out refIn))
        {
            return new CustomCrcError(CustomCrcErrorCode.InvalidValue, CustomCrcField.RefIn, width);
        }

        if (o["refout"] is { } refOutNode && !TryReadBool(refOutNode, out refOut))
        {
            return new CustomCrcError(CustomCrcErrorCode.InvalidValue, CustomCrcField.RefOut, width);
        }

        if (o["xorout"] is { } xorOutNode && !TryReadNumber(xorOutNode, out xorOut))
        {
            return new CustomCrcError(CustomCrcErrorCode.InvalidValue, CustomCrcField.XorOut, width);
        }

        var definition = new CustomCrcDefinition(name?.Trim() ?? string.Empty, width, poly, init, refIn, refOut, xorOut);
        if (definition.Validate(names) is [var first, ..])
        {
            return first;
        }

        // 書かれた check が計算した値と違う項目は読み込まない (パラメータの写し間違いの検出)。
        if (o["check"] is { } checkNode)
        {
            if (!TryReadNumber(checkNode, out ulong check))
            {
                return new CustomCrcError(CustomCrcErrorCode.InvalidValue, CustomCrcField.Check, width);
            }

            if (check != definition.Check)
            {
                return new CustomCrcError(CustomCrcErrorCode.CheckMismatch, CustomCrcField.Check, width);
            }
        }

        item = definition;
        return null;
    }

    /// <summary>数値を読む: <c>"0x…"</c> の 16 進の文字列、10 進の文字列、JSON の数値 (0 以上の整数)。</summary>
    public static bool TryReadNumber(JsonNode? node, out ulong value)
    {
        value = 0;
        if (node is not JsonValue v)
        {
            return false;
        }

        if (v.TryGetValue(out string? text))
        {
            return TryParseNumber(text, out value);
        }

        if (v.GetValueKind() == JsonValueKind.Number)
        {
            if (v.TryGetValue(out ulong u))
            {
                value = u;
                return true;
            }

            // 1.0 のような整数値の小数表記は受け付けない (2^53 を超える値を JSON の数値で書くと桁が落ちるため、16 進の文字列を推奨)。
            return false;
        }

        return false;
    }

    /// <summary>数値の文字列 (<c>0x</c> 付きの 16 進、または 10 進) を読む。</summary>
    public static bool TryParseNumber(string? text, out ulong value)
    {
        value = 0;
        string t = text?.Trim() ?? string.Empty;
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return t.Length > 2 && ulong.TryParse(t.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }

        return t.Length > 0 && ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadBool(JsonNode node, out bool value)
    {
        value = false;
        if (node is not JsonValue v)
        {
            return false;
        }

        if (v.TryGetValue(out bool b))
        {
            value = b;
            return true;
        }

        return v.TryGetValue(out string? s) && bool.TryParse(s?.Trim(), out value);
    }

    private static string Hex(ulong value, int width) => "0x" + CustomCrcDefinition.FormatHex(value, width);
}

/// <summary>
/// 利用者のカスタム CRC の一覧 (ANA-20 の仕様 5)。設定に保存する文字列 (<see cref="Serialize"/>、<see cref="Load"/>) は
/// エクスポートする JSON と同じ形式。<see cref="ApplyToCatalog"/> でハッシュパネルの一覧 (<see cref="HashCatalog.Custom"/>) に反映する。
/// </summary>
public sealed class CustomCrcStore
{
    private readonly List<CustomCrcDefinition> _items = [];

    public IReadOnlyList<CustomCrcDefinition> Items => _items;

    /// <summary>一覧が変わった (設定への保存と一覧への反映のきっかけ)。</summary>
    public event EventHandler? Changed;

    /// <summary>設定の文字列から読み込む (読めない項目は飛ばす)。</summary>
    public static CustomCrcStore Load(string? settings)
    {
        var store = new CustomCrcStore();
        if (!string.IsNullOrWhiteSpace(settings))
        {
            store._items.AddRange(CustomCrcJson.Parse(settings).Items);
        }

        return store;
    }

    /// <summary>設定に保存する文字列。</summary>
    public string Serialize() => CustomCrcJson.Serialize(_items);

    /// <summary>エクスポートする JSON (<see cref="Serialize"/> と同じ)。</summary>
    public string Export() => Serialize();

    /// <summary>名前で探す (大文字・小文字を区別しない序数比較)。</summary>
    public CustomCrcDefinition? Find(string name) =>
        _items.FirstOrDefault(d => string.Equals(d.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>定義の誤り。<paramref name="replacing"/> は編集中の項目の元の名前 (その項目との重複は誤りにしない)。</summary>
    public IReadOnlyList<CustomCrcError> Validate(CustomCrcDefinition definition, string? replacing = null) =>
        definition.Validate(_items.Select(d => d.Name)
            .Where(n => replacing is null || !string.Equals(n.Trim(), replacing.Trim(), StringComparison.OrdinalIgnoreCase)));

    /// <summary>追加する。誤りがあれば追加せずに誤りを返す。</summary>
    public IReadOnlyList<CustomCrcError> Add(CustomCrcDefinition definition)
    {
        IReadOnlyList<CustomCrcError> errors = Validate(definition);
        if (errors.Count == 0)
        {
            _items.Add(Normalize(definition));
            OnChanged();
        }

        return errors;
    }

    /// <summary>名前が <paramref name="name"/> の項目を置き換える。誤りがあれば置き換えずに誤りを返す。</summary>
    public IReadOnlyList<CustomCrcError> Replace(string name, CustomCrcDefinition definition)
    {
        int index = IndexOf(name);
        if (index < 0)
        {
            throw new KeyNotFoundException($"カスタム CRC がありません: {name}");
        }

        IReadOnlyList<CustomCrcError> errors = Validate(definition, replacing: name);
        if (errors.Count == 0)
        {
            _items[index] = Normalize(definition);
            OnChanged();
        }

        return errors;
    }

    public bool Remove(string name)
    {
        int index = IndexOf(name);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        OnChanged();
        return true;
    }

    /// <summary>JSON をインポートする。正しい項目だけを追加し、読み込めない項目の誤りを返す (ANA-20 の「エラー」)。</summary>
    public CustomCrcImportResult Import(string? json)
    {
        CustomCrcImportResult result = CustomCrcJson.Parse(json, _items.Select(d => d.Name));
        if (result.Items.Count > 0)
        {
            _items.AddRange(result.Items);
            OnChanged();
        }

        return result;
    }

    /// <summary>ハッシュパネルの一覧に反映する (<see cref="HashCatalog.SetCustom"/>)。</summary>
    public void ApplyToCatalog() => HashCatalog.SetCustom([.. _items.Select(d => d.ToAlgorithm())]);

    private int IndexOf(string name) =>
        _items.FindIndex(d => string.Equals(d.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static CustomCrcDefinition Normalize(CustomCrcDefinition d) => d with { Name = d.Name.Trim() };

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
