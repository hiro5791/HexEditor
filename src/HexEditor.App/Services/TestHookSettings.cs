#if HEX_TEST_HOOKS
using System.Globalization;
using System.IO.Enumeration;
using System.Text.Json.Nodes;
using HexEditor.Core.Sources;

namespace HexEditor.App.Services;

/// <summary>強制終了する時点 (テスト方針 7.2「強制終了」)。</summary>
public enum KillPoint
{
    None,

    /// <summary>安全な保存 (ENG-22) で、一時ファイルへの書き込みの途中 (最初の進捗の通知)。</summary>
    SaveWrite,

    /// <summary>安全な保存で、一時ファイルに書き終えた後、置き換えの前。</summary>
    SaveBeforeReplace,

    /// <summary>安全な保存で、置き換えの直後 (保存したファイルを開き直す前)。</summary>
    SaveAfterReplace,

    /// <summary>その場保存 (ENG-23) で、ジャーナルを書いた後、ファイルに書き込む前。</summary>
    InPlaceAfterJournal,
}

/// <summary>未処理の例外を起こす場所 (テスト方針 7.2「未処理の例外」)。</summary>
public enum ExceptionPlace
{
    UiThread,
    Background,
    UnobservedTask,

    /// <summary>保存の処理の中 (進捗の通知で)。</summary>
    Save,
}

public enum SaveFaultKind
{
    /// <summary>I/O エラー (ERROR_IO_DEVICE)。</summary>
    Io,

    /// <summary>空き容量不足 (ERROR_DISK_FULL)。</summary>
    DiskFull,
}

/// <summary>保存中の指定のバイト位置で失敗させる。<see cref="Once"/> なら 1 回だけ。</summary>
public sealed record SaveFault(long AtByte, SaveFaultKind Kind, bool Once);

/// <summary>開くファイルに加える遅延と読み込みエラー。<see cref="Match"/> はファイル名またはフルパスのワイルドカード。</summary>
public sealed record FileSourceSpec(string Match, int DelayMs, IReadOnlyList<(long Offset, long Length)> ReadErrors);

/// <summary>起動時に開く仮想のデータソース。</summary>
public sealed record VirtualSourceSpec(
    string Name,
    long Length,
    VirtualContent Content,
    bool Resizable,
    byte Fill,
    ulong Seed,
    int DelayMs,
    IReadOnlyList<(long Offset, long Length)> ReadErrors);

/// <summary>
/// --test-hooks の設定ファイル (JSON)。すべて省略できる。数値は 10 進の数か、"0x" で始まる 16 進の文字列で書ける。
/// <code>
/// {
///   "noActivate": true,                         // ウィンドウを前面に出さない (既定 true)
///   "recoveryIntervalSeconds": 2,               // 復旧用データの保存間隔 (ENG-27。既定は 60 秒)
///   "frozenTime": "2026-01-02T03:04:05Z",       // 時刻の固定
///   "fileSources": [ { "match": "*.bin", "delayMs": 500, "readErrors": [ { "offset": "0x1000", "length": 512 } ] } ],
///   "virtualSources": [ { "name": "virtual", "length": "0x7FFFFFFFFFFFFFFF", "content": "offset64",
///                         "resizable": false, "fill": 255, "seed": 1, "delayMs": 0, "readErrors": [] } ],
///   "saveFault": { "atByte": 0, "kind": "io" | "diskFull", "once": false },
///   "killAt": "saveWrite" | "saveBeforeReplace" | "saveAfterReplace" | "inPlaceAfterJournal",
///   "unhandledException": "uiThread" | "background" | "unobservedTask" | "save",
///   "unhandledExceptionDelayMs": 0
/// }
/// </code>
/// </summary>
public sealed record TestHookSettings
{
    public bool NoActivate { get; init; } = true;

    public TimeSpan? RecoveryInterval { get; init; }

    public DateTimeOffset? FrozenTime { get; init; }

    public IReadOnlyList<FileSourceSpec> FileSources { get; init; } = [];

    public IReadOnlyList<VirtualSourceSpec> VirtualSources { get; init; } = [];

    public SaveFault? SaveFault { get; init; }

    public KillPoint KillAt { get; init; }

    public ExceptionPlace? UnhandledException { get; init; }

    public int UnhandledExceptionDelayMs { get; init; }

    public FileSourceSpec? FileSourceFor(string path)
    {
        string full = Path.GetFullPath(path);
        return FileSources.FirstOrDefault(s =>
            FileSystemName.MatchesSimpleExpression(s.Match, Path.GetFileName(full))
            || FileSystemName.MatchesSimpleExpression(s.Match, full));
    }

    public static TestHookSettings Parse(string json)
    {
        JsonObject root = JsonNode.Parse(json, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
            ?? new JsonObject();
        var settings = new TestHookSettings
        {
            NoActivate = root["noActivate"]?.GetValue<bool>() ?? true,
            RecoveryInterval = root["recoveryIntervalSeconds"] is { } s ? TimeSpan.FromSeconds(ReadDouble(s)) : null,
            FrozenTime = root["frozenTime"] is { } t ? DateTimeOffset.Parse(t.GetValue<string>(), CultureInfo.InvariantCulture) : null,
            FileSources = root["fileSources"]?.AsArray().Select(n => new FileSourceSpec(
                n!["match"]?.GetValue<string>() ?? "*",
                (int)ReadLong(n["delayMs"], 0),
                ReadRanges(n["readErrors"]))).ToList() ?? [],
            VirtualSources = root["virtualSources"]?.AsArray().Select(n => ParseVirtual(n!.AsObject())).ToList() ?? [],
            SaveFault = root["saveFault"] is JsonObject f
                ? new SaveFault(ReadLong(f["atByte"], 0), ParseEnum<SaveFaultKind>(f["kind"], SaveFaultKind.Io), f["once"]?.GetValue<bool>() ?? false)
                : null,
            KillAt = ParseEnum(root["killAt"], KillPoint.None),
            UnhandledException = root["unhandledException"] is { } e ? ParseEnum(e, ExceptionPlace.UiThread) : null,
            UnhandledExceptionDelayMs = (int)ReadLong(root["unhandledExceptionDelayMs"], 0),
        };
        return settings;
    }

    public static VirtualSourceSpec ParseVirtual(JsonObject n) => new(
        n["name"]?.GetValue<string>() ?? "virtual",
        ReadLong(n["length"], 0),
        ParseEnum(n["content"], VirtualContent.Offset64),
        n["resizable"]?.GetValue<bool>() ?? false,
        (byte)ReadLong(n["fill"], 0),
        (ulong)ReadLong(n["seed"], 0),
        (int)ReadLong(n["delayMs"], 0),
        ReadRanges(n["readErrors"]));

    private static IReadOnlyList<(long Offset, long Length)> ReadRanges(JsonNode? node) =>
        node?.AsArray().Select(r => (ReadLong(r!["offset"], 0), ReadLong(r["length"], 1))).ToList() ?? [];

    /// <summary>数か、"0x" で始まる 16 進の文字列を読む。</summary>
    public static long ReadLong(JsonNode? node, long fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            return node.GetValue<long>();
        }

        string text = node.GetValue<string>().Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.Parse(text, CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(JsonNode node) =>
        node.GetValueKind() == System.Text.Json.JsonValueKind.Number ? node.GetValue<double>() : double.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture);

    private static T ParseEnum<T>(JsonNode? node, T fallback)
        where T : struct, Enum =>
        node is null ? fallback : Enum.Parse<T>(node.GetValue<string>(), ignoreCase: true);
}
#endif
