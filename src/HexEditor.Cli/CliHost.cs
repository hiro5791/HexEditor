using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;

namespace HexEditor.Cli;

/// <summary>
/// 1 回の実行の入出力と外部とのつなぎ目 (テストが差し替えられるようにする)。実際の実行は <see cref="ForProcess"/>。
/// </summary>
public sealed class CliHost
{
    /// <summary>標準出力 (UTF-8、BOM なし、改行は LF)。</summary>
    public required TextWriter Out { get; init; }

    /// <summary>標準エラー出力。</summary>
    public required TextWriter Err { get; init; }

    /// <summary>標準入力 (バイト列)。<c>hexed hash -</c> が読む。</summary>
    public required Func<Stream> OpenInput { get; init; }

    /// <summary>標準エラー出力が端末か (AUTO-36 の仕様 5: 端末の場合だけ進捗を出す)。</summary>
    public bool ErrIsTerminal { get; init; }

    /// <summary>標準出力が端末か (色を付けるかの判定。今は色を使わない)。</summary>
    public bool OutIsTerminal { get; init; }

    /// <summary>相対パスの基準のフォルダ。</summary>
    public string CurrentDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Ctrl+C などでキャンセルされたら通知される (終了コード 6。AUTO-36 の仕様 6)。</summary>
    public CancellationToken Cancellation { get; init; }

    /// <summary>外部の機能 (テンプレート・スクリプト・GUI)。</summary>
    public CliServices Services { get; init; } = new();

    /// <summary>メッセージの言語を決める Windows の表示言語の優先順位。</summary>
    public Func<IReadOnlyList<string>> WindowsLanguages { get; init; } = () => [System.Globalization.CultureInfo.CurrentUICulture.Name];

    /// <summary>環境変数 <c>HEXED_TEST_PROFILE</c> 相当 (テスト用: 設定・状態の置き場所を差し替える)。</summary>
    public string? TestProfile { get; init; }

    /// <summary>このプロセスの標準入出力で動く実行環境。</summary>
    public static CliHost ForProcess(CancellationToken cancellation, CliServices services)
    {
        bool outRedirected = Console.IsOutputRedirected;
        bool errRedirected = Console.IsErrorRedirected;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (!outRedirected || !errRedirected)
        {
            try
            {
                Console.OutputEncoding = utf8;
            }
            catch (IOException)
            {
                // コンソールがなければそのまま。
            }
        }

        return new CliHost
        {
            Out = new StreamWriter(Console.OpenStandardOutput(), utf8, 1 << 16) { NewLine = "\n", AutoFlush = false },
            Err = new StreamWriter(Console.OpenStandardError(), utf8, 1 << 12) { NewLine = "\n", AutoFlush = true },
            OpenInput = Console.OpenStandardInput,
            ErrIsTerminal = !errRedirected,
            OutIsTerminal = !outRedirected,
            Cancellation = cancellation,
            Services = services,
            TestProfile = Environment.GetEnvironmentVariable("HEXED_TEST_PROFILE") is { Length: > 0 } p ? p : null,
        };
    }

    /// <summary>パスを絶対パスにする (<see cref="CurrentDirectory"/> が基準)。</summary>
    public string Resolve(string path) => Path.GetFullPath(path, CurrentDirectory);
}

/// <summary>ファイルを開く。ヘッドレスのサブコマンドは <see cref="Document"/> を作って読む (ファイル全体は読み込まない)。</summary>
internal static class CliFiles
{
    /// <summary>読み取り専用で開く。見つからない・開けない場合は終了コード 3。</summary>
    public static Document OpenDocument(string path)
    {
        try
        {
            return new Document(FileByteSource.Open(path), DocumentOptions());
        }
        catch (FileNotFoundException)
        {
            throw CliException.Io("Cli_Error_FileNotFound", path);
        }
        catch (DirectoryNotFoundException)
        {
            throw CliException.Io("Cli_Error_FileNotFound", path);
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliException(ExitCodes.Denied, "Cli_Error_AccessDenied", path);
        }
        catch (IOException ex)
        {
            throw CliException.Io("Cli_Error_CannotRead", path, ex.Message);
        }
    }

    /// <summary>ヘッドレスのドキュメントの設定 (一時ファイルは %TEMP% の下。ファイルをロックしない)。</summary>
    public static DocumentOptions DocumentOptions() => new()
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), "HexEditor", "cli"),
        LockPolicy = FileLockPolicy.None,
    };

    /// <summary>読み込み元のデータソースを開く (編集しない処理)。</summary>
    public static FileByteSource OpenSource(string path)
    {
        try
        {
            return FileByteSource.Open(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw CliException.Io("Cli_Error_FileNotFound", path);
        }
        catch (UnauthorizedAccessException)
        {
            throw new CliException(ExitCodes.Denied, "Cli_Error_AccessDenied", path);
        }
        catch (IOException ex)
        {
            throw CliException.Io("Cli_Error_CannotRead", path, ex.Message);
        }
    }
}
