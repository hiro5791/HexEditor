using Jint;
using JintEngine = Jint.Engine;
using Jint.Runtime.Modules;

namespace HexEditor.Core.Scripting.Js;

/// <summary>
/// モジュールの読み込み (AUTO-02 の仕様 3)。読めるのは、実行中のスクリプトと同じフォルダ以下、スクリプトライブラリのフォルダ、
/// <c>hexeditor:</c> で始まる組み込みモジュールだけ。それ以外は「このモジュールは読み込めません: パス」の例外にする。
/// 名前だけの指定 (<c>import "util"</c>) はスクリプトライブラリから探す。
/// </summary>
internal sealed class ScriptModuleLoader(string mainKey, string mainCode, string? scriptFolder, string libraryFolder) : IModuleLoader
{
    public const string BuiltinPrefix = "hexeditor:";

    private static readonly string[] Extensions = ["", ".js", ".mjs"];

    public ResolvedSpecifier Resolve(string? referencingModuleLocation, ModuleRequest moduleRequest)
    {
        string spec = moduleRequest.Specifier;
        if (spec == mainKey || spec.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
        {
            return new ResolvedSpecifier(moduleRequest, spec, null, SpecifierType.Bare);
        }

        bool relative = spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal)
            || spec.StartsWith(".\\", StringComparison.Ordinal) || spec.StartsWith("..\\", StringComparison.Ordinal);
        string? path = null;
        if (Path.IsPathFullyQualified(spec))
        {
            path = spec;
        }
        else if (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(spec, UriKind.Absolute, out Uri? uri))
        {
            path = uri.LocalPath;
        }
        else if (relative)
        {
            string? baseDir = referencingModuleLocation is null || referencingModuleLocation == mainKey
                ? scriptFolder
                : Path.GetDirectoryName(ToPath(referencingModuleLocation));
            if (baseDir is not null)
            {
                path = Path.Combine(baseDir, spec);
            }
        }
        else
        {
            path = Path.Combine(libraryFolder, spec);
        }

        if (path is not null)
        {
            string full = Path.GetFullPath(path);
            foreach (string ext in Extensions)
            {
                string candidate = full + ext;
                if (File.Exists(candidate) && IsAllowed(candidate))
                {
                    return new ResolvedSpecifier(moduleRequest, candidate, new Uri(candidate), SpecifierType.RelativeOrAbsolute);
                }
            }

            if (!IsAllowed(full))
            {
                throw new ModuleResolutionException(Msg.T($"このモジュールは読み込めません: {full}", $"This module cannot be loaded: {full}"), spec, referencingModuleLocation ?? "", full);
            }

            throw new ModuleResolutionException(Msg.T($"モジュールが見つかりません: {full}", $"Module not found: {full}"), spec, referencingModuleLocation ?? "", full);
        }

        throw new ModuleResolutionException(Msg.T($"このモジュールは読み込めません: {spec}", $"This module cannot be loaded: {spec}"), spec, referencingModuleLocation ?? "", spec);
    }

    public Module LoadModule(JintEngine engine, ResolvedSpecifier resolved)
    {
        string code = resolved.Key == mainKey ? mainCode : File.ReadAllText(resolved.Key);
        return ModuleFactory.BuildSourceTextModule(engine, resolved, code, ModuleParsingOptions.Default);
    }

    private bool IsAllowed(string full) =>
        (scriptFolder is not null && ScriptContext.IsUnder(full, scriptFolder)) || ScriptContext.IsUnder(full, libraryFolder);

    private static string ToPath(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out Uri? uri) && uri.IsFile ? uri.LocalPath : location;
}
