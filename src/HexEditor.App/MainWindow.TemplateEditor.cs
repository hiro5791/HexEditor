using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Debugging;
using HexEditor.Core.Templates.Editing;
using HexEditor.Core.Templates.Loading;
using HexEditor.Sdk;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// テンプレートエディタ (TPL-31) とテンプレートデバッガ (TPL-32) のタブ。エディタはタブ列のページ (設定画面と同じ <c>ShowToolPage</c>) で、
/// ドキュメントのタブと同じ並びに出す。コードエディタの有効範囲のコマンド (<c>codeEditor.*</c>。F5・F9・F10・F11・F12 など) は
/// MainWindow.CodeEditor.cs が表示中のエディタに振り分ける。
/// </summary>
public sealed partial class MainWindow : ITemplateEditorHost
{
    private const string TemplateEditorPrefix = "template:";

    private readonly Dictionary<string, TemplateEditorPage> _templateEditors = new(StringComparer.OrdinalIgnoreCase);
    private DocumentViewModel? _lastTemplateTarget;
    private int _newTemplateCount;

    /// <summary>表示中のテンプレートエディタ (なければ null)。</summary>
    internal TemplateEditorPage? ActiveTemplateEditor =>
        ActiveToolPage is { } id && _templateEditors.TryGetValue(id, out TemplateEditorPage? page) ? page : null;

    /// <summary>開いているテンプレートエディタ (テスト用の命令)。</summary>
    internal IReadOnlyCollection<TemplateEditorPage> TemplateEditors => _templateEditors.Values;

    private bool TemplateEditorOpen => _templateEditors.Count > 0;

    private bool TemplateEditorDebugging => _templateEditors.Values.Any(e => e.IsDebugging);

    /// <summary>テスト用: エクスポートの書き込みをこの位置で失敗させる (TC-TPL-25-05)。</summary>
    internal static long? TemplateExportFailAt { get; set; }

    private void RegisterTemplateEditorCommands()
    {
        Commands.Register("templates.new", () => OpenNewTemplateEditor());
        Commands.Register("templates.open", OpenTemplateFileAsync);
        Commands.Register("templates.edit", EditTemplateAsync);
        Commands.Register("templates.manage", () => OpenSettingsPage(Views.TemplateSettingsSection.Category));
        Commands.Register("templates.convertKsy", ConvertKsyAsync);
        Commands.Register("templates.libraryReference", () => OpenUriAsync(Templates.TemplateHelp.LibraryUrl));
        Commands.Register("templates.tryRun", () => ActiveTemplateEditor?.TryRun(), TemplateEditorState);
        Commands.Register("templates.breakOnRead", () => BreakOnRead(Editor?.Cursor ?? 0),
            () => NeedsDocument(_ => TemplateEditorOpen ? null : Loc.Get("TplEd_NoEditor")));

        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected) && Vm.Selected is { } doc)
            {
                _lastTemplateTarget = doc;
            }
        };
        _lastTemplateTarget = Vm.Selected;
        TemplateService.CatalogChanged += TemplateCatalog_Changed;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                TemplateService.CatalogChanged -= TemplateCatalog_Changed;
            }
        };
    }

    private void TemplateCatalog_Changed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() => Views.TemplateSettingsSection.RefreshAll());

    private CommandState TemplateEditorState() =>
        ActiveTemplateEditor is null ? CommandState.Unavailable(Loc.Get("TplEd_NoEditor")) : CommandState.Available;

    // ───────────── エディタのタブ ─────────────

    private static string EditorId(string path) => TemplateEditorPrefix + path.ToLowerInvariant();

    /// <summary>テンプレートをエディタのタブで開く (既に開いていれば前に出す)。</summary>
    internal TemplateEditorPage? OpenTemplateEditor(string path)
    {
        string id = EditorId(path);
        if (_templateEditors.TryGetValue(id, out TemplateEditorPage? existing))
        {
            ShowToolPageTab(id);
            return existing;
        }

        string text;
        try
        {
            text = TemplateCatalog.ReadSource(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice(Loc.Format("TplEd_OpenError", path, ex.Message), InfoBarSeverity.Error);
            return null;
        }

        return AddTemplateEditor(id, new TemplateEditorPage(this, path, text));
    }

    /// <summary>新規作成 (ひな形を入れる。TPL-31 の仕様 5)。</summary>
    internal TemplateEditorPage OpenNewTemplateEditor(string? text = null)
    {
        _newTemplateCount++;
        string id = TemplateEditorPrefix + "new:" + _newTemplateCount.ToString(CultureInfo.InvariantCulture);
        var page = new TemplateEditorPage(this, null, text ?? TemplateLanguageService.NewTemplate(Loc.Get("TplEd_NewName")));
        return AddTemplateEditor(id, page);
    }

    private TemplateEditorPage AddTemplateEditor(string id, TemplateEditorPage page)
    {
        _templateEditors[id] = page;
        page.Paused += (_, _) => RefreshTemplateHighlights();
        page.DebugEnded += (_, _) => RefreshTemplateHighlights();
        ShowToolPage(id, page, page.Title, "");
        page.FocusEditor();
        return page;
    }

    /// <summary>エディタのタブを閉じた (デバッグを止める)。</summary>
    private void TemplateEditorClosed(string id)
    {
        if (_templateEditors.Remove(id, out TemplateEditorPage? page))
        {
            page.StopDebug();
        }
    }

    public async Task OpenTemplateEditorAtAsync(string file, int line, int column)
    {
        await Task.Yield();
        string path = file;
        if (!TemplateCatalog.IsStandardPath(path) && !File.Exists(path))
        {
            ShowNotice(Loc.Format("Tpl_DefinitionMissing", Path.GetFileName(file)), InfoBarSeverity.Warning);
            return;
        }

        if (OpenTemplateEditor(path) is { } page)
        {
            page.MoveCaret(line, column);
        }
    }

    private async Task OpenTemplateFileAsync()
    {
        string? path = (await ScriptPickAsync("open", new PickOptions { Filters = ["*.hxt", "*.ksy"] }))?.FirstOrDefault();
        if (path is not null)
        {
            OpenTemplateEditor(path);
        }
    }

    /// <summary>「テンプレートを編集」: 選んだ適用のテンプレート、なければ一覧から選ぶ。</summary>
    private async Task EditTemplateAsync()
    {
        if (TemplateVm.SelectedItem is { } item && TemplateVm.EntryOf(item) is { } entry)
        {
            OpenTemplateEditor(entry.Info.TemplatePath);
            return;
        }

        if (CurrentTemplates?.Entries.FirstOrDefault() is { } first)
        {
            OpenTemplateEditor(first.Info.TemplatePath);
            return;
        }

        await OpenTemplateFileAsync();
    }

    /// <summary>「.ksy を HXT に変換」(TPL-33 の仕様 4): 変換した結果を新しいエディタのタブで開く (保存先は利用者が選ぶ)。</summary>
    private async Task ConvertKsyAsync()
    {
        string? path = (await ScriptPickAsync("open", new PickOptions { Filters = ["*.ksy"] }))?.FirstOrDefault();
        if (path is null)
        {
            return;
        }

        TemplateProgram program = TemplateLoader.LoadFile(path, TemplateService.LoadOptions);
        if (program.ConvertedHxt is not { } hxt)
        {
            ShowNotice(Loc.Format("TplEd_ConvertError", Path.GetFileName(path)), InfoBarSeverity.Error);
            return;
        }

        TemplateEditorPage page = OpenNewTemplateEditor(hxt);
        page.SetPath(Path.ChangeExtension(path, ".hxt"));
    }

    // ───────────── ITemplateEditorHost ─────────────

    public IReadOnlyList<DocumentViewModel> TemplateTargets => [.. Vm.Documents];

    public DocumentViewModel? LastTemplateTarget => _lastTemplateTarget ?? Vm.Selected;

    /// <summary>「試しに実行」: 対象のドキュメントの同じテンプレートの適用を、保存していない内容で評価し直す (なければ加える)。</summary>
    public void TryRunTemplate(DocumentViewModel target, string templatePath, TemplateProgram program)
    {
        DocumentTemplates templates = DocumentTemplates.For(target);
        TemplateApplicationEntry? entry = templates.Entries.FirstOrDefault(e => e.Debugger is null
            && string.Equals(e.Info.TemplatePath, templatePath, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            entry.Application.SetUnsavedProgram(program);
        }
        else
        {
            AddTemplateApplication(target, new TemplateApplicationInfo { TemplatePath = templatePath }, unsaved: program);
        }

        ShowPanel(TemplatesPanelId, focus: false);
    }

    private readonly Dictionary<TemplateApplicationEntry, ReadOnlyReason> _debugReadOnly = [];

    /// <summary>デバッグを始める: 対象のドキュメントをデバッグ中は読み取り専用にする (TPL-32 の仕様 6)。</summary>
    public TemplateApplicationEntry? StartTemplateDebug(DocumentViewModel target, string templatePath, TemplateProgram program, TemplateDebugger debugger)
    {
        DocumentTemplates templates = DocumentTemplates.For(target);
        foreach (TemplateApplicationEntry old in templates.Entries.Where(e => ReferenceEquals(e.Debugger, debugger)).ToList())
        {
            templates.Remove(old);
        }

        Document doc = target.Document;
        ReadOnlyReason before = doc.ReadOnlyReason;
        if (before == ReadOnlyReason.None)
        {
            doc.SetReadOnly(ReadOnlyReason.User);
        }

        TemplateApplicationEntry? entry = AddTemplateApplication(target, new TemplateApplicationInfo { TemplatePath = templatePath }, unsaved: program, debugger: debugger);
        if (entry is not null)
        {
            _debugReadOnly[entry] = before;
        }
        else if (before == ReadOnlyReason.None)
        {
            doc.SetReadOnly(ReadOnlyReason.None);
        }

        UpdateCommandStates();
        return entry;
    }

    /// <summary>デバッグを終えた: 読み取り専用を元に戻す。</summary>
    public void EndTemplateDebug(TemplateApplicationEntry entry)
    {
        if (_debugReadOnly.Remove(entry, out ReadOnlyReason before) && entry.Owner.Document.ReadOnlyReason == ReadOnlyReason.User && before == ReadOnlyReason.None)
        {
            entry.Owner.Document.SetReadOnly(ReadOnlyReason.None);
        }

        UpdateCommandStates();
        RefreshTemplateHighlights();
    }

    public void TemplateSaved(string path)
    {
        foreach (MainWindow w in WindowManager.Windows)
        {
            foreach (DocumentViewModel doc in w.Vm.Documents)
            {
                DocumentTemplates.Find(doc.Document)?.TemplateSaved(path);
            }
        }

        // インスペクタ用テンプレート (TPL-29) として登録したファイルなら、ユーザー定義型の行を更新する
        InspectorUserTypeService.Instance.ReloadIfChanged();
    }

    /// <summary>「複製して編集」: 利用者のフォルダに複製し (エディタの変更を引き継ぐ)、複製をエディタで開く (TPL-35 の仕様 5)。</summary>
    public async Task DuplicateTemplateAsync(TemplateEditorPage page)
    {
        await Task.Yield();
        string copy;
        try
        {
            copy = TemplateLibrary.Duplicate(page.Path, TemplateService.DefaultUserFolder, page.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice(Loc.Format("TplEd_SaveError", ex.Message), InfoBarSeverity.Error);
            return;
        }

        OpenTemplateEditor(copy);
    }

    public void ShowEditorNotice(string message, InfoBarSeverity severity) => ShowNotice(message, severity);

    /// <summary>「ここを読むときに止める」(TPL-32 の仕様 1): 開いているエディタのデバッガにデータのブレークポイントを加える。</summary>
    private void BreakOnRead(long offset)
    {
        TemplateEditorPage? page = ActiveTemplateEditor ?? _templateEditors.Values.FirstOrDefault();
        if (page is null)
        {
            ShowNotice(Loc.Get("TplEd_NoEditor"), InfoBarSeverity.Informational);
            return;
        }

        page.AddDataBreakpoint(offset);
        ShowStatusMessage(Loc.Format("TplEd_DataBreakpointSet", "0x" + offset.ToString("X", CultureInfo.InvariantCulture)));
    }

    // ───────────── 管理画面から使う操作 ─────────────

    internal Task ExecuteCommandAsync(string id) => Commands.ExecuteAsync(id);

    internal void OpenTemplateEditorFromManager(string path) => OpenTemplateEditor(path);

    internal async Task<string?> PickTemplateFolderAsync() => (await ScriptPickAsync("folder", new PickOptions()))?.FirstOrDefault();

    internal async Task<string?> PickTemplateImportAsync(bool hxtOnly = false) =>
        (await ScriptPickAsync("open", new PickOptions { Filters = hxtOnly ? ["*.hxt"] : ["*.hxt", "*.ksy", "*.zip"] }))?.FirstOrDefault();

    internal async Task<string?> PickTemplateZipSaveAsync(string name) =>
        (await ScriptPickAsync("save", new PickOptions { SuggestedName = name }))?.FirstOrDefault();
}
