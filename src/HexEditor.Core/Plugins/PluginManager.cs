using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.Core.Scripting;
using HexEditor.Sdk.Plugins;

namespace HexEditor.Core.Plugins;

/// <summary>信頼の確認の答え (AUTO-34 の仕様 2: プラグインは「有効にする」「有効にしない」。AUTO-35 の仕様 3: 「この発行者を常に信頼する」)。</summary>
public enum PluginTrustAnswer
{
    Enable,

    /// <summary>有効にし、同じ発行者の署名のあるプラグインを以後確認しない。</summary>
    EnableAndTrustPublisher,

    DontEnable,
}

/// <summary>信頼の確認ダイアログに出す内容 (AUTO-34 の仕様 2・8、AUTO-35)。</summary>
/// <param name="Trusted">信頼済み (管理者として実行中の一覧で、確認を兼ねない行)。</param>
/// <param name="Update">同じ ID の新しいバージョンの追加 (更新)。</param>
public sealed record PluginTrustPrompt(string Id, string Name, string Version, string Publisher, string Folder, PluginSignatureInfo Signature,
    IReadOnlyList<string> Permissions, string Description, bool FromInternet, bool Elevated, bool Development, bool Update, bool Trusted);

/// <summary>プラグインの確認の UI (App はダイアログ。ヘッドレス実行にはない)。</summary>
public interface IPluginPrompts
{
    /// <summary>初めて読み込むプラグイン、ファイルが変わったプラグイン、追加するパッケージの信頼の確認 (AUTO-34 の仕様 1・2)。</summary>
    Task<PluginTrustAnswer> ConfirmTrustAsync(PluginTrustPrompt prompt);

    /// <summary>
    /// 管理者として実行中の起動時の確認 (AUTO-34 の仕様 8): 自動で読み込むプラグインの一覧をまとめて 1 回だけ確認する。信頼されていない行は信頼の確認を兼ねる。
    /// 「読み込む」なら true (既定のボタンは「読み込まない」)。
    /// </summary>
    Task<bool> ConfirmElevatedLoadAsync(IReadOnlyList<PluginTrustPrompt> plugins);
}

/// <summary>プラグインの管理の設定 (アプリ・CLI・テストが作る)。</summary>
public sealed class PluginHostOptions
{
    /// <summary><c>&lt;設定フォルダ&gt;/plugins</c> (AUTO-30 の仕様 2)。</summary>
    public required string PluginsRoot { get; init; }

    /// <summary>アプリのバージョン (<c>minAppVersion</c> の判定)。</summary>
    public required string AppVersion { get; init; }

    /// <summary>アプリの SDK のバージョン (テスト用のビルドは 1 つ前のマイナーバージョンを宣言できる。TC-AUTO-29-02)。</summary>
    public Version AppSdkVersion { get; init; } = CurrentSdkVersion;

    public required TrustStore Trust { get; init; }

    public required IPluginSignatureVerifier Verifier { get; init; }

    /// <summary>確認の UI (null はヘッドレス実行: 確認を出せないので、信頼済みのものだけを読み込む)。</summary>
    public IPluginPrompts? Prompts { get; init; }

    /// <summary>組織の設定 <c>scripts.policy</c> (AUTO-34 の仕様 9)。</summary>
    public Func<string> Policy { get; init; } = () => "none";

    /// <summary>設定「署名されたプラグインだけを読み込む」(AUTO-35 の仕様 2)。</summary>
    public Func<bool> SignedOnly { get; init; } = () => false;

    /// <summary>セーフモード (AUTO-30 の仕様 8): プラグインを 1 つも読み込まない。</summary>
    public bool SafeMode { get; init; }

    /// <summary>アプリ全体を管理者として実行中 (AUTO-34 の仕様 8)。</summary>
    public bool IsElevated { get; init; }

    /// <summary>コマンドラインの <c>--trust</c> (ヘッドレス実行で、管理者として実行中でも読み込む)。</summary>
    public bool CommandLineTrust { get; init; }

    /// <summary>開発用のフォルダ (AUTO-33。設定と <c>--plugin-dev</c>)。</summary>
    public IReadOnlyList<string> DevFolders { get; init; } = [];

    /// <summary>ウィンドウのあるアプリ (パネルを登録できる)。</summary>
    public bool HasUi { get; init; }

    /// <summary>表示言語 (プラグインの名前・文字列)。</summary>
    public string UiLanguage { get; init; } = "en";

    /// <summary>出力パネルの元 (null なら出さない)。</summary>
    public ScriptOutputLog? Output { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>この SDK (アプリが参照している HexEditor.Sdk のバージョン)。</summary>
    public static Version CurrentSdkVersion { get; } = Version.Parse(SdkInfo.Version);
}

/// <summary>パッケージの追加の結果 (AUTO-32 の仕様 2)。</summary>
public sealed record PluginInstallResult(bool Installed, string Message, string? Id = null, string? Version = null, bool Update = false);

/// <summary>
/// プラグインの読み込みと管理 (アプリ全体で 1 つ。AUTO-30〜AUTO-35)。
/// <list type="number">
/// <item>見つける: <c>plugins/&lt;id&gt;/&lt;version&gt;/</c> の最新のバージョンと開発用のフォルダ。</item>
/// <item>マニフェストを検証する (必須項目、SDK のバージョン、アプリのバージョン、入口のアセンブリ、参照している SDK のバージョン)。</item>
/// <item>署名を確かめる (無効なら読み込まない。設定・組織の設定で署名なしを拒否する)。</item>
/// <item>信頼の確認 (初めて・ファイルが変わったもの。管理者として実行中は一覧をまとめて 1 回)。</item>
/// <item>プラグインごとの <see cref="PluginLoadContext"/> で読み込み、<see cref="IPlugin.Initialize"/> を呼び、拡張ポイントを登録する。</item>
/// </list>
/// アンロードには対応しない (無効化・削除・更新は再起動で反映する)。
/// </summary>
public sealed class PluginManager : IPluginGuardHost
{
    public const string TrustKindDevFolder = "plugin-dev";
    public const string TrustKindPublisher = "publisher";
    private const string StateFile = "state.json";
    private const string StagingFolder = ".staging";

    private readonly List<PluginRecord> _plugins = [];
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _loadStarted;

    public PluginManager(PluginHostOptions options)
    {
        Options = options;
    }

    /// <summary>今のプロセスの管理 (App・CLI が 1 つ作って入れる。Core の各機能 (スクリプトの API など) が使う)。</summary>
    public static PluginManager? Current { get; set; }

    public PluginHostOptions Options { get; }

    public PluginRegistry Registry { get; } = new();

    public IReadOnlyList<PluginRecord> Plugins
    {
        get
        {
            lock (_lock)
            {
                return [.. _plugins];
            }
        }
    }

    public PluginRecord? Find(string id) => Plugins.FirstOrDefault(p => p.Id == id);

    /// <summary>読み込みが終わった (起動時に必要になった機能はこれを待って優先して使う。AUTO-30 の仕様 4)。</summary>
    public Task Loaded => _loaded.Task;

    /// <summary>次の起動で反映する変更がある (「今すぐ再起動」を出す。AUTO-32 の仕様 3)。</summary>
    public bool RestartRequired => Plugins.Any(p => p.PendingRestart) || _restartRequired;

    private bool _restartRequired;

    /// <summary>一覧・状態が変わった (書いたスレッドで発生する)。</summary>
    public event EventHandler? Changed;

    /// <summary>エラーが続いたためプラグインを止めた (App は InfoBar を出す。AUTO-30 の仕様 7)。</summary>
    public event EventHandler<PluginRecord>? Suspended;

    DateTimeOffset IPluginGuardHost.Now => Options.Time.GetUtcNow();

    /// <summary>出力パネルの出力元の名前 (「プラグイン: 名前」)。</summary>
    public string ChannelOf(PluginRecord plugin) => "plugin:" + plugin.NameFor(Options.UiLanguage);

    internal void Write(PluginRecord plugin, ScriptOutputKind kind, string text) => Options.Output?.Write(ChannelOf(plugin), kind, text);

    void IPluginGuardHost.Write(PluginRecord plugin, ScriptOutputKind kind, string text) => Write(plugin, kind, text);

    void IPluginGuardHost.Suspend(PluginRecord plugin, string reason)
    {
        if (plugin.State != PluginState.Enabled)
        {
            return;
        }

        plugin.State = PluginState.Suspended;
        plugin.Reason = reason;
        Write(plugin, ScriptOutputKind.Error, reason);
        Suspended?.Invoke(this, plugin);
        Registry.RaiseChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ================= 見つける =================

    /// <summary>
    /// フォルダを調べて一覧を作る (起動時。読み込みはしない)。削除の予約を先に片付ける。何度呼んでも同じ結果になる
    /// (読み込んだ後は一覧を作り直さない)。
    /// </summary>
    public IReadOnlyList<PluginRecord> Discover()
    {
        lock (_lock)
        {
            if (_loadStarted != 0 && _plugins.Count > 0)
            {
                return [.. _plugins];
            }
        }

        var found = new List<PluginRecord>();
        string root = Options.PluginsRoot;
        if (Directory.Exists(root))
        {
            ApplyPendingRemovals();
            PluginPackage.TryDelete(Path.Combine(root, StagingFolder));
            var removing = new HashSet<string>(LoadState().Remove.Select(r => Path.GetFullPath(Path.Combine(root, r)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase);
            foreach (string idFolder in SafeEnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                string id = Path.GetFileName(idFolder);
                if (id.StartsWith('.') || !PluginManifest.IsValidId(id) || removing.Contains(idFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                {
                    continue;
                }

                if (Latest(idFolder, removing) is not { } versionFolder)
                {
                    continue;
                }

                found.Add(Create(id, versionFolder, development: false));
            }
        }

        foreach (string dev in Options.DevFolders.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            PluginRecord record = Create(null, dev, development: true);
            // 開発用のフォルダのものを使う (同じ ID の入っているものは読み込まない)。
            found.RemoveAll(p => p.Id == record.Id);

            found.Add(record);
        }

        found.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        lock (_lock)
        {
            _plugins.Clear();
            _plugins.AddRange(found);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return found;
    }

    private PluginRecord Create(string? id, string folder, bool development)
    {
        PluginManifest? manifest = null;
        string? problem = null;
        try
        {
            manifest = PluginManifest.Load(folder);
        }
        catch (Exception ex) when (ex is PluginManifestException or IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
        }

        var record = new PluginRecord(manifest?.Id ?? id ?? Path.GetFileName(folder.TrimEnd('\\', '/')), folder, manifest, development);
        record.Guard = new PluginGuard(record, this);
        if (problem is not null)
        {
            record.State = PluginState.Failed;
            record.Reason = problem;
        }
        else if (!development && (manifest!.Id != id || !string.Equals(manifest.Version.ToString(), Path.GetFileName(folder), StringComparison.OrdinalIgnoreCase)))
        {
            record.State = PluginState.Failed;
            record.Reason = Msg.T($"フォルダ ({id}/{Path.GetFileName(folder)}) がマニフェストの ID とバージョン ({manifest.Id} {manifest.Version}) と一致しません。",
                $"The folder ({id}/{Path.GetFileName(folder)}) does not match the manifest ID and version ({manifest.Id} {manifest.Version}).");
        }

        record.FromInternet = File.Exists(Path.Combine(folder, ".motw"));
        return record;
    }

    private static string? Latest(string idFolder, HashSet<string> removing)
    {
        string? best = null;
        PluginVersion? bestVersion = null;
        foreach (string dir in SafeEnumerateDirectories(idFolder).Where(d => !removing.Contains(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))))
        {
            if (PluginVersion.TryParse(Path.GetFileName(dir), out PluginVersion? v) && File.Exists(Path.Combine(dir, PluginManifest.FileName))
                && (bestVersion is null || v!.CompareTo(bestVersion) > 0))
            {
                best = dir;
                bestVersion = v;
            }
        }

        return best;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string folder)
    {
        try
        {
            // 再解析ポイント (リンク) はたどらない。
            return [.. new DirectoryInfo(folder).EnumerateDirectories().Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).Select(d => d.FullName)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ================= 読み込み =================

    /// <summary>
    /// 見つけたプラグインを検証し、信頼の確認をして読み込む (起動時に 1 回。最初のウィンドウを表示した後にバックグラウンドで呼ぶ。AUTO-30 の仕様 4)。
    /// </summary>
    public async Task LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _loadStarted, 1) != 0)
        {
            await Loaded.ConfigureAwait(false);
            return;
        }

        try
        {
            if (Plugins.Count == 0)
            {
                Discover();
            }

            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loaded.TrySetResult();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PluginRecord> all = Plugins;
        if (Options.SafeMode)
        {
            foreach (PluginRecord p in all.Where(p => p.State == PluginState.NotLoaded))
            {
                p.Reason = Msg.T("セーフモードで起動したため読み込んでいません。", "Not loaded because the application started in safe mode.");
            }

            return;
        }

        if (Options.Policy() == ScriptSettingKeys.PolicyDisabled)
        {
            foreach (PluginRecord p in all.Where(p => p.State == PluginState.NotLoaded))
            {
                p.Reason = Msg.T("組織の設定 (scripts.policy) でスクリプトとプラグインが無効にされています。", "Scripts and plugins are disabled by the organization setting (scripts.policy).");
            }

            return;
        }

        HashSet<string> disabled = LoadState().Disabled;
        var ready = new List<PluginRecord>();
        foreach (PluginRecord p in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (p.State != PluginState.NotLoaded)
            {
                continue;
            }

            if (disabled.Contains(p.Id))
            {
                p.State = PluginState.Disabled;
                p.Reason = null;
                continue;
            }

            if (Prepare(p))
            {
                ready.Add(p);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        var toLoad = new List<PluginRecord>();
        if (Options.IsElevated)
        {
            // 管理者として実行中: 起動ごとに 1 回だけ、読み込む一覧をまとめて確認する (仕様 8)。
            bool load = Options.Prompts is null
                ? Options.CommandLineTrust
                : ready.Count == 0 || await Options.Prompts.ConfirmElevatedLoadAsync([.. ready.Select(p => PromptFor(p, update: false, elevated: true))]).ConfigureAwait(false);
            if (!load)
            {
                foreach (PluginRecord p in ready)
                {
                    p.Reason = Msg.T("管理者として実行中のため、読み込まないことを選びました。", "Chose not to load while running as administrator.");
                }

                return;
            }

            toLoad.AddRange(ready);
        }
        else
        {
            foreach (PluginRecord p in ready)
            {
                if (IsTrusted(p))
                {
                    toLoad.Add(p);
                    continue;
                }

                if (Options.Prompts is null)
                {
                    p.Reason = Msg.T("信頼されていないため読み込んでいません (アプリで「有効にする」を選ぶと信頼済みになります)。",
                        "Not loaded because it is not trusted (choose \"Enable\" in the application to trust it).");
                    continue;
                }

                PluginTrustAnswer answer = await Options.Prompts.ConfirmTrustAsync(PromptFor(p, update: false, elevated: false)).ConfigureAwait(false);
                if (answer == PluginTrustAnswer.DontEnable)
                {
                    p.State = PluginState.Disabled;
                    p.Reason = Msg.T("信頼の確認で「有効にしない」を選びました。", "\"Don't enable\" was chosen in the confirmation.");
                    SetDisabledInState(p.Id, true);
                    continue;
                }

                RecordTrust(p, answer);
                toLoad.Add(p);
            }
        }

        foreach (PluginRecord p in toLoad)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => LoadOne(p), cancellationToken).ConfigureAwait(false);
            Registry.RaiseChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>読み込む前の検証 (マニフェスト、バージョン、入口、署名)。読み込める状態なら true。読めない理由は <see cref="PluginRecord.Reason"/>。</summary>
    private bool Prepare(PluginRecord p)
    {
        PluginManifest m = p.Manifest!;
        string? problem = PluginCompatibility.SdkProblem(m.Sdk, Options.AppSdkVersion) ?? PluginCompatibility.AppVersionProblem(m.MinAppVersion, Options.AppVersion);
        string entry = Path.Combine(p.Folder, m.Entry);
        if (problem is null && !File.Exists(entry))
        {
            problem = Msg.T($"入口のアセンブリが見つかりません: {m.Entry}", $"The entry assembly is not found: {m.Entry}");
        }

        problem ??= ReferencedSdkProblem(entry);
        if (problem is null)
        {
            p.Signature = PluginSignatures.VerifyFolder(p.Folder, m.Entry, Options.Verifier);
            if (p.Signature.State == PluginSignatureState.Invalid)
            {
                problem = p.Signature.Problem ?? Msg.T("署名が無効です。", "The signature is invalid.");
            }
            else if (p.Signature.State == PluginSignatureState.Unsigned && (Options.SignedOnly() || Options.Policy() == ScriptSettingKeys.PolicySignedPluginsOnly))
            {
                problem = Msg.T("署名されていません (設定で署名されたプラグインだけを読み込みます)。", "Not signed (only signed plugins are loaded by the settings).");
            }
        }

        if (problem is not null)
        {
            p.State = PluginState.Failed;
            p.Reason = problem;
            return false;
        }

        p.ContentHash = ContentHash(p.Folder);
        return true;
    }

    /// <summary>入口のアセンブリが参照している SDK のバージョンの判定 (コードを実行せずにメタデータだけを読む)。</summary>
    private string? ReferencedSdkProblem(string entry)
    {
        try
        {
            using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                return Msg.T("入口が .NET のアセンブリではありません。", "The entry is not a .NET assembly.");
            }

            MetadataReader reader = pe.GetMetadataReader();
            foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
            {
                AssemblyReference reference = reader.GetAssemblyReference(handle);
                if (reader.GetString(reference.Name) == "HexEditor.Sdk")
                {
                    return PluginCompatibility.SdkProblem(new Version(reference.Version.Major, reference.Version.Minor), Options.AppSdkVersion);
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Msg.T($"入口のアセンブリを読めません: {ex.Message}", $"Cannot read the entry assembly: {ex.Message}");
        }
    }

    private bool IsTrusted(PluginRecord p)
    {
        if (p.IsDevelopment)
        {
            return Options.Trust.Find(TrustKindDevFolder, DevKey(p.Folder), "dev") is not null;
        }

        if (Options.Trust.Find(TrustStore.PluginKind, p.Id, p.ContentHash) is not null)
        {
            return true;
        }

        return p.Signature is { State: PluginSignatureState.Valid, Thumbprint: { } thumbprint } && IsPublisherTrusted(thumbprint);
    }

    /// <summary>「この発行者を常に信頼する」を選んだ発行者か (証明書の SHA-256 で識別する)。</summary>
    public bool IsPublisherTrusted(string thumbprint) =>
        Options.Trust.Records.Any(r => r.Kind == TrustKindPublisher && string.Equals(r.Key, thumbprint, StringComparison.OrdinalIgnoreCase));

    private void RecordTrust(PluginRecord p, PluginTrustAnswer answer)
    {
        if (p.IsDevelopment)
        {
            // 開発用のフォルダは、指定したときに 1 回だけ確認する (ビルドのたびに内容が変わるため、フォルダで記録する。AUTO-33 の仕様 2)。
            Options.Trust.Add(TrustKindDevFolder, DevKey(p.Folder), "dev", TrustSource.UserAllowed, []);
            return;
        }

        Options.Trust.Add(TrustStore.PluginKind, p.Id, p.ContentHash, TrustSource.UserAllowed, p.Manifest?.Permissions ?? []);
        if (answer == PluginTrustAnswer.EnableAndTrustPublisher && p.Signature is { State: PluginSignatureState.Valid, Thumbprint: { } thumbprint })
        {
            Options.Trust.Add(TrustKindPublisher, thumbprint.ToUpperInvariant(), p.Signature.Publisher ?? thumbprint, TrustSource.UserAllowed, []);
        }
    }

    private static string DevKey(string folder) => Path.GetFullPath(folder).TrimEnd('\\', '/').ToUpperInvariant();

    private PluginTrustPrompt PromptFor(PluginRecord p, bool update, bool elevated) => new(p.Id, p.NameFor(Options.UiLanguage), p.Version, p.Publisher, p.Folder,
        p.Signature, p.Manifest?.Permissions ?? [], p.Manifest?.DescriptionFor(Options.UiLanguage) ?? string.Empty, p.FromInternet, elevated, p.IsDevelopment,
        update, Trusted: IsTrusted(p));

    /// <summary>1 つ読み込む (作業用のスレッド)。失敗は状態と理由に残し、アプリは動き続ける。</summary>
    private void LoadOne(PluginRecord p)
    {
        PluginManifest m = p.Manifest!;
        string entry = Path.Combine(p.Folder, m.Entry);
        long start = Stopwatch.GetTimestamp();
        try
        {
            var context = new PluginLoadContext(p.Id, entry);
            Assembly assembly = context.LoadFromAssemblyPath(Path.GetFullPath(entry));
            p.AssemblyLoaded = true;
            p.AssemblyName = assembly.GetName().Name;
            Type[] types = PublicTypes(assembly);

            List<Type> entries = [.. types.Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPlugin).IsAssignableFrom(t))];
            if (entries.Count != 1)
            {
                Fail(p, entries.Count == 0
                    ? Msg.T("IPlugin を実装した公開の型がありません。", "No public type implements IPlugin.")
                    : Msg.T("IPlugin を実装した公開の型が複数あります。", "More than one public type implements IPlugin."));
                return;
            }

            var plugin = (IPlugin)Activator.CreateInstance(entries[0])!;
            var ctx = new PluginContext(this, p);
            p.State = PluginState.Enabled;
            p.Reason = null;
            plugin.Initialize(ctx);
            ctx.CommitPending();
            Write(p, ScriptOutputKind.Info, Msg.T($"読み込みました ({Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms)。", $"Loaded ({Stopwatch.GetElapsedTime(start).TotalMilliseconds:0} ms)."));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Exception shown = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            Fail(p, Msg.T($"読み込みに失敗しました: {shown.GetType().Name}: {shown.Message}", $"Failed to load: {shown.GetType().Name}: {shown.Message}"));
        }
    }

    /// <summary>
    /// 公開の型。一部の型が読めない場合 (ヘッドレス実行で WinUI の型を使うパネルなど) は、読めた型だけを返す。
    /// </summary>
    private static Type[] PublicTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (Exception ex) when (ex is ReflectionTypeLoadException or FileNotFoundException or FileLoadException or TypeLoadException)
        {
            try
            {
                return [.. assembly.GetTypes().Where(t => t.IsPublic)];
            }
            catch (ReflectionTypeLoadException partial)
            {
                return [.. partial.Types.Where(t => t is { IsPublic: true }).Cast<Type>()];
            }
        }
    }

    private void Fail(PluginRecord p, string reason)
    {
        p.State = PluginState.Failed;
        p.Reason = reason;
        Write(p, ScriptOutputKind.Error, reason);
    }

    /// <summary>登録した拡張ポイントを登録簿に入れる。重複などの警告は出力パネルに出す。</summary>
    internal void Commit(PluginRecord p, IEnumerable<IExtension> extensions)
    {
        foreach (IExtension extension in extensions)
        {
            if (Registry.Add(p, extension) is { } warning)
            {
                Write(p, ScriptOutputKind.Warning, warning);
            }
        }

        Registry.RaiseChanged();
    }

    // ================= 内容のハッシュ =================

    /// <summary>全ファイルのハッシュ (相対パスと各ファイルの SHA-256 から。AUTO-34 の仕様 1 の「id + 全ファイルのハッシュ」)。</summary>
    public static string ContentHash(string folder)
    {
        var sb = new StringBuilder();
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(folder, f).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).Select(f => f.Full))
        {
            string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (relative == ".motw")
            {
                continue;
            }

            using FileStream stream = File.OpenRead(file);
            sb.Append(relative.ToUpperInvariant()).Append('\0').Append(Convert.ToHexStringLower(SHA256.HashData(stream))).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ================= 追加・有効 / 無効・削除 (AUTO-32) =================

    /// <summary>
    /// <c>.hexplugin</c> を追加する (AUTO-32 の仕様 2): パッケージを検証し、一時的な場所に展開してマニフェストと署名を確かめ、信頼の確認を
    /// 出してから <c>plugins/&lt;id&gt;/&lt;version&gt;/</c> に置く。同じ ID の新しいバージョンなら更新として扱う。反映は再起動後。
    /// </summary>
    public async Task<PluginInstallResult> InstallAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        string staging = Path.Combine(Options.PluginsRoot, StagingFolder, Guid.NewGuid().ToString("N"));
        try
        {
            if (!File.Exists(packagePath))
            {
                return new PluginInstallResult(false, Msg.T("ファイルが見つかりません。", "The file is not found."));
            }

            PluginManifest manifest;
            try
            {
                // 展開の前にマニフェストを検証する (読み込めないものは信頼の確認を出さない)。
                PluginManifest head = PluginPackage.ReadManifest(packagePath);
                if ((PluginCompatibility.SdkProblem(head.Sdk, Options.AppSdkVersion) ?? PluginCompatibility.AppVersionProblem(head.MinAppVersion, Options.AppVersion)) is { } problem)
                {
                    return new PluginInstallResult(false, problem, head.Id, head.Version.ToString());
                }

                manifest = PluginPackage.Extract(packagePath, staging);
            }
            catch (PluginPackageException ex)
            {
                return new PluginInstallResult(false, ex.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var record = new PluginRecord(manifest.Id, staging, manifest, development: false) { FromInternet = MarkOfTheWeb.IsFromInternet(packagePath) };
            record.Guard = new PluginGuard(record, this);
            if (!Prepare(record))
            {
                return new PluginInstallResult(false, record.Reason!, manifest.Id, manifest.Version.ToString());
            }

            if (Options.Policy() == ScriptSettingKeys.PolicyDisabled)
            {
                return new PluginInstallResult(false, Msg.T("組織の設定 (scripts.policy) でスクリプトとプラグインが無効にされています。", "Scripts and plugins are disabled by the organization setting (scripts.policy)."));
            }

            PluginRecord? existing = Find(manifest.Id);
            string target = Path.Combine(Options.PluginsRoot, manifest.Id, manifest.Version.ToString());
            bool update = existing is not null && !existing.IsDevelopment;
            if (Directory.Exists(target))
            {
                if (existing is { AssemblyLoaded: true } && string.Equals(Path.GetFullPath(existing.Folder), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    return new PluginInstallResult(false, Msg.T("同じバージョンのプラグインを読み込み中です。削除して再起動してから追加してください。",
                        "The same version is loaded. Remove it and restart before adding it again."), manifest.Id, manifest.Version.ToString());
                }
            }

            if (Options.Prompts is null)
            {
                return new PluginInstallResult(false, Msg.T("確認を出せないため追加できません。", "Cannot add without a confirmation."));
            }

            PluginTrustAnswer answer = await Options.Prompts.ConfirmTrustAsync(PromptFor(record, update, Options.IsElevated) with { Trusted = false }).ConfigureAwait(false);
            if (answer == PluginTrustAnswer.DontEnable)
            {
                return new PluginInstallResult(false, Msg.T("追加しませんでした。", "Not added."), manifest.Id, manifest.Version.ToString());
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
            if (record.FromInternet)
            {
                File.WriteAllText(Path.Combine(target, ".motw"), "ZoneId=3");
            }

            // 移した後のフォルダで識別を記録する (内容は同じ)。
            RecordTrust(new PluginRecord(manifest.Id, target, manifest, false) { Signature = record.Signature, ContentHash = ContentHash(target), Guard = record.Guard }, answer);
            SetDisabledInState(manifest.Id, false);
            if (update && existing is not null && !string.Equals(Path.GetFullPath(existing.Folder), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                ScheduleRemoval(existing.Folder);
                existing.PendingRestart = true;
            }

            _restartRequired = true;
            Changed?.Invoke(this, EventArgs.Empty);
            return new PluginInstallResult(true, update
                ? Msg.T($"「{manifest.NameFor(Options.UiLanguage)}」を {manifest.Version} に更新しました。再起動すると反映されます。", $"Updated \"{manifest.NameFor(Options.UiLanguage)}\" to {manifest.Version}. Restart to apply.")
                : Msg.T($"「{manifest.NameFor(Options.UiLanguage)}」を追加しました。再起動すると有効になります。", $"Added \"{manifest.NameFor(Options.UiLanguage)}\". Restart to enable it."),
                manifest.Id, manifest.Version.ToString(), update);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PluginInstallResult(false, Msg.T($"追加できませんでした: {ex.Message}", $"Could not add the plugin: {ex.Message}"));
        }
        finally
        {
            PluginPackage.TryDelete(staging);
        }
    }

    /// <summary>有効 / 無効を切り替える (AUTO-32 の仕様 3。再起動で反映する)。</summary>
    public void SetEnabled(string id, bool enabled)
    {
        SetDisabledInState(id, !enabled);
        if (Find(id) is { } p)
        {
            bool loadedNow = p.State is PluginState.Enabled or PluginState.Suspended;
            p.PendingRestart = enabled != loadedNow;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>次の起動で有効か (状態の記録)。</summary>
    public bool IsEnabledInState(string id) => !LoadState().Disabled.Contains(id);

    /// <summary>削除する (読み込み中なら再起動後に消す)。開発用のフォルダは一覧から外すだけで、フォルダは消さない。</summary>
    public bool Remove(string id)
    {
        if (Find(id) is not { } p)
        {
            return false;
        }

        if (p.IsDevelopment)
        {
            return false;
        }

        string idFolder = Path.GetDirectoryName(p.Folder)!;
        if (p.AssemblyLoaded)
        {
            ScheduleRemoval(idFolder);
            p.PendingRestart = true;
        }
        else
        {
            PluginPackage.TryDelete(idFolder);
            lock (_lock)
            {
                _plugins.Remove(p);
            }
        }

        Options.Trust.Revoke(TrustStore.PluginKind, id);
        SetDisabledInState(id, false);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ================= 異常終了 (AUTO-30 の仕様 7) =================

    private const string CrashFile = "crash.json";

    /// <summary>呼び出し履歴にプラグインのアセンブリが含まれていれば、そのプラグインを記録する (次の起動で無効にするか確認する)。</summary>
    public PluginRecord? RecordCrash(string stackTrace)
    {
        PluginRecord? suspect = Plugins.FirstOrDefault(p => p.AssemblyName is { } name && stackTrace.Contains(name + ".", StringComparison.Ordinal));
        if (suspect is null)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Options.PluginsRoot);
            File.WriteAllText(Path.Combine(Options.PluginsRoot, CrashFile), new JsonObject { ["id"] = suspect.Id }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return suspect;
    }

    /// <summary>前回の異常終了の原因と考えられるプラグインの ID を取り出す (記録は消す)。</summary>
    public string? TakeCrashSuspect()
    {
        string path = Path.Combine(Options.PluginsRoot, CrashFile);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string? id = JsonNode.Parse(File.ReadAllText(path))?["id"]?.GetValue<string>();
            File.Delete(path);
            return id is not null && PluginManifest.IsValidId(id) ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // ================= 状態の記録 (plugins/state.json) =================

    private sealed record PluginStateData(HashSet<string> Disabled, List<string> Remove);

    private PluginStateData LoadState()
    {
        string path = Path.Combine(Options.PluginsRoot, StateFile);
        var data = new PluginStateData(new HashSet<string>(StringComparer.Ordinal), []);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
            {
                foreach (JsonNode? n in obj["disabled"] as JsonArray ?? [])
                {
                    if (n?.GetValueKind() == JsonValueKind.String)
                    {
                        data.Disabled.Add(n.GetValue<string>());
                    }
                }

                foreach (JsonNode? n in obj["remove"] as JsonArray ?? [])
                {
                    if (n?.GetValueKind() == JsonValueKind.String)
                    {
                        data.Remove.Add(n.GetValue<string>());
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return data;
    }

    private void SaveState(PluginStateData data)
    {
        Directory.CreateDirectory(Options.PluginsRoot);
        var obj = new JsonObject
        {
            ["disabled"] = new JsonArray([.. data.Disabled.Order(StringComparer.Ordinal).Select(s => (JsonNode?)s)]),
            ["remove"] = new JsonArray([.. data.Remove.Select(s => (JsonNode?)s)]),
        };
        string path = Path.Combine(Options.PluginsRoot, StateFile);
        File.WriteAllText(path + ".tmp", obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private void SetDisabledInState(string id, bool disabled)
    {
        lock (_lock)
        {
            PluginStateData data = LoadState();
            if (disabled ? data.Disabled.Add(id) : data.Disabled.Remove(id))
            {
                SaveState(data);
            }
        }
    }

    private void ScheduleRemoval(string folder)
    {
        lock (_lock)
        {
            PluginStateData data = LoadState();
            string relative = Path.GetRelativePath(Options.PluginsRoot, folder);
            if (!data.Remove.Contains(relative))
            {
                data.Remove.Add(relative);
                SaveState(data);
            }
        }
    }

    /// <summary>削除の予約を片付ける (読み込む前。設定フォルダの plugins の中だけを消す)。</summary>
    private void ApplyPendingRemovals()
    {
        lock (_lock)
        {
            PluginStateData data = LoadState();
            if (data.Remove.Count == 0)
            {
                return;
            }

            string root = Path.GetFullPath(Options.PluginsRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var remaining = new List<string>();
            foreach (string relative in data.Remove)
            {
                string full = Path.GetFullPath(Path.Combine(root, relative));
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.Length > root.Length)
                {
                    PluginPackage.TryDelete(full);
                    if (Directory.Exists(full))
                    {
                        // 消せなかった (ファイルが使用中など): 次の起動でもう一度消す。それまでは読み込まない。
                        remaining.Add(relative);
                    }
                }
            }

            data.Remove.Clear();
            data.Remove.AddRange(remaining);
            SaveState(data);
        }
    }
}
