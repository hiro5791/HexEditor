using System.Reflection;
using System.Text.RegularExpressions;
using HexEditor.Core.Scripting.Docs;
using HexEditor.Core.Scripting.Js;
using HexEditor.Sdk;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// 型定義 (hexeditor.d.ts)・JSON-RPC のメソッド一覧・API リファレンスが SDK と食い違わない (AUTO-04 の受け入れ基準 1)。
/// 成果物は docs/api/ に置き、SDK を変えたら <c>HEXEDITOR_UPDATE_API=1</c> を付けてこのテストを実行して作り直す (CI では作り直さずに比べる)。
/// </summary>
public sealed class ApiDocTests
{
    private static string ApiFolder
    {
        get
        {
            string? dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "HexEditor.slnx")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return Path.Combine(dir ?? throw new DirectoryNotFoundException("HexEditor.slnx"), "docs", "api");
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-04-01")]
    public void Generated_artifacts_match_the_sdk()
    {
        var gen = new ApiDocGenerator();
        Assert.True(gen.HasDocs, "HexEditor.Sdk.xml (XML ドキュメントコメント) がテストの出力にありません。");
        bool update = Environment.GetEnvironmentVariable("HEXEDITOR_UPDATE_API") == "1";
        var differences = new List<string>();
        foreach ((string file, string content) in gen.Artifacts())
        {
            string path = Path.Combine(ApiFolder, file);
            if (update)
            {
                Directory.CreateDirectory(ApiFolder);
                File.WriteAllText(path, content);
                continue;
            }

            string existing = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) : "";
            if (existing != content)
            {
                differences.Add(file);
            }
        }

        Assert.True(differences.Count == 0,
            $"docs/api の次のファイルが SDK と違います (HEXEDITOR_UPDATE_API=1 で作り直してください): {string.Join(", ", differences)}");
    }

    [Fact]
    [Trait("TC", "TC-AUTO-04-01")]
    public void Every_sdk_member_appears_in_the_type_definitions_and_reference()
    {
        var gen = new ApiDocGenerator();
        string ts = gen.TypeScript(japanese: false);
        string reference = gen.Reference(ja: false);
        string referenceJa = gen.Reference(ja: true);
        var missing = new List<string>();
        foreach (Type t in ApiDocGenerator.ApiTypes.Where(t => t.IsInterface || t.IsAbstract))
        {
            string name = JsBinder.TsName(t);
            Match block = Regex.Match(ts, $@"\n    interface {name}\b[^{{]*\{{(?<body>.*?)\n    \}}", RegexOptions.Singleline);
            Assert.True(block.Success, $"d.ts に interface {name} がありません。");
            foreach (MemberInfo m in ApiDocGenerator.MembersOf(t))
            {
                string js = JsBinder.JsName(m);
                if (m is MethodInfo method)
                {
                    // 引数の数・順序・名前が一致する。
                    string args = string.Join(", ", method.GetParameters().Select(p => p.Name + (ApiDocGenerator.IsOptional(p) ? "?" : "") + ":"));
                    var pattern = new Regex(@"\n        " + Regex.Escape(js) + @"\((?<args>[^\n]*)\): ");
                    Match mm = pattern.Match(block.Groups["body"].Value);
                    string actual = mm.Success ? string.Join(", ", TopLevelNames(mm.Groups["args"].Value)) : "(none)";
                    if (actual != args)
                    {
                        missing.Add($"{name}.{js}({args}) / d.ts: {actual}");
                    }
                }
                else if (!Regex.IsMatch(block.Groups["body"].Value, @"\n        (readonly )?" + Regex.Escape(js) + @"\??: "))
                {
                    missing.Add($"{name}.{js}");
                }

                if (!reference.Contains($"\n### {name}.{js}\n", StringComparison.Ordinal) || !referenceJa.Contains($"\n### {name}.{js}\n", StringComparison.Ordinal))
                {
                    missing.Add($"reference: {name}.{js}");
                }
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>引数の並び (<c>a: T, b?: (x: U) =&gt; V</c>) の最上位の名前 (<c>a:</c>、<c>b?:</c>)。</summary>
    private static IEnumerable<string> TopLevelNames(string args)
    {
        int depth = 0;
        int start = 0;
        var parts = new List<string>();
        for (int i = 0; i <= args.Length; i++)
        {
            char c = i < args.Length ? args[i] : ',';
            if (c is '(' or '<' or '{' or '[')
            {
                depth++;
            }
            else if (c is ')' or '}' or ']' || (c == '>' && i > 0 && args[i - 1] != '='))
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                string part = args[start..i].Trim();
                if (part.Length > 0)
                {
                    parts.Add(part[..(part.IndexOf(':', StringComparison.Ordinal) + 1)]);
                }

                start = i + 1;
            }
        }

        return parts;
    }

    [Fact]
    public void Every_sdk_member_is_documented_in_both_languages()
    {
        var gen = new ApiDocGenerator();
        string en = gen.Reference(ja: false);
        string ja = gen.Reference(ja: true);
        Assert.DoesNotMatch(@"[ぁ-んァ-ン一-龥]", en);
        Assert.Matches(@"[ぁ-んァ-ン一-龥]", ja);
        Assert.Contains("JsonRpc", nameof(ApiDocGenerator.JsonRpcMethods), StringComparison.Ordinal);
        string rpc = gen.JsonRpcMethods();
        Assert.Contains("\"document.readBytes\"", rpc);
        Assert.Contains("\"app.open\"", rpc);
    }

    [Fact]
    public void Js_names_follow_camel_case_without_async()
    {
        Assert.Equal("findAll", JsBinder.JsName(typeof(IDocument).GetMethod(nameof(IDocument.FindAllAsync))!));
        Assert.Equal("toJSON", JsBinder.JsName(typeof(ITemplateNode).GetMethod(nameof(ITemplateNode.ToJson))!));
        Assert.Equal("activeDocument", JsBinder.JsName(typeof(IApp).GetProperty(nameof(IApp.ActiveDocument))!));
        Assert.Equal("Document", JsBinder.TsName(typeof(IDocument)));
    }
}
