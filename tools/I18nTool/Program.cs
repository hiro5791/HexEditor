// 翻訳の運用のツール (09 の UI-48、UI-49、10 の PKG-29)。リポジトリのルートで実行する。
//
//   dotnet run --project tools/I18nTool -- check-glossary            用語集の形式と、訳さない用語の検査 (違反は警告)
//   dotnet run --project tools/I18nTool -- check-status              状態ファイルの形式と .resw との食い違いの検査 (誤りは失敗)
//   dotnet run --project tools/I18nTool -- translate                 未翻訳の文字列を機械翻訳する (環境変数 HEX_TRANSLATOR_KEY と
//                                                                    HEX_TRANSLATOR_ENDPOINT。既定は DeepL の https://api.deepl.com/)
//   dotnet run --project tools/I18nTool -- coverage --out <file>      translation-coverage.json を書く
//   dotnet run --project tools/I18nTool -- release-table --coverage <file> [--previous <file>] [--out <file>]
//                                                                    リリースノートの翻訳の進捗の表 (Markdown)
//
// 共通のオプション: --strings <フォルダ> (既定 src/HexEditor.App/Strings)、--status <ファイル> (既定 docs/i18n/translation-status.json)、
// --glossary <ファイル> (既定 docs/i18n/glossary.csv)。警告は GitHub Actions の ::warning:: の形で出す。
using HexEditor.I18nTool;

return await Cli.RunAsync(args);

namespace HexEditor.I18nTool
{
    public static class Cli
    {
        public static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: I18nTool check-glossary | check-status | translate | coverage --out <file> | release-table --coverage <file>");
                return 2;
            }

            string Option(string name, string fallback)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            }

            string strings = Option("--strings", Path.Combine("src", "HexEditor.App", "Strings"));
            string statusPath = Option("--status", Path.Combine("docs", "i18n", "translation-status.json"));
            string glossaryPath = Option("--glossary", Path.Combine("docs", "i18n", "glossary.csv"));
            try
            {
                switch (args[0])
                {
                    case "check-glossary":
                    {
                        Glossary glossary = Glossary.Load(glossaryPath);
                        int errors = Report(glossary.FormatErrors(), "error");
                        Dictionary<string, string> english = Resw.LoadMap(Resw.PathFor(strings, "en"));
                        var translations = Languages.All.Where(l => l.Tag != "en")
                            .ToDictionary(l => l.Tag, l => (IReadOnlyDictionary<string, string>)Resw.LoadMap(Resw.PathFor(strings, l.Tag)));
                        int warnings = Report(glossary.DoNotTranslateViolations(english, translations)
                            .Select(v => $"{v.Language}: {v.Key}: the term '{v.Term}' must stay untranslated (glossary.csv do_not_translate)."), "warning");
                        Console.WriteLine($"glossary: {glossary.Terms.Count} term(s), {errors} error(s), {warnings} warning(s).");
                        return errors > 0 ? 1 : 0;
                    }

                    case "check-status":
                    {
                        TranslationStatus status = TranslationStatus.Load(statusPath);
                        Dictionary<string, string> english = Resw.LoadMap(Resw.PathFor(strings, "en"));
                        int errors = Report(status.Mismatches(english, l => Resw.LoadMap(Resw.PathFor(strings, l))), "error");
                        Console.WriteLine($"translation-status.json: {errors} error(s).");
                        return errors > 0 ? 1 : 0;
                    }

                    case "translate":
                    {
                        string? key = Environment.GetEnvironmentVariable("HEX_TRANSLATOR_KEY");
                        if (string.IsNullOrEmpty(key))
                        {
                            Console.WriteLine("::warning::HEX_TRANSLATOR_KEY is not set; nothing was translated.");
                            return 0;
                        }

                        var endpoint = new Uri(Environment.GetEnvironmentVariable("HEX_TRANSLATOR_ENDPOINT") is { Length: > 0 } e ? e : "https://api.deepl.com/");
                        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                        TranslationRunResult result = await MachineTranslation.RunAsync(strings, statusPath, Glossary.Load(glossaryPath), new DeepLTranslator(http, endpoint, key));
                        Report(result.Warnings, "warning");
                        foreach ((string language, int count) in result.Translated.Where(t => t.Value > 0 || result.MarkedStale[t.Key] > 0))
                        {
                            Console.WriteLine($"{language}: {count} translated, {result.MarkedStale[language]} marked stale.");
                        }

                        Console.WriteLine(result.Changed ? "changed=true" : "changed=false");
                        return 0;
                    }

                    case "init-status":
                    {
                        // 状態ファイルを今の .resw から作り直す (一度だけ使う移行用): 日本語は確認済み、他の言語の既存の訳は機械翻訳。
                        Dictionary<string, string> english = Resw.LoadMap(Resw.PathFor(strings, "en"));
                        var status = new TranslationStatus();
                        foreach ((string tag, _) in Languages.All.Where(l => l.Tag != "en"))
                        {
                            Dictionary<string, string> values = Resw.LoadMap(Resw.PathFor(strings, tag));
                            TranslationState state = tag == "ja" ? TranslationState.Reviewed : TranslationState.Machine;
                            foreach ((string key, string source) in english.Where(e => values.ContainsKey(e.Key)))
                            {
                                status.For(tag)[key] = new TranslationEntry(state, TranslationStatus.Hash(source));
                            }
                        }

                        status.Save(statusPath);
                        return 0;
                    }

                    case "coverage":
                    {
                        string json = Coverage.ToJson(Coverage.Compute(strings, TranslationStatus.Load(statusPath)));
                        string? output = Option("--out", string.Empty) is { Length: > 0 } o ? o : null;
                        if (output is null)
                        {
                            Console.Write(json);
                        }
                        else
                        {
                            File.WriteAllText(output, json, new System.Text.UTF8Encoding(false));
                        }

                        return 0;
                    }

                    case "release-table":
                    {
                        string coveragePath = Option("--coverage", string.Empty);
                        if (!File.Exists(coveragePath))
                        {
                            // translation-coverage.json がなければ表を省き、警告を出す (PKG-29 の「エラー」)。
                            Console.WriteLine("::warning::translation-coverage.json was not found; the translation table is left out of the release notes.");
                            return 0;
                        }

                        string previousPath = Option("--previous", string.Empty);
                        string table = ReleaseTable.Build(Coverage.FromJson(File.ReadAllText(coveragePath)),
                            File.Exists(previousPath) ? Coverage.FromJson(File.ReadAllText(previousPath)) : null);
                        string output = Option("--out", string.Empty);
                        if (output.Length > 0)
                        {
                            File.WriteAllText(output, table, new System.Text.UTF8Encoding(false));
                        }
                        else
                        {
                            Console.Write(table);
                        }

                        return 0;
                    }

                    default:
                        Console.Error.WriteLine($"unknown command: {args[0]}");
                        return 2;
                }
            }
            catch (FormatException ex)
            {
                Console.WriteLine($"::error::{ex.Message}");
                return 1;
            }
        }

        private static int Report(IEnumerable<string> messages, string level)
        {
            int count = 0;
            foreach (string message in messages)
            {
                Console.WriteLine($"::{level}::{message}");
                count++;
            }

            return count;
        }
    }
}
