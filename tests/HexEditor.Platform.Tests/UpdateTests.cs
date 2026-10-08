using System.Diagnostics;
using System.Net;
using HexEditor.Platform.Network;
using HexEditor.Platform.Tests.Support;
using HexEditor.Platform.Updates;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// 自動更新 (10 の PKG-17〜PKG-22) の判断の部分。配布元は偽物 (<see cref="FakeReleaseFeed"/>) に差し替え、実際のネットワークには接続しない。
/// インストールした配布物で確かめる部分 (Velopack の差分のダウンロード、再起動、Store) は build/tests/Test-Update.ps1 (CI) が行う。
/// </summary>
public sealed class UpdateTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 9, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();
    private readonly MemorySettings _settings = new();
    private readonly FakeReleaseFeed _feed = new();
    private readonly ManualClock _clock = new(Start);

    public void Dispose() => _temp.Dispose();

    private static readonly UpdateSource Feed = UpdateSource.Parse("https://github.com/test/feed");

    private UpdateService Service(string current, UpdateKind kind = UpdateKind.Portable, IUpdateBackend? backend = null, NetworkClient? network = null)
    {
        SemanticVersion version = SemanticVersion.Parse(current);
        network ??= new NetworkClient(new NetworkPolicy(_settings), version.SemVer, _feed);
        backend ??= new GitHubUpdateBackend(network, Feed, version, kind);
        return new UpdateService(backend, new UpdatePreferences(_settings), new NetworkPolicy(_settings), version, new StateFile(_temp.Path), () => _clock.Now);
    }

    /// <summary>時計を少しずつ進めながら、自動の確認の時刻になったら確認する (アプリの定期的な確認と同じ)。</summary>
    private async Task<List<UpdateCheckResult>> RunAutomaticAsync(UpdateService service, TimeSpan duration)
    {
        var results = new List<UpdateCheckResult>();
        for (TimeSpan t = TimeSpan.Zero; t < duration; t += TimeSpan.FromSeconds(1))
        {
            if (service.IsAutomaticCheckDue())
            {
                results.Add(await service.CheckAsync(manual: false));
            }

            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        return results;
    }

    // ---- PKG-17 更新の確認 ----

    [Fact]
    [Trait(TC, "TC-PKG-17-01")]
    public async Task Turning_off_automatic_checks_sends_nothing_at_start_or_after_24_hours()
    {
        _settings.SetBool(UpdatePreferences.CheckAutomaticallyKey, false);
        UpdateService service = Service("0.9.0");
        Assert.Empty(await RunAutomaticAsync(service, TimeSpan.FromSeconds(60)));
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        Assert.Empty(await RunAutomaticAsync(service, TimeSpan.FromSeconds(60)));
        Assert.Empty(_feed.Requests);

        // 自動の確認の呼び出しがあっても通信しない。
        UpdateCheckResult automatic = await service.CheckAsync(manual: false);
        Assert.Equal(UpdateCheckOutcome.NotChecked, automatic.Outcome);
        Assert.Empty(_feed.Requests);
    }

    [Fact]
    [Trait(TC, "TC-PKG-17-02")]
    public async Task Manual_check_when_up_to_date_shows_the_current_version_without_buttons_and_closes_after_8_seconds()
    {
        UpdateService service = Service("0.9.1");
        UpdateCheckResult result = await service.CheckAsync(manual: true);
        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        UpdateMessage message = UpdatePresentation.ForCheck(result, service.Current, service.Kind)!;
        Assert.Equal("Update_UpToDate", message.MessageKey);
        Assert.Equal(["0.9.1"], message.Arguments);
        Assert.Empty(message.Buttons);
        Assert.True(message.AutoClose);
        Assert.Equal(TimeSpan.FromSeconds(8), UpdatePresentation.UpToDateCloseAfter);

        // 自動の確認では、最新のときは何も表示しない (PKG-17 の仕様 6)。
        Assert.Null(UpdatePresentation.ForCheck(result with { Manual = false }, service.Current, service.Kind));
    }

    [Fact]
    [Trait(TC, "TC-PKG-17-03")]
    public async Task Automatic_check_waits_30_seconds_after_start_and_then_checks_once()
    {
        UpdateService service = Service("0.9.0");
        Assert.Empty(await RunAutomaticAsync(service, TimeSpan.FromSeconds(29)));
        Assert.Empty(_feed.Requests);
        List<UpdateCheckResult> results = await RunAutomaticAsync(service, TimeSpan.FromSeconds(31));
        Assert.Single(results);
        Assert.Single(_feed.Requests);
        Assert.Equal(Start + TimeSpan.FromSeconds(30) + TimeSpan.FromHours(24), service.NextAutomaticCheck);

        // 前回から 24 時間たっていなければ、次の起動でも確認しない (前回から 24 時間後に確認する)。
        _clock.Advance(TimeSpan.FromHours(1));
        UpdateService restarted = Service("0.9.0");
        Assert.Equal(Start + TimeSpan.FromSeconds(30) + TimeSpan.FromHours(24), restarted.NextAutomaticCheck);
        Assert.Empty(await RunAutomaticAsync(restarted, TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Schedule_after_a_long_break_checks_30_seconds_after_start()
    {
        DateTimeOffset started = Start + TimeSpan.FromDays(3);
        Assert.Equal(started + UpdateSchedule.StartupDelay, UpdateSchedule.NextCheck(started, Start));
        Assert.Equal(started + UpdateSchedule.StartupDelay, UpdateSchedule.NextCheck(started, null));
    }

    [Fact]
    [Trait(TC, "TC-PKG-17-04")]
    public async Task Manual_check_without_network_reports_that_it_could_not_check_within_15_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), NetworkClient.DefaultTimeout);
        _feed.Hang = true;
        UpdateService service = Service("0.9.0");
        var watch = Stopwatch.StartNew();
        UpdateCheckResult result = await service.CheckAsync(manual: true);
        watch.Stop();
        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal(UpdateFailure.NoConnection, result.Failure);
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(16));
        UpdateMessage message = UpdatePresentation.ForCheck(result, service.Current, service.Kind)!;
        Assert.Equal("Update_Failed", message.MessageKey);
        Assert.Equal(["Update_Reason_NoConnection"], message.Arguments);

        // 自動の確認の失敗は表示しない (ログだけ)。
        Assert.Null(UpdatePresentation.ForCheck(result with { Manual = false }, service.Current, service.Kind));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, UpdateFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateFailure.ServerError)]
    public async Task Server_errors_are_reported_with_the_reason(HttpStatusCode status, UpdateFailure expected)
    {
        _feed.Status = status;
        UpdateCheckResult result = await Service("0.9.0").CheckAsync(manual: true);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task Offline_mode_never_checks_and_the_manual_check_says_why()
    {
        _settings.SetBool(NetworkPolicy.OfflineKey, true);
        UpdateService service = Service("0.9.0");
        Assert.Empty(await RunAutomaticAsync(service, TimeSpan.FromSeconds(90)));
        UpdateCheckResult manual = await service.CheckAsync(manual: true);
        Assert.Equal(UpdateFailure.Offline, manual.Failure);
        Assert.Empty(_feed.Requests);
    }

    [Fact]
    public async Task Requests_send_only_the_user_agent()
    {
        var network = new NetworkClient(new NetworkPolicy(_settings), "0.9.0", _feed);
        await Service("0.9.0", network: network).CheckAsync(manual: true);
        Assert.Equal("HexEditor/0.9.0", network.UserAgent);
        NetworkRequestRecord record = Assert.Single(network.Requests);
        Assert.Equal("api.github.com", record.Host);
        Assert.Equal("/repos/test/feed/releases", record.Path);
    }

    [Fact]
    public void Test_source_is_read_only_by_test_builds_and_other_hosts_use_api_v3()
    {
        _settings.SetString(UpdatePreferences.TestSourceKey, "https://192.0.2.1/owner/repo", string.Empty);
        Assert.Same(UpdateSource.Product, UpdateSource.Resolve(new UpdatePreferences(_settings, testBuild: false)));
        UpdateSource test = UpdateSource.Resolve(new UpdatePreferences(_settings, testBuild: true));
        Assert.Equal("https://192.0.2.1/api/v3/repos/owner/repo/releases?per_page=30", test.ReleasesApi.AbsoluteUri);
        Assert.Equal("https://192.0.2.1/owner/repo/releases/tag/v0.9.1", test.ReleasePage(SemanticVersion.Parse("0.9.1")).AbsoluteUri);
        Assert.Equal("https://api.github.com/repos/hiro5791/HexEditor/releases?per_page=30", UpdateSource.Product.ReleasesApi.AbsoluteUri);
        Assert.True(UpdateSource.TryParse("http://127.0.0.1:5000/o/r", out _));
        Assert.False(UpdateSource.TryParse("http://example.com/o/r", out _));
        Assert.False(UpdateSource.TryParse("https://github.com/only-owner", out _));
    }

    // ---- PKG-18 インストーラ版 (判断の部分) ----

    [Fact]
    [Trait(TC, "TC-PKG-18-04")]
    public void Restart_to_update_is_disabled_while_a_long_operation_runs()
    {
        Assert.False(UpdateService.CanRestartNow(runningOperations: 1));
        Assert.True(UpdateService.CanRestartNow(runningOperations: 0));
    }

    [Fact]
    public async Task Installer_downloads_automatically_and_then_offers_restart_later_or_release_notes()
    {
        var backend = new RecordingBackend(UpdateKind.Installer, "0.9.1");
        UpdateService service = Service("0.9.0", backend: backend);
        UpdateCheckResult result = await service.CheckAsync(manual: true);
        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.True(service.ShouldDownloadAutomatically);
        await service.DownloadAsync();
        Assert.Equal(UpdatePhase.Ready, service.Phase);
        UpdateMessage ready = UpdatePresentation.Ready(service.Offer!.Version);
        Assert.Equal([UpdateButton.RestartToUpdate, UpdateButton.ReleaseNotes, UpdateButton.Later], ready.Buttons);

        // 「後で」: 次の終了時に適用する。次の起動で「版 X に更新しました」。
        service.ApplyOnExit();
        Assert.Equal(1, backend.ApplyOnExitCalls);
        UpdateService next = Service("0.9.1", backend: new RecordingBackend(UpdateKind.Installer, null));
        Assert.Equal((true, SemanticVersion.Parse("0.9.1")), next.StartupNotice());
        next.RecordRun();
        Assert.Null(Service("0.9.1", backend: new RecordingBackend(UpdateKind.Installer, null)).StartupNotice());
    }

    [Fact]
    public void A_failed_update_is_reported_at_the_next_start()
    {
        new StateFile(_temp.Path).WriteSection(UpdateService.StateSection, new System.Text.Json.Nodes.JsonObject { ["pendingVersion"] = "0.9.1", ["lastRunVersion"] = "0.9.0" });
        Assert.Equal((false, SemanticVersion.Parse("0.9.1")), Service("0.9.0").StartupNotice());
    }

    [Fact]
    public async Task Turning_off_automatic_download_shows_the_download_button()
    {
        _settings.SetBool(UpdatePreferences.DownloadAutomaticallyKey, false);
        UpdateService service = Service("0.9.0", backend: new RecordingBackend(UpdateKind.Installer, "0.9.1"));
        await service.CheckAsync(manual: true);
        Assert.False(service.ShouldDownloadAutomatically);
    }

    // ---- PKG-19 MSIX 版 ----

    [Fact]
    [Trait(TC, "TC-PKG-19-02")]
    public async Task Store_version_does_not_query_the_store_when_automatic_checks_are_off()
    {
        _settings.SetBool(UpdatePreferences.CheckAutomaticallyKey, false);
        int queries = 0;
        var network = new NetworkClient(new NetworkPolicy(_settings), "1.3.0", _feed);
        var backend = new StoreUpdateBackend(network, "HexEditor_abc", _ =>
        {
            queries++;
            return Task.FromResult<IReadOnlyList<Version>>([new Version(1, 3, 1999, 0)]);
        });
        UpdateService service = Service("1.3.0", backend: backend, network: network);
        Assert.Empty(await RunAutomaticAsync(service, TimeSpan.FromSeconds(60)));
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        Assert.Empty(await RunAutomaticAsync(service, TimeSpan.FromSeconds(60)));
        Assert.Equal(0, queries);
        Assert.Empty(network.Requests);

        // 手動の確認では Store に問い合わせ、MSIX の版から SemVer に戻して知らせる。ボタンは「Microsoft Store で更新」。
        UpdateCheckResult manual = await service.CheckAsync(manual: true);
        Assert.Equal(1, queries);
        Assert.Equal("1.3.1", manual.Offer!.Version.SemVer);
        Assert.Equal("ms-windows-store://pdp/?PFN=HexEditor_abc", manual.Offer.DownloadPage!.OriginalString);
        Assert.Equal(UpdateButton.OpenStore, UpdatePresentation.Available(manual.Offer.Version, UpdateKind.Store).Buttons[0]);
    }

    [Fact]
    public void Msix_versions_convert_back_to_semver()
    {
        Assert.Equal("1.3.0-preview.2", SemanticVersion.FromMsixVersion(new Version(1, 3, 2, 0)).SemVer);
        Assert.Equal("1.3.0", SemanticVersion.FromMsixVersion(new Version(1, 3, 999, 0)).SemVer);
        Assert.Equal("1.3.1", SemanticVersion.FromMsixVersion(new Version(1, 3, 1999, 0)).SemVer);
        foreach (string v in new[] { "1.3.0-preview.2", "1.3.0", "2.0.64" })
        {
            Assert.Equal(v, SemanticVersion.FromMsixVersion(SemanticVersion.Parse(v).MsixVersion).SemVer);
        }
    }

    // ---- PKG-20 ポータブル版 ----

    [Fact]
    [Trait(TC, "TC-PKG-20-01")]
    public async Task Portable_version_offers_the_download_page_of_the_new_version()
    {
        UpdateService service = Service("0.9.0");
        UpdateCheckResult result = await service.CheckAsync(manual: true);
        Assert.Equal("0.9.1", result.Offer!.Version.SemVer);
        UpdateMessage message = UpdatePresentation.ForCheck(result, service.Current, service.Kind)!;
        Assert.Equal("Update_Available", message.MessageKey);
        Assert.Equal(["0.9.1"], message.Arguments);
        Assert.Equal([UpdateButton.OpenDownloadPage, UpdateButton.ReleaseNotes, UpdateButton.Skip], message.Buttons);
        Assert.Equal("https://github.com/test/feed/releases/tag/v0.9.1", result.Offer.DownloadPage!.AbsoluteUri);
        Assert.Equal(result.Offer.DownloadPage, result.Offer.ReleaseNotes);
    }

    [Fact]
    [Trait(TC, "TC-PKG-20-02")]
    public async Task Skipping_a_version_hides_it_until_a_newer_version_is_published()
    {
        UpdateService service = Service("0.9.0");
        List<UpdateCheckResult> first = await RunAutomaticAsync(service, TimeSpan.FromSeconds(31));
        Assert.Equal("0.9.1", Assert.Single(first).Offer!.Version.SemVer);
        service.Skip();
        Assert.Equal("0.9.1", _settings.GetString(UpdatePreferences.SkippedVersionKey, string.Empty));

        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        List<UpdateCheckResult> second = await RunAutomaticAsync(service, TimeSpan.FromSeconds(60));
        Assert.Equal(UpdateCheckOutcome.Skipped, Assert.Single(second).Outcome);
        Assert.Null(UpdatePresentation.ForCheck(second[0], service.Current, service.Kind));

        // 手動の確認では、スキップした版も表示する (PKG-22 の仕様 5)。
        Assert.Equal(UpdateCheckOutcome.Available, (await service.CheckAsync(manual: true)).Outcome);
        service.Skip();

        _feed.Publish("0.9.2");
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        List<UpdateCheckResult> third = await RunAutomaticAsync(service, TimeSpan.FromSeconds(60));
        Assert.Equal("0.9.2", Assert.Single(third).Offer!.Version.SemVer);
        Assert.Equal(UpdateCheckOutcome.Available, third[0].Outcome);
    }

    // ---- PKG-21 リリースチャネル ----

    [Fact]
    [Trait(TC, "TC-PKG-21-01")]
    public async Task Preview_channel_finds_the_preview_release()
    {
        UpdateService service = Service("0.9.1");
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await service.CheckAsync(manual: true)).Outcome);
        new UpdatePreferences(_settings).Channel = ReleaseChannel.Preview;
        Assert.Equal("preview", _settings.GetString(UpdatePreferences.ChannelKey, "stable"));
        Assert.Equal("0.9.2-preview.1", (await service.CheckAsync(manual: true)).Offer!.Version.SemVer);
    }

    [Fact]
    [Trait(TC, "TC-PKG-21-02")]
    public async Task Preview_channel_receives_a_newer_stable_release()
    {
        new UpdatePreferences(_settings).Channel = ReleaseChannel.Preview;
        UpdateService service = Service("0.9.2-preview.1");
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await service.CheckAsync(manual: true)).Outcome);
        _feed.Publish("0.9.2");
        UpdateCheckResult result = await service.CheckAsync(manual: true);
        Assert.Equal("0.9.2", result.Offer!.Version.SemVer);
        Assert.Equal(ReleaseChannel.Stable, result.Offer.Version.Channel);
    }

    [Fact]
    [Trait(TC, "TC-PKG-21-03")]
    public async Task Going_back_to_stable_does_not_downgrade()
    {
        new UpdatePreferences(_settings).Channel = ReleaseChannel.Stable;
        UpdateService service = Service("0.9.2-preview.1");
        UpdateCheckResult result = await service.CheckAsync(manual: true);
        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Offer);

        // 「今すぐ最新の安定版に戻す」だけが古い安定版を返す。
        var backend = new GitHubUpdateBackend(new NetworkClient(new NetworkPolicy(_settings), "0.9.2-preview.1", _feed), Feed, SemanticVersion.Parse("0.9.2-preview.1"));
        Assert.Equal("0.9.1", (await backend.FindLatestStableAsync(CancellationToken.None))!.Version.SemVer);
    }

    [Fact]
    public void Velopack_channels_follow_the_architecture_and_the_release_channel()
    {
        Assert.Equal("win-x64-stable", VelopackChannels.For(System.Runtime.InteropServices.Architecture.X64, ReleaseChannel.Stable));
        Assert.Equal("win-arm64-preview", VelopackChannels.For(System.Runtime.InteropServices.Architecture.Arm64, ReleaseChannel.Preview));
    }

    [Fact]
    public void Versions_are_ordered_preview_before_stable_before_the_next_patch()
    {
        string[] ordered = ["0.0.0-local", "0.9.0", "0.9.1", "0.9.2-preview.1", "0.9.2-preview.2", "0.9.2", "0.9.3", "0.10.0", "1.0.0"];
        for (int i = 1; i < ordered.Length; i++)
        {
            Assert.True(SemanticVersion.Parse(ordered[i - 1]) < SemanticVersion.Parse(ordered[i]), $"{ordered[i - 1]} < {ordered[i]}");
        }

        Assert.True(SemanticVersion.Parse("1.2.0+abc").SameVersion(SemanticVersion.Parse("1.2.0+def")));
    }

    // ---- PKG-22 更新の UI ----

    [Fact]
    [Trait(TC, "TC-PKG-22-01")]
    public void Installer_and_portable_show_the_same_message_with_different_first_buttons()
    {
        SemanticVersion v = SemanticVersion.Parse("0.9.1");
        UpdateMessage installer = UpdatePresentation.Available(v, UpdateKind.Installer);
        UpdateMessage portable = UpdatePresentation.Available(v, UpdateKind.Portable);
        UpdateMessage store = UpdatePresentation.Available(v, UpdateKind.Store);
        Assert.Equal(installer.MessageKey, portable.MessageKey);
        Assert.Equal(installer.MessageKey, store.MessageKey);
        Assert.Equal(installer.Arguments, portable.Arguments);
        Assert.Equal([UpdateButton.Download, UpdateButton.ReleaseNotes, UpdateButton.Skip], installer.Buttons);
        Assert.Equal([UpdateButton.OpenDownloadPage, UpdateButton.ReleaseNotes, UpdateButton.Skip], portable.Buttons);
        Assert.Equal([UpdateButton.OpenStore, UpdateButton.ReleaseNotes, UpdateButton.Skip], store.Buttons);
    }

    [Fact]
    [Trait(TC, "TC-PKG-22-03")]
    public async Task The_app_never_restarts_by_itself()
    {
        var backend = new RecordingBackend(UpdateKind.Installer, "0.9.1");
        UpdateService service = Service("0.9.0", backend: backend);
        foreach (UpdateCheckResult r in await RunAutomaticAsync(service, TimeSpan.FromMinutes(11)))
        {
            if (r.Outcome == UpdateCheckOutcome.Available && service.ShouldDownloadAutomatically && service.Phase == UpdatePhase.Available)
            {
                await service.DownloadAsync();
            }
        }

        Assert.Equal(UpdatePhase.Ready, service.Phase);
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        await RunAutomaticAsync(service, TimeSpan.FromMinutes(2));
        Assert.Equal(0, backend.RestartCalls);
        Assert.Equal(0, backend.ApplyOnExitCalls);
    }

    // ---- 見つけた後の確認・自動のダウンロードの再試行 (PKG-17 の仕様 1、PKG-18 の仕様 1・2・「エラー」) ----

    [Fact]
    public async Task Automatic_checks_continue_every_24_hours_after_a_version_was_found()
    {
        UpdateService service = Service("0.9.0");
        Assert.Equal("0.9.1", Assert.Single(await RunAutomaticAsync(service, TimeSpan.FromSeconds(31))).Offer!.Version.SemVer);
        Assert.Equal(UpdatePhase.Available, service.Phase);

        // 帯を閉じただけ (この版をスキップしていない) でも、24 時間後にもう一度確認し、新しい版があればそれを知らせる。
        _feed.Publish("0.9.2");
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        UpdateCheckResult again = Assert.Single(await RunAutomaticAsync(service, TimeSpan.FromSeconds(60)));
        Assert.Equal("0.9.2", again.Offer!.Version.SemVer);
        Assert.Equal(2, _feed.Requests.Count);
    }

    [Fact]
    public async Task A_failed_check_keeps_the_version_found_before()
    {
        UpdateService service = Service("0.9.0");
        await service.CheckAsync(manual: true);
        _feed.Status = HttpStatusCode.InternalServerError;
        Assert.Equal(UpdateCheckOutcome.Failed, (await service.CheckAsync(manual: true)).Outcome);
        Assert.Equal(UpdatePhase.Available, service.Phase);
        Assert.Equal("0.9.1", service.Offer!.Version.SemVer);
    }

    [Fact]
    public async Task A_failed_automatic_download_is_retried_in_the_next_cycle()
    {
        var backend = new RecordingBackend(UpdateKind.Installer, "0.9.1") { FailDownloads = 1 };
        UpdateService service = Service("0.9.0", backend: backend);
        async Task<int> RunAsync(TimeSpan duration)
        {
            int downloads = 0;
            foreach (UpdateCheckResult r in await RunAutomaticAsync(service, duration))
            {
                if (r.Outcome == UpdateCheckOutcome.Available && service.Phase == UpdatePhase.Available && service.ShouldDownloadAutomatically)
                {
                    downloads++;
                    try
                    {
                        await service.DownloadAsync();
                    }
                    catch (IOException)
                    {
                        // 自動のときはログだけ (アプリの DownloadUpdateAsync と同じ)。
                    }
                }
            }

            return downloads;
        }

        Assert.Equal(1, await RunAsync(TimeSpan.FromSeconds(31)));
        Assert.Equal(UpdatePhase.Available, service.Phase);

        // 次の周期 (24 時間後) に確認し直し、もう一度ダウンロードする。
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await RunAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(UpdatePhase.Ready, service.Phase);
        Assert.Equal(2, backend.DownloadCalls);

        // ダウンロード済みなら、自動の確認はしない。
        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        Assert.False(service.IsAutomaticCheckDue());
    }

    [Fact]
    public async Task Pressing_download_during_the_automatic_download_does_not_start_a_second_one()
    {
        var gate = new TaskCompletionSource();
        var backend = new RecordingBackend(UpdateKind.Installer, "0.9.1") { DownloadGate = gate.Task };
        UpdateService service = Service("0.9.0", backend: backend);
        await service.CheckAsync(manual: false);
        Task<bool> first = service.DownloadAsync();
        Assert.Equal(UpdatePhase.Downloading, service.Phase);
        Assert.False(await service.DownloadAsync());
        gate.SetResult();
        Assert.True(await first);
        Assert.False(await service.DownloadAsync());
        Assert.Equal(1, backend.DownloadCalls);
    }

    [Fact]
    public async Task With_automatic_download_the_available_bar_is_not_shown_and_a_manual_check_when_ready_shows_ready()
    {
        var backend = new RecordingBackend(UpdateKind.Installer, "0.9.1");
        UpdateService service = Service("0.9.0", backend: backend);
        UpdateCheckResult automatic = await service.CheckAsync(manual: false);

        // 自動の確認: 「ダウンロード」付きの「版 X があります」は出さず、裏でダウンロードする。
        Assert.Null(UpdatePresentation.ForCheck(automatic, service.Current, service.Kind, downloadsAutomatically: true));

        // 手動の確認: 「版 X があります」を出すが、「ダウンロード」「この版をスキップ」は出さない (ダウンロードは処理センター)。
        UpdateMessage manual = UpdatePresentation.ForCheck(automatic with { Manual = true }, service.Current, service.Kind, downloadsAutomatically: true)!;
        Assert.Equal("Update_Available", manual.MessageKey);
        Assert.Equal([UpdateButton.ReleaseNotes], manual.Buttons);

        // 自動のダウンロードがオフなら、従来どおり「ダウンロード」を出す。
        Assert.Equal(UpdateButton.Download, UpdatePresentation.ForCheck(automatic, service.Current, service.Kind)!.Buttons[0]);

        // ダウンロードが済んだ後の手動の確認は「版 X の準備ができました」(Available ではない)。
        await service.DownloadAsync();
        UpdateCheckResult ready = await service.CheckAsync(manual: true);
        Assert.Equal(UpdateCheckOutcome.Ready, ready.Outcome);
        UpdateMessage message = UpdatePresentation.ForCheck(ready, service.Current, service.Kind, downloadsAutomatically: true)!;
        Assert.Equal("Update_Ready", message.MessageKey);
        Assert.Equal([UpdateButton.RestartToUpdate, UpdateButton.ReleaseNotes, UpdateButton.Later], message.Buttons);
    }

    // ---- PKG-21 の仕様 4 「今すぐ最新の安定版に戻す」 ----

    [Fact]
    public async Task Return_to_stable_is_offered_only_for_a_preview_on_the_stable_channel()
    {
        Assert.False(Service("0.9.1").CanReturnToStable);
        UpdateService preview = Service("0.9.2-preview.1");
        Assert.True(preview.CanReturnToStable);
        new UpdatePreferences(_settings).Channel = ReleaseChannel.Preview;
        Assert.False(preview.CanReturnToStable);
        new UpdatePreferences(_settings).Channel = ReleaseChannel.Stable;
        Assert.False(Service("0.9.2-preview.1", UpdateKind.Store, new RecordingBackend(UpdateKind.Store, null)).CanReturnToStable);

        // 最新の安定版 (今の版より古い) を見つけ、新しい版と同じ流れ (Available) に乗せる。
        UpdateCheckResult result = await preview.FindLatestStableAsync();
        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Equal("0.9.1", result.Offer!.Version.SemVer);
        Assert.Equal(UpdatePhase.Available, preview.Phase);
        Assert.Equal([UpdateButton.OpenDownloadPage, UpdateButton.ReleaseNotes, UpdateButton.Skip],
            UpdatePresentation.ForCheck(result, preview.Current, preview.Kind)!.Buttons);
    }

    [Fact]
    public async Task Return_to_stable_in_offline_mode_does_not_connect()
    {
        _settings.SetBool(NetworkPolicy.OfflineKey, true);
        UpdateCheckResult result = await Service("0.9.2-preview.1").FindLatestStableAsync();
        Assert.Equal(UpdateFailure.Offline, result.Failure);
        Assert.Empty(_feed.Requests);
    }

    /// <summary>呼び出しを数える偽の実装 (インストーラ版の Velopack の代わり)。</summary>
    private sealed class RecordingBackend(UpdateKind kind, string? newest) : IUpdateBackend
    {
        public int RestartCalls { get; private set; }

        public int ApplyOnExitCalls { get; private set; }

        public UpdateKind Kind => kind;

        public Task<UpdateOffer?> FindAsync(ReleaseChannel channel, bool manual, CancellationToken cancellationToken) =>
            Task.FromResult(newest is null ? null : new UpdateOffer(SemanticVersion.Parse(newest), new Uri("https://example.invalid/notes")));

        public int DownloadCalls { get; private set; }

        /// <summary>最初の何回のダウンロードを失敗させるか。</summary>
        public int FailDownloads { get; set; }

        /// <summary>ダウンロードを終わらせるまで待つ (ダウンロード中の操作のテスト)。</summary>
        public Task? DownloadGate { get; set; }

        public async Task DownloadAsync(UpdateOffer offer, Action<int>? progress, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            if (DownloadGate is { } gate)
            {
                await gate;
            }

            if (FailDownloads > 0)
            {
                FailDownloads--;
                throw new IOException("The download failed.");
            }

            progress?.Invoke(100);
        }

        public void ApplyAndRestart(UpdateOffer offer, IReadOnlyList<string> restartArguments) => RestartCalls++;

        public void ApplyOnExit(UpdateOffer offer) => ApplyOnExitCalls++;
    }
}
