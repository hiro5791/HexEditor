using HexEditor.Platform.Network;

namespace HexEditor.Platform.Updates;

/// <summary>
/// MSIX 版 (Microsoft Store 版) の更新の確認 (10 の PKG-17 の仕様 4、PKG-19)。
/// <c>StoreContext.GetAppAndOptionalStorePackageUpdatesAsync()</c> で確かめ、更新は Store に任せる (Store のページを開くだけ)。
/// Store 版は安定版だけ (PKG-21 の仕様 3) なので、チャネルは使わない。
/// </summary>
public sealed class StoreUpdateBackend(NetworkClient network, string packageFamilyName, Func<CancellationToken, Task<IReadOnlyList<Version>>>? query = null)
    : IUpdateBackend
{
    public UpdateKind Kind => UpdateKind.Store;

    /// <summary>Store のアプリのページ (「Microsoft Store で更新」。PKG-19 の仕様 2)。</summary>
    public Uri StorePage => new($"ms-windows-store://pdp/?PFN={Uri.EscapeDataString(packageFamilyName)}");

    public async Task<UpdateOffer?> FindAsync(ReleaseChannel channel, bool manual, CancellationToken cancellationToken)
    {
        // 通信の許可の確認と記録は、他の配布形態と同じ入口で行う (オフラインでは呼ばない。UI-58)。
        network.EnsureAllowed(NetworkFeature.Updates, manual);
        network.Record(NetworkFeature.Updates, "StoreContext", new Uri("ms-windows-store://storecontext/updates"));
        IReadOnlyList<Version> versions = await (query ?? QueryStoreAsync)(cancellationToken).ConfigureAwait(false);
        if (versions.Count == 0)
        {
            return null;
        }

        SemanticVersion version = SemanticVersion.FromMsixVersion(versions.Max()!);
        return new UpdateOffer(version, UpdateSource.Product.ReleasePage(version), StorePage);
    }

    private static async Task<IReadOnlyList<Version>> QueryStoreAsync(CancellationToken cancellationToken)
    {
        var context = Windows.Services.Store.StoreContext.GetDefault();
        IReadOnlyList<Windows.Services.Store.StorePackageUpdate> updates =
            await context.GetAppAndOptionalStorePackageUpdatesAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return [.. updates.Select(u => u.Package.Id.Version).Select(v => new Version(v.Major, v.Minor, v.Build, v.Revision))];
    }
}
