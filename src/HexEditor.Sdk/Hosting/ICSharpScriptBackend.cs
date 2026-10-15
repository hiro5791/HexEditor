namespace HexEditor.Sdk.Hosting;

/// <summary>
/// The contract between the application and the C# script component (AUTO-03). The component (Roslyn) is not part of the
/// application; it is downloaded separately and implements this interface. Not part of the script API.
/// <ja>アプリと C# スクリプト コンポーネント (AUTO-03) の間の取り決め。コンポーネント (Roslyn) はアプリに含めず、別に入れてこのインターフェイスを実装する。スクリプトの API ではない。</ja>
/// </summary>
public interface ICSharpScriptBackend
{
    /// <summary>The component version. <ja>コンポーネントのバージョン。</ja></summary>
    string Version { get; }

    /// <summary>Compiles a script into an assembly (portable PDB embedded when <see cref="CSharpCompileRequest.Debug"/>). <ja>スクリプトをアセンブリにコンパイルする。</ja></summary>
    CSharpCompileResult Compile(CSharpCompileRequest request, CancellationToken cancellationToken);
}

/// <summary>A compile request. <ja>コンパイルの要求。</ja></summary>
public sealed class CSharpCompileRequest
{
    /// <summary>The script text. <ja>スクリプトの本文。</ja></summary>
    public required string Code { get; init; }

    /// <summary>The script path (used for <c>#load</c>, line numbers and debugging), or null. <ja>スクリプトのパス (<c>#load</c>・行番号・デバッグに使う)。</ja></summary>
    public string? Path { get; init; }

    /// <summary>Folders <c>#load</c> may read from (the script folder and the script library). <ja><c>#load</c> で読めるフォルダ (スクリプトのフォルダとスクリプトライブラリ)。</ja></summary>
    public IReadOnlyList<string> LoadFolders { get; init; } = [];

    /// <summary>Assembly simple names <c>#r</c> may reference and that are referenced by default. <ja><c>#r</c> で参照でき、既定で参照するアセンブリの名前。</ja></summary>
    public IReadOnlyList<string> AllowedReferences { get; init; } = [];

    /// <summary>Paths of the reference assemblies (simple name to file). <ja>参照するアセンブリのファイル (名前からパス)。</ja></summary>
    public IReadOnlyDictionary<string, string> ReferencePaths { get; init; } = new Dictionary<string, string>();

    /// <summary>Compile without optimization and embed debug information. <ja>最適化なしでコンパイルし、デバッグ情報を埋め込む。</ja></summary>
    public bool Debug { get; init; }
}

/// <summary>A compile result. <ja>コンパイルの結果。</ja></summary>
public sealed class CSharpCompileResult
{
    /// <summary>The assembly bytes, or null when compilation failed. <ja>アセンブリのバイト列。失敗した場合は null。</ja></summary>
    public byte[]? Assembly { get; init; }

    /// <summary>Errors and warnings. <ja>エラーと警告。</ja></summary>
    public IReadOnlyList<CSharpDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Contents of the files loaded by <c>#load</c> (path to SHA-256 Hex), for the compile cache key. <ja><c>#load</c> したファイル (パスから SHA-256)。キャッシュのキーに使う。</ja></summary>
    public IReadOnlyDictionary<string, string> LoadedFiles { get; init; } = new Dictionary<string, string>();

    /// <summary>The full name of the type that has the entry point. <ja>入口のある型の完全な名前。</ja></summary>
    public string? EntryType { get; init; }

    /// <summary>The name of the static entry method (<c>(object[]) =&gt; Task&lt;object&gt;</c>). <ja>入口の静的メソッドの名前。</ja></summary>
    public string? EntryMethod { get; init; }
}

/// <summary>A compiler diagnostic. <ja>コンパイラの診断。</ja></summary>
/// <param name="Id">Diagnostic ID such as <c>CS0103</c>. <ja><c>CS0103</c> などの番号。</ja></param>
/// <param name="IsError">True for errors. <ja>エラーか。</ja></param>
/// <param name="Message">Message. <ja>メッセージ。</ja></param>
/// <param name="Path">File path. <ja>ファイルのパス。</ja></param>
/// <param name="Line">1-based line. <ja>行 (1 から)。</ja></param>
/// <param name="Column">1-based column. <ja>列 (1 から)。</ja></param>
public sealed record CSharpDiagnostic(string Id, bool IsError, string Message, string? Path, int Line, int Column);
