using System.Runtime.InteropServices;

namespace HexEditor.Platform;

/// <summary>配布形態 (PKG-01、PKG-12)。</summary>
public enum Distribution
{
    Msix,
    Installer,
    Portable,
    Development,
}

/// <summary>
/// データの保存先 (PKG-13)。<see cref="Root"/> は設定・状態のフォルダ。各フォルダは既定で <see cref="Root"/> の下で、
/// データフォルダに書き込めない場合は <see cref="Recovery"/> だけを一時フォルダの下に移す (PKG-06 の仕様 5 の 3)。
/// </summary>
public sealed record DataLocations(string Root, string Temp, string Components)
{
    public string Settings => Root;

    public string Documents => Path.Combine(Root, "documents");

    public string Recovery { get; init; } = Path.Combine(Root, "recovery");

    public string Crash => Path.Combine(Root, "crash");

    public string Logs => Path.Combine(Root, "logs");
}

/// <summary>
/// 実行環境 (PKG-12)。配布形態ごとの違いはここにまとめ、他のコードは配布形態を直接調べない (PKG-01 の仕様 3)。
/// 右クリックメニュー・更新・管理者権限の補助プロセス (仕様 3 の ShellIntegration、UpdateService、ElevationLauncher) は
/// それぞれの機能 (F1-22、F1-24、F2-09) を作るときに加える。
/// </summary>
public interface IAppEnvironment
{
    Distribution Distribution { get; }

    /// <summary>true なら Microsoft Store 版 (PKG-04)。</summary>
    bool IsPackaged { get; }

    bool IsElevated { get; }

    Architecture ProcessArchitecture { get; }

    /// <summary>SemVer の版 (PKG-28)。InformationalVersion の「+コミット」の部分は除く。</summary>
    string AppVersion { get; }

    /// <summary>バージョン情報に出す版: SemVer + <c>+&lt;コミットの短いハッシュ&gt;</c> (PKG-28 の仕様 3)。</summary>
    string InformationalVersion { get; }

    /// <summary>チャネル (安定版 / プレビュー版。PKG-28: 版にプレリリースの部分があればプレビュー版)。</summary>
    ReleaseChannel Channel { get; }

    /// <summary>タスクバーのまとまりとジャンプリストに使う AppUserModelID (PKG-12 の仕様 4)。</summary>
    string AppUserModelId { get; }

    DataLocations Locations { get; }

    /// <summary>
    /// データフォルダに書き込めるか (起動時に <c>.write-test</c> を作って消して確かめる。PKG-06 の仕様 5)。
    /// false なら設定はセッションの間だけ有効にし、ファイルに書かない。
    /// </summary>
    bool IsDataDirectoryWritable { get; }

    /// <summary>単一インスタンスのキー (PKG-11 の仕様 2)。</summary>
    string InstanceKey { get; }

    /// <summary>判定のときの警告 (ビルドの配布形態との食い違い、判定の失敗など。PKG-12 の仕様 2・「エラー」)。ログに書く。</summary>
    IReadOnlyList<string> Warnings { get; }
}
