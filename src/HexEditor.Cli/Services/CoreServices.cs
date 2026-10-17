using System.Reflection;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Plugins;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.CSharp;
using HexEditor.Core.Templates;
using HexEditor.Platform;

namespace HexEditor.Cli.Services;

/// <summary>コマンドラインの設定・データの置き場所 (GUI と同じ。PKG-06)。テンプレートのフォルダ・信頼の一覧・C# スクリプト コンポーネントに使う。</summary>
internal static class CliLocations
{
    private static readonly Lazy<DataLocations?> Detected = new(() =>
    {
        try
        {
            EnvironmentContext context = EnvironmentContext.ForCurrentProcess(typeof(CliApp).Assembly) with { ProbeWritable = _ => true };
            return AppEnvironment.Detect(context, Environment.GetEnvironmentVariable("HEXED_TEST_PROFILE")).Locations;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return null;
        }
    });

    public static DataLocations? Current => Detected.Value;

    /// <summary>利用者のテンプレートのフォルダ (設定フォルダの <c>templates/</c>。TPL-35 の仕様 1)。</summary>
    public static string? TemplateFolder => Current is { } l ? Path.Combine(l.Settings, "templates") : null;
}

/// <summary>
/// GUI と同じ設定ファイル (<c>&lt;設定フォルダ&gt;/settings.json</c>) を読み取り専用で読む (AUTO-36 の仕様 8)。書き換えない。
/// 組織の設定 <c>scripts.policy</c> (AUTO-34 の仕様 9)、スクリプトの上限 (AUTO-02 の仕様 4)、「署名されたプラグインだけ」(AUTO-35 の仕様 2) に使う。
/// 読めない場合は既定値にする。
/// </summary>
internal sealed class CliSettings
{
    private readonly System.Text.Json.Nodes.JsonObject? _data;

    public CliSettings(string settingsFolder)
    {
        try
        {
            string path = Path.Combine(settingsFolder, "settings.json");
            _data = File.Exists(path) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _data = null;
        }
    }

    public string String(string key, string fallback) =>
        _data?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? s) ? s : fallback;

    public double? Number(string key) =>
        _data?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out double d) ? d : null;

    public bool Bool(string key, bool fallback) =>
        _data?[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
}

/// <summary>
/// <c>hexed template</c> (AUTO-42) の実装: Core のテンプレートエンジン (07) で適用し、JSON (AUTO-13 の <c>toJSON()</c> と同じ構造に
/// <c>schema</c> を加えたもの)・CSV・テキストをストリームに書く。
/// </summary>
public sealed class CoreTemplateService(Func<TemplateCatalog>? catalog = null) : ITemplateService
{
    private readonly Func<TemplateCatalog> _catalog = catalog ?? (() => TemplateCatalog.CreateDefault(CliLocations.TemplateFolder));

    public int Apply(TemplateRequest request, CancellationToken cancellationToken)
    {
        TemplateCatalog catalog = _catalog();
        DocumentSnapshot snapshot = request.Document.Current;
        TemplateCatalogEntry entry = request.Auto
            ? catalog.SelectAuto(snapshot, request.FilePath) ?? throw new CliException(ExitCodes.Negative, "Cli_Template_NoMatch", Path.GetFileName(request.FilePath))
            : catalog.Find(request.Template!) ?? throw CliException.Usage("Cli_Error_TemplateNotFound", request.Template);

        TemplateResult result;
        try
        {
            result = TemplateScriptEngine.Apply(catalog, entry, snapshot, request.FilePath, request.Offset, null, cancellationToken);
        }
        catch (TemplateScriptException ex)
        {
            throw new CliException(ExitCodes.Script, "Cli_Error_Template", Where(ex.File, ex.Line, ex.Column) + ex.Message);
        }

        using (result)
        {
            var options = new TemplateExportOptions
            {
                MaxDepth = request.Depth,
                Fields = request.Fields.Count > 0 ? new TemplateFieldFilter(request.Fields) : null,
                Display = TemplateDisplayContext.Invariant,
                RootProperties = [new("schema", TemplateScriptEngine.JsonSchema), .. TemplateScriptEngine.RootProperties(entry.Name)],
            };
            switch (request.Format)
            {
                case "csv":
                    TemplateTextExporter.WriteCsv(result, request.Output, options, cancellationToken);
                    break;
                case "text":
                    TemplateTextExporter.WriteText(result, request.Output, options, cancellationToken);
                    break;
                default:
                    using (var stream = new TextWriterStream(request.Output))
                    {
                        TemplateJsonExporter.Write(result, stream, options, cancellationToken);
                    }

                    request.Output.WriteLine();
                    break;
            }

            // 評価中のエラー (部分結果。TPL-27) は位置を付けて標準エラー出力に出し、終了コード 4 にする (AUTO-42 の「エラー」)。
            bool failed = false;
            foreach (TemplateDiagnostic d in result.Diagnostics.Where(d => d.Severity is TemplateSeverity.Error or TemplateSeverity.Warning))
            {
                failed |= d.Severity == TemplateSeverity.Error;
                string at = d.Location is { } l ? Where(l.File, l.Line, l.Column) : string.Empty;
                request.Error.WriteLine($"{at}{d.Code}: {d.Message}" + (d.Path is null ? string.Empty : $" ({d.Path})"));
            }

            return failed ? ExitCodes.Script : ExitCodes.Success;
        }
    }

    private static string Where(string? file, int line, int column) =>
        file is not null && line > 0 ? $"{Path.GetFileName(file)}({line},{column}): " : string.Empty;

    /// <summary>UTF-8 のバイト列を文字にして <see cref="TextWriter"/> に書く (JSON をストリームのまま出力するため)。</summary>
    private sealed class TextWriterStream(TextWriter writer) : Stream
    {
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private char[] _chars = new char[4096];

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int needed = _decoder.GetCharCount(buffer, flush: false);
            if (needed > _chars.Length)
            {
                _chars = new char[needed];
            }

            int n = _decoder.GetChars(buffer, _chars, flush: false);
            writer.Write(_chars, 0, n);
        }

        public override void Flush() => writer.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>
/// <c>hexed run</c> (AUTO-41) の実装: Core のスクリプトのホスト (<see cref="ScriptHost"/>) と UI のない環境 (<see cref="CoreScriptEnvironment"/>)
/// で実行する。<c>console.log</c> は標準出力、警告・エラー・<c>ui.message</c> は標準エラー出力に出す (仕様 6)。終了コードは
/// <see cref="ScriptRunResult.ProcessExitCode"/> (<c>app.exit</c> の値が優先。仕様 8)。
/// </summary>
public sealed class CoreScriptRunner(Func<ScriptFolders>? folders = null, Func<CSharpComponentManager?>? csharp = null) : IScriptRunner
{
    private const string ManifestResource = "HexEditor.CSharpComponent.json";

    private readonly Func<ScriptFolders> _folders = folders ?? DefaultFolders;
    private readonly Func<CSharpComponentManager?> _csharp = csharp ?? DefaultComponents;

    public int Run(ScriptRunRequest request, CancellationToken cancellationToken)
    {
        ScriptFolders scriptFolders = _folders();
        var trust = new TrustStore(scriptFolders.TrustFile);
        var settings = new CliSettings(scriptFolders.Settings);
        // 組織の設定 (scripts.policy) と上限は GUI と同じ設定ファイルから読む (AUTO-34 の仕様 9、AUTO-36 の仕様 8)。
        using var host = new ScriptHost(scriptFolders, trust, () => ScriptLimits.FromSettings(settings.Number),
            () => settings.String(ScriptSettingKeys.Policy, "none"), _csharp());
        using var env = new CoreScriptEnvironment(language: request.Language)
        {
            AppVersion = typeof(CoreScriptRunner).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0",
            DryRun = request.DryRun,
        };
        env.Ui = new CliScriptUi(env, request.Error);
        env.Templates = new TemplateScriptEngine(TemplateCatalog.CreateDefault(Path.Combine(scriptFolders.Settings, "templates")));
        PluginManager? previousPlugins = PluginManager.Current;
        try
        {
            LoadPlugins(scriptFolders, trust, env, request, settings);
            return RunCore(request, cancellationToken, scriptFolders, host, env);
        }
        finally
        {
            PluginManager.Current = previousPlugins;
        }
    }

    /// <summary>
    /// プラグイン (AUTO-30) を読み込む: アプリで「有効にする」を選んで信頼済みにしたものだけ (確認を出せないため)。管理者として実行中は
    /// <c>--trust</c> がある場合だけ (AUTO-34 の仕様 8)。署名の失効の確認はキャッシュだけを使い、通信しない。
    /// </summary>
    private static void LoadPlugins(ScriptFolders folders, TrustStore trust, CoreScriptEnvironment env, ScriptRunRequest request, CliSettings settings)
    {
        string root = Path.Combine(folders.Settings, "plugins");
        if (!Directory.Exists(root))
        {
            return;
        }

        var manager = new PluginManager(new PluginHostOptions
        {
            PluginsRoot = root,
            AppVersion = env.AppVersion,
            Trust = trust,
            Verifier = new PluginVerifier(),
            Policy = () => settings.String(ScriptSettingKeys.Policy, "none"),
            SignedOnly = () => settings.Bool("plugins.signedOnly", false),
            Prompts = null,
            CommandLineTrust = request.Trust,
            IsElevated = env.IsElevated,
            HasUi = false,
            UiLanguage = request.Language ?? "en",
            Output = env.Output,
        });
        PluginManager.Current = manager;
        PluginIntegration.Attach(manager);
        manager.Discover();
        manager.LoadAllAsync().GetAwaiter().GetResult();
    }

    private sealed class PluginVerifier : IPluginSignatureVerifier
    {
        public FileSignature Verify(string path)
        {
            Platform.Security.AuthenticodeVerifier.SignatureResult r = Platform.Security.AuthenticodeVerifier.VerifyPluginFile(path, offline: true);
            return r.Status switch
            {
                Platform.Security.AuthenticodeVerifier.SignatureStatus.Valid => new FileSignature(PluginSignatureState.Valid, r.Publisher, r.Thumbprint, null, r.RevocationUnchecked),
                Platform.Security.AuthenticodeVerifier.SignatureStatus.Unsigned => FileSignature.Unsigned,
                _ => new FileSignature(PluginSignatureState.Invalid, Problem: $"0x{r.Code:X8}"),
            };
        }
    }

    private static int RunCore(ScriptRunRequest request, CancellationToken cancellationToken, ScriptFolders scriptFolders, ScriptHost host, CoreScriptEnvironment env)
    {

        // 開いたファイル (仕様 2: 開いた順に app.documents に入り、最初のファイルが app.activeDocument)。
        var originals = new Dictionary<Document, string>();
        env.Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < request.Documents.Count; i++)
            {
                Document document = request.Documents[i];
                string? path = request.DocumentPaths is { } paths && i < paths.Count ? paths[i] : null;
                if (path is not null)
                {
                    originals[document] = path;
                }

                env.Add(document, path, path is null ? $"document{i + 1}" : Path.GetFileName(path), "file", activate: i == 0);
            }
        });

        // 保存 (仕様 4): 開いたときのパスへの保存はコマンドラインの扱い (--dry-run など) に任せる。別の名前での保存は通常の保存。
        env.SaveOverride = h => originals.TryGetValue(h.Document, out string? original) && string.Equals(original, h.Path, StringComparison.OrdinalIgnoreCase)
            ? request.SaveDocument(h.Document)
            : null;

        ScriptRun? run = null;
        object writeLock = request.Error;
        host.RunStarted += (_, r) => run = r;
        env.Output.LinesAdded += (_, line) =>
        {
            TextWriter? target = line.Kind == ScriptOutputKind.Info
                ? (line.Channel.StartsWith(ScriptRun.ChannelPrefix, StringComparison.Ordinal) && run?.ExecutionFinished != true ? request.Output : null)
                : request.Error;
            if (target is null)
            {
                return;
            }

            lock (writeLock)
            {
                target.WriteLine(line.Text);
            }
        };

        var coreRequest = new Core.Scripting.ScriptRunRequest
        {
            Path = request.ScriptPath,
            Headless = true,
            CommandLineArgs = request.Arguments.GroupBy(a => a.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal),
            CommandLineTrust = request.Trust,
            CommandLineAllow = [.. request.Allow],
            TimeLimitOverride = request.Timeout,
            SkipParameterDialog = true,
        };
        ScriptRun started = host.Start(coreRequest, env);
        using CancellationTokenRegistration registration = cancellationToken.Register(started.Stop);
        ScriptRunResult result = started.Completion.GetAwaiter().GetResult();
        lock (writeLock)
        {
            request.Output.Flush();
            request.Error.Flush();
        }

        return result.ProcessExitCode;
    }

    private static ScriptFolders DefaultFolders()
    {
        DataLocations? l = CliLocations.Current;
        string root = l?.Root ?? Path.Combine(Path.GetTempPath(), "HexEditor", "cli");
        return new ScriptFolders(l?.Settings ?? Path.Combine(root, "settings"), root);
    }

    /// <summary>GUI で追加した C# スクリプト コンポーネント (AUTO-03)。コマンドラインからは追加しない (ダウンロードしない)。</summary>
    private static CSharpComponentManager? DefaultComponents()
    {
        if (CliLocations.Current is not { } l)
        {
            return null;
        }

        CSharpComponentManifest? manifest = null;
        try
        {
            using Stream? stream = typeof(CoreScriptRunner).Assembly.GetManifestResourceStream(ManifestResource);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                manifest = CSharpComponentManifest.Parse(reader.ReadToEnd());
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            manifest = null;
        }

        return new CSharpComponentManager(l.Components, manifest, null, new Verifier(), () => false);
    }

    private sealed class Verifier : IAuthenticodeVerifier
    {
        public bool IsMicrosoftSigned(string path, out string? error) => Platform.Security.AuthenticodeVerifier.IsMicrosoftSigned(path, out error);
    }

    /// <summary><c>ui.message</c> と状態の表示は標準エラー出力に出す (仕様 6)。ほかは UI のない既定の扱い (AUTO-16 の仕様 4)。</summary>
    private sealed class CliScriptUi(CoreScriptEnvironment env, TextWriter error) : HeadlessScriptUi(env)
    {
        public override Task MessageAsync(string scriptName, string text, string kind, bool modal)
        {
            lock (error)
            {
                error.WriteLine(text);
            }

            return Task.CompletedTask;
        }

        public override void Status(string text)
        {
        }
    }
}
