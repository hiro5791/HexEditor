using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HexEditor.TestData;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// UI テスト 1 件分の場所と、起動したアプリ。テストごとに新しい一時フォルダと設定フォルダを使う (テスト方針 6.5)。
/// 失敗したときは、起動中のアプリのスクリーンショット・状態・ログを成果物のフォルダに保存する。
/// 終わったら、起動したアプリをすべて終了し、一時フォルダを消す。
/// </summary>
public sealed class UiTestContext : IAsyncDisposable
{
    private readonly List<AppSession> _sessions = [];
    private int _profiles;

    private UiTestContext(string name)
    {
        Name = name;
        Root = Path.Combine(Path.GetTempPath(), "HexEditor.UITests", $"{name}-{Guid.NewGuid():N}"[..Math.Min(name.Length + 9, 80)]);
        Directory.CreateDirectory(Root);
    }

    public string Name { get; }

    /// <summary>このテストの一時フォルダ。</summary>
    public string Root { get; }

    /// <summary>最初に作った設定フォルダ (同じ設定フォルダで起動し直すときに使う)。</summary>
    public string? DefaultProfile { get; private set; }

    /// <summary>失敗時の成果物の置き場所。環境変数 HEXEDITOR_UITEST_ARTIFACTS で変えられる。</summary>
    public static string ArtifactsRoot =>
        Environment.GetEnvironmentVariable("HEXEDITOR_UITEST_ARTIFACTS") is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppLocator.RepositoryRoot, "TestResults", "ui-artifacts");

    /// <summary>テストの本体を実行する。失敗したら成果物を保存してから例外を投げ直す。</summary>
    public static async Task RunAsync(Func<UiTestContext, Task> body, [CallerMemberName] string name = "")
    {
        await using var context = new UiTestContext(name);
        try
        {
            await body(context);
            context.AssertNoForegroundTheft();
        }
        catch (Exception ex)
        {
            await context.SaveArtifactsAsync(ex);
            throw;
        }
    }

    /// <summary>
    /// テストの本体の中で、別の一時フォルダ・アプリの組を作る (1 つのテストで何度も起動し直す撮影など)。失敗時の成果物の保存は
    /// しないので、呼び出し側で記録する。使い終わったら破棄する。
    /// </summary>
    public static UiTestContext Create(string name) => new(name);

    /// <summary>新しい設定フォルダを作る。</summary>
    public string NewProfile()
    {
        string profile = Path.Combine(Root, $"profile{++_profiles}");
        Directory.CreateDirectory(profile);
        DefaultProfile ??= profile;
        return profile;
    }

    /// <summary>テストデータ (test-data.md の ID) を、このテストの一時フォルダに複製する (編集・保存してよいコピー)。</summary>
    public string CopyTestData(string id, string? fileName = null)
    {
        string source = TestDataCatalog.Get(id);
        string target = Path.Combine(Root, fileName ?? Path.GetFileName(source));
        File.Copy(source, target, overwrite: true);
        return target;
    }

    /// <summary>テストデータの共有のパス (読むだけのテストで使う。保存しないこと)。</summary>
    public string TestData(string id) => TestDataCatalog.Get(id);

    /// <summary>このテストの一時フォルダにファイルを作る。</summary>
    public string WriteFile(string fileName, byte[] content)
    {
        string path = Path.Combine(Root, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>アプリを起動する。設定フォルダの指定がなければ最初の設定フォルダ (なければ新しく作る) を使う。</summary>
    public async Task<AppSession> StartAsync(AppOptions? options = null)
    {
        options ??= new AppOptions();
        string profile = options.Profile ?? DefaultProfile ?? NewProfile();
        string hooks = Path.Combine(Root, $"hooks-{_sessions.Count + 1}.json");
        AppSession session = await AppSession.StartAsync(options, profile, hooks);
        _sessions.Add(session);
        return session;
    }

    /// <summary>アプリが自分で起動したプロセス (再起動の後など) につなぎ、テストの終わりに閉じる。</summary>
    public async Task<AppSession> AttachAsync(System.Diagnostics.Process process, string profile)
    {
        AppSession session = await AppSession.AttachAsync(process, profile);
        _sessions.Add(session);
        return session;
    }

    /// <summary>
    /// 2 つ目の起動 (既存のインスタンスに転送されて終わるもの) を行い、終了コードを返す。
    /// </summary>
    public async Task<int> LaunchAndWaitAsync(AppOptions options, TimeSpan timeout)
    {
        string profile = options.Profile ?? DefaultProfile ?? NewProfile();
        using var process = AppSession.Launch(options, profile, Path.Combine(Root, $"hooks-launch-{Guid.NewGuid():N}.json"));
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The second launch did not exit.");
        }

        return process.ExitCode;
    }

    private void AssertNoForegroundTheft()
    {
        if (_sessions.FirstOrDefault(s => s.StoleForeground) is { } s)
        {
            throw new Xunit.Sdk.XunitException($"HexEditor (pid {s.Pid}) became the foreground window during the test.");
        }
    }

    private async Task SaveArtifactsAsync(Exception error)
    {
        string folder = Path.Combine(ArtifactsRoot, Name);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "error.txt"), error.ToString());
            for (int i = 0; i < _sessions.Count; i++)
            {
                AppSession s = _sessions[i];
                if (s.Process.HasExited)
                {
                    // 落ちたアプリの記録 (クラッシュ情報とログ) を残す。
                    foreach (string sub in new[] { "crash", "logs" })
                    {
                        string source = Path.Combine(s.Profile, sub);
                        if (Directory.Exists(source))
                        {
                            string target = Path.Combine(folder, $"app{i + 1}-{sub}");
                            Directory.CreateDirectory(target);
                            foreach (string file in Directory.EnumerateFiles(source))
                            {
                                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
                            }
                        }
                    }

                    continue;
                }

                s.Screenshot().Save(Path.Combine(folder, $"app{i + 1}.bmp"));
                try
                {
                    JsonObject state = await s.StateAsync();
                    await File.WriteAllTextAsync(Path.Combine(folder, $"app{i + 1}-state.json"), state.ToJsonString(new() { WriteIndented = true }));
                    await File.WriteAllLinesAsync(Path.Combine(folder, $"app{i + 1}-log.txt"), await s.LogAsync());
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 成果物を保存できなくても、元の失敗を伝える。
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (AppSession session in _sessions)
        {
            await session.DisposeAsync();
        }

        // 復旧用データのロックなどが外れるのを待ってから消す。
        for (int attempt = 0; attempt < 10 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }
}
