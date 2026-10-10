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

    /// <summary>ずらしながらのその場保存 (ENG-24) で、<see cref="TestHookSettings.KillAtBytes"/> バイトを書いた時点。</summary>
    ShiftWrite,

    /// <summary>設定ファイルの書き込み (UI-23 の仕様 5) で、一時ファイルに半分まで書いた時点 (TD-UI-HOOK-KILL-SETTINGS-TEMP)。</summary>
    SettingsTemp,

    /// <summary>設定ファイルの書き込みで、一時ファイルを書き終えて置き換える直前。</summary>
    SettingsBeforeReplace,

    /// <summary>設定ファイルの書き込みで、置き換えた直後。</summary>
    SettingsAfterReplace,
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
public sealed record FileSourceSpec(string Match, int DelayMs, IReadOnlyList<(long Offset, long Length)> ReadErrors, long DelayFromOffset = 0);

/// <summary>起動時に開く仮想のデータソース。</summary>
public sealed record VirtualSourceSpec(
    string Name,
    long Length,
    VirtualContent Content,
    bool Resizable,
    byte Fill,
    ulong Seed,
    int DelayMs,
    IReadOnlyList<(long Offset, long Length)> ReadErrors,
    long DelayFromOffset = 0);

/// <summary>
/// --test-hooks の設定ファイル (JSON)。すべて省略できる。数値は 10 進の数か、"0x" で始まる 16 進の文字列で書ける。
/// <code>
/// {
///   "noActivate": true,                         // ウィンドウを前面に出さない (既定 true)
///   "recoveryIntervalSeconds": 2,               // 復旧用データの保存間隔 (ENG-27。既定は 60 秒)
///   "frozenTime": "2026-01-02T03:04:05Z",       // 時刻の固定
///   "fileSources": [ { "match": "*.bin", "delayMs": 500, "delayFromOffset": 65536,
///                      "readErrors": [ { "offset": "0x1000", "length": 512 } ] } ],
///   "virtualSources": [ { "name": "virtual", "length": "0x7FFFFFFFFFFFFFFF", "content": "offset64",
///                         "resizable": false, "fill": 255, "seed": 1, "delayMs": 0, "readErrors": [] } ],
///   "saveFault": { "atByte": 0, "kind": "io" | "diskFull", "once": false },
///   "killAt": "saveWrite" | "saveBeforeReplace" | "saveAfterReplace" | "inPlaceAfterJournal",
///   "unhandledException": "uiThread" | "background" | "unobservedTask" | "save",
///   "unhandledExceptionDelayMs": 0,
///   "elevated": true,                           // 管理者として実行している扱い (タイトルの「(管理者)」。昇格はしない)
///   "culture": "de-DE",                         // 地域設定の上書き (OS の設定を変えずに CultureInfo.CurrentCulture を変える)
///   "timeZone": "Tokyo Standard Time",          // インスペクタの「ローカル時刻」のタイムゾーン (OS の設定を変えない。INSP-15)
///   "openPicker": [ "C:\a.bin" ],               // 「開く」のダイアログの代わりに返すファイル ([] はキャンセル)
///   "savePicker": "C:\b.bin",                   // 「名前を付けて保存」のダイアログの代わりに返すパス ("" はキャンセル)
///   "freeSpace": 1048576                        // 保存先・ジャーナルの置き場所の空き容量の上書き (ENG-25)
/// }
/// </code>
/// </summary>
/// <summary>開くときの書き込みの確認で起こすエラー (<see cref="TestHookSettings.WriteErrors"/>)。</summary>
public sealed record WriteErrorSpec(string Match, string Error);

public sealed record TestHookSettings
{
    public bool NoActivate { get; init; } = true;

    public TimeSpan? RecoveryInterval { get; init; }

    public DateTimeOffset? FrozenTime { get; init; }

    public IReadOnlyList<FileSourceSpec> FileSources { get; init; } = [];

    public IReadOnlyList<VirtualSourceSpec> VirtualSources { get; init; } = [];

    public SaveFault? SaveFault { get; init; }

    public KillPoint KillAt { get; init; }

    /// <summary>ずらしながらのその場保存で強制終了する書き込み量 (既定 512 MiB。TC-ENG-24-03)。</summary>
    public long KillAtBytes { get; init; } = 512L * 1024 * 1024;

    public ExceptionPlace? UnhandledException { get; init; }

    public int UnhandledExceptionDelayMs { get; init; }

    /// <summary>ANSI として使うコードページ (テキスト列の文字コード。null ならシステムの既定)。</summary>
    public int? AnsiCodePage { get; init; }

    /// <summary>地域設定 (数値・サイズの書式。null ならシステムの既定)。表示言語は --ui-lang で変える。</summary>
    public string? Culture { get; init; }

    /// <summary>管理者として実行している扱いにする (昇格せずに UI-02 の「(管理者)」を確かめる。TC-UI-02-02)。</summary>
    public bool Elevated { get; init; }

    /// <summary>
    /// 管理者として実行中のドロップの案内 (UI-34 の仕様 7) をテスト用のビルドでも出す。既定では出さない (CI のランナーは管理者として動くため、
    /// ほかの通知を確かめるテストの邪魔になる)。TC-UI-34-06 で使う。
    /// </summary>
    public bool AdminDropNotice { get; init; }

    /// <summary>
    /// ディスクの書き込み (ENG-30) で、ボリュームをロックした後・書き込みの直前に止め、このパスに印のファイルを書く (ロックを保持したまま。
    /// TC-ENG-28-08)。null なら止めない。
    /// </summary>
    public string? PauseAfterVolumeLock { get; init; }

    /// <summary>インスペクタの「ローカル時刻」に使うタイムゾーンの ID (null ならシステムの設定)。</summary>
    public string? TimeZone { get; init; }

    /// <summary>偽のディスク・ボリュームの定義ファイル (JSON。ENG-29 の UI テスト用)。null なら本物のデバイス。</summary>
    public string? FakeDevices { get; init; }

    /// <summary>偽のプロセスの定義ファイル (JSON。ENG-32 の UI テスト用)。null なら本物のプロセス。</summary>
    public string? FakeProcesses { get; init; }

    /// <summary>「開く」のダイアログの代わりに返すファイル。null なら本物のダイアログを出す。</summary>
    public IReadOnlyList<string>? OpenPicker { get; init; }

    /// <summary>「名前を付けて保存」のダイアログの代わりに返すパス (空文字列はキャンセル)。null なら本物のダイアログを出す。</summary>
    public string? SavePicker { get; init; }

    /// <summary>ボリュームの空き容量の上書き (バイト数)。null なら OS の値。</summary>
    public long? FreeSpace { get; init; }

    /// <summary>
    /// 開くときの書き込みの確認 (ENG-14 の仕様 1) で起こすエラー。ファイル名 (またはパス) のパターンと、"accessDenied"・"sharingViolation"・
    /// "writeProtect" (読み取り専用のメディア) のどれか。
    /// </summary>
    public IReadOnlyList<WriteErrorSpec> WriteErrors { get; init; } = [];

    public WriteErrorSpec? WriteErrorFor(string path)
    {
        string full = Path.GetFullPath(path);
        return WriteErrors.FirstOrDefault(s =>
            FileSystemName.MatchesSimpleExpression(s.Match, Path.GetFileName(full))
            || FileSystemName.MatchesSimpleExpression(s.Match, full));
    }

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
                ReadRanges(n["readErrors"]),
                ReadLong(n["delayFromOffset"], 0))).ToList() ?? [],
            VirtualSources = root["virtualSources"]?.AsArray().Select(n => ParseVirtual(n!.AsObject())).ToList() ?? [],
            SaveFault = root["saveFault"] is JsonObject f
                ? new SaveFault(ReadLong(f["atByte"], 0), ParseEnum<SaveFaultKind>(f["kind"], SaveFaultKind.Io), f["once"]?.GetValue<bool>() ?? false)
                : null,
            KillAt = ParseEnum(root["killAt"], KillPoint.None),
            KillAtBytes = ReadLong(root["killAtBytes"], 512L * 1024 * 1024),
            UnhandledException = root["unhandledException"] is { } e ? ParseEnum(e, ExceptionPlace.UiThread) : null,
            UnhandledExceptionDelayMs = (int)ReadLong(root["unhandledExceptionDelayMs"], 0),
            AnsiCodePage = root["ansiCodePage"] is { } cp ? (int)ReadLong(cp, 0) : null,
            Culture = root["culture"]?.GetValue<string>(),
            Elevated = root["elevated"]?.GetValue<bool>() ?? false,
            AdminDropNotice = root["adminDropNotice"]?.GetValue<bool>() ?? false,
            PauseAfterVolumeLock = root["pauseAfterVolumeLock"]?.GetValue<string>(),
            TimeZone = root["timeZone"]?.GetValue<string>(),
            FakeDevices = root["fakeDevices"]?.GetValue<string>(),
            FakeProcesses = root["fakeProcesses"]?.GetValue<string>(),
            OpenPicker = root["openPicker"]?.AsArray().Select(n => n!.GetValue<string>()).ToList(),
            SavePicker = root["savePicker"]?.GetValue<string>(),
            FreeSpace = root["freeSpace"] is { } free ? ReadLong(free, 0) : null,
            WriteErrors = root["writeErrors"]?.AsArray().Select(n => new WriteErrorSpec(
                n!["match"]?.GetValue<string>() ?? "*",
                n["error"]?.GetValue<string>() ?? "accessDenied")).ToList() ?? [],
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
        ReadRanges(n["readErrors"]),
        ReadLong(n["delayFromOffset"], 0));

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
