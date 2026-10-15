using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Sdk;
using HexEditor.Sdk.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Text;

namespace HexEditor.Scripting.CSharp;

/// <summary>
/// C# スクリプトのコンパイル (AUTO-03 の仕様 1・5・6)。Roslyn の C# スクリプトの書式に従う。<c>#load</c> はスクリプトのフォルダと
/// スクリプトライブラリのフォルダだけ、<c>#r</c> はアプリが許可したアセンブリだけ。NuGet の参照 (<c>#r "nuget:..."</c>) は使えない。
/// コンパイルしたアセンブリにはポータブル PDB を埋め込み、例外のスタックトレースに .csx の行番号を出す (AUTO-22 の仕様 3)。
/// </summary>
public sealed class CSharpScriptBackend : ICSharpScriptBackend
{
    public string Version => typeof(CSharpScriptBackend).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public CSharpCompileResult Compile(CSharpCompileRequest request, CancellationToken cancellationToken)
    {
        var references = new List<MetadataReference>();
        foreach (string name in request.AllowedReferences)
        {
            if (request.ReferencePaths.TryGetValue(name, out string? path) && File.Exists(path))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var sources = new RestrictedSourceResolver(request.LoadFolders, request.Path);
        var metadata = new RestrictedMetadataResolver(request.AllowedReferences, request.ReferencePaths);
        ScriptOptions options = ScriptOptions.Default
            .WithReferences(references)
            .WithImports("System", "System.IO", "System.Linq", "System.Text", "System.Collections.Generic", "System.Threading", "System.Threading.Tasks",
                "HexEditor.Sdk")
            .WithMetadataResolver(metadata)
            .WithSourceResolver(sources)
            .WithEmitDebugInformation(true)
            .WithFileEncoding(new UTF8Encoding(false))
            .WithOptimizationLevel(request.Debug ? OptimizationLevel.Debug : OptimizationLevel.Release)
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithAllowUnsafe(false);
        if (request.Path is not null)
        {
            options = options.WithFilePath(request.Path);
        }

        Script<object> script = CSharpScript.Create(request.Code, options, globalsType: typeof(ScriptGlobals));
        Compilation compilation = script.GetCompilation();
        var diagnostics = new List<CSharpDiagnostic>();
        foreach (Diagnostic d in compilation.GetDiagnostics(cancellationToken))
        {
            if (d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            {
                diagnostics.Add(ToDiagnostic(d));
            }
        }

        if (diagnostics.Any(d => d.IsError))
        {
            return new CSharpCompileResult { Diagnostics = diagnostics, LoadedFiles = sources.Loaded };
        }

        using var pe = new MemoryStream();
        EmitResult emit = compilation.Emit(pe, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded),
            cancellationToken: cancellationToken);
        diagnostics.AddRange(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(ToDiagnostic));
        if (!emit.Success)
        {
            return new CSharpCompileResult { Diagnostics = diagnostics, LoadedFiles = sources.Loaded };
        }

        IMethodSymbol entry = compilation.GetEntryPoint(cancellationToken)
            ?? throw new InvalidOperationException("スクリプトの入口が見つかりません。");
        return new CSharpCompileResult
        {
            Assembly = pe.ToArray(),
            Diagnostics = diagnostics,
            LoadedFiles = sources.Loaded,
            EntryType = entry.ContainingType.MetadataName is var typeName && entry.ContainingType.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? ns.ToDisplayString() + "." + typeName
                : entry.ContainingType.MetadataName,
            EntryMethod = entry.MetadataName,
        };
    }

    private static CSharpDiagnostic ToDiagnostic(Diagnostic d)
    {
        FileLinePositionSpan span = d.Location.GetMappedLineSpan();
        return new CSharpDiagnostic(d.Id, d.Severity == DiagnosticSeverity.Error, d.GetMessage(System.Globalization.CultureInfo.CurrentUICulture),
            string.IsNullOrEmpty(span.Path) ? null : span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    /// <summary><c>#load</c> の解決 (仕様 1: 同じフォルダとスクリプトライブラリのフォルダのみ)。読んだファイルとハッシュを記録する。</summary>
    private sealed class RestrictedSourceResolver(IReadOnlyList<string> folders, string? scriptPath) : SourceReferenceResolver
    {
        private readonly Dictionary<string, string> _loaded = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> Loaded => _loaded;

        public override bool Equals(object? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => 0;

        public override string? NormalizePath(string path, string? baseFilePath) => path;

        public override string? ResolveReference(string path, string? baseFilePath)
        {
            string? baseDir = baseFilePath is not null ? Path.GetDirectoryName(baseFilePath) : scriptPath is not null ? Path.GetDirectoryName(scriptPath) : null;
            string full = Path.GetFullPath(Path.IsPathRooted(path) || baseDir is null ? path : Path.Combine(baseDir, path));
            if (!File.Exists(full) && !Path.IsPathRooted(path))
            {
                foreach (string folder in folders)
                {
                    string candidate = Path.GetFullPath(Path.Combine(folder, path));
                    if (File.Exists(candidate))
                    {
                        full = candidate;
                        break;
                    }
                }
            }

            bool allowed = folders.Any(f => IsUnder(full, f));
            return allowed && File.Exists(full) ? full : null;
        }

        public override Stream OpenRead(string resolvedPath)
        {
            byte[] bytes = File.ReadAllBytes(resolvedPath);
            _loaded[resolvedPath] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            return new MemoryStream(bytes);
        }

        public override SourceText ReadText(string resolvedPath)
        {
            using Stream stream = OpenRead(resolvedPath);
            return SourceText.From(stream, Encoding.UTF8);
        }

        private static bool IsUnder(string full, string folder)
        {
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary><c>#r</c> の解決 (仕様 1・5: 許可したアセンブリの名前だけ。NuGet は使えない)。</summary>
    private sealed class RestrictedMetadataResolver(IReadOnlyList<string> allowed, IReadOnlyDictionary<string, string> paths) : MetadataReferenceResolver
    {
        public override bool Equals(object? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => 0;

        public override ImmutableArray<PortableExecutableReference> ResolveReference(string reference, string? baseFilePath, MetadataReferenceProperties properties)
        {
            string name = reference.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? reference[..^4] : reference;
            if (allowed.Contains(name, StringComparer.OrdinalIgnoreCase) && paths.TryGetValue(name, out string? path) && File.Exists(path))
            {
                return [MetadataReference.CreateFromFile(path, properties)];
            }

            return [];
        }

        public override bool ResolveMissingAssemblies => false;
    }
}
