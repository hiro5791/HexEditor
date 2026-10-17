// コードエディタ (08 の AUTO-20、07 の TPL-31) の WebView2 の中の処理。Monaco Editor を作り、アプリ (MonacoCodeEditor.cs) と
// メッセージ (window.chrome.webview) をやり取りする。アプリが持つ本文の写しは、ここから送る変更の差分で保つ。
// メッセージの種類と内容は MonacoCodeEditor.cs の説明を参照。このページはネットワークを使わない (読み込むのは同じ場所のファイルだけ)。
(function () {
  'use strict';

  const webview = window.chrome && window.chrome.webview;
  const post = (message) => { if (webview) { webview.postMessage(message); } };
  const queued = [];
  const pending = new Map();
  let nextRequest = 1;
  let cfg = null;
  let editor = null;
  let model = null;
  let suppress = 0;
  let forward = new Set();
  let chordPending = false;
  let breakpointIds = [];
  let currentIds = [];
  let markerIds = [];
  let markers = [];
  let currentLine = null;

  // ───── アプリからのメッセージ ─────

  if (webview) {
    webview.addEventListener('message', (e) => {
      const m = e.data;
      if (!m || typeof m !== 'object') {
        return;
      }

      if (m.t === 'init') {
        init(m);
      } else if (!editor) {
        queued.push(m);
      } else {
        handle(m);
      }
    });
  }

  post({ t: 'hello' });

  function init(m) {
    cfg = m;
    const nls = m.nls && m.nls !== 'en' ? { availableLanguages: { '*': m.nls } } : {};
    window.require.config({ paths: { vs: '../vs' }, 'vs/nls': nls });
    window.require(['vs/editor/editor.main'], () => {
      try {
        create();
      } catch (err) {
        post({ t: 'error', message: String(err && err.stack || err) });
        return;
      }

      post({ t: 'ready', len: model.getValueLength(), version: monaco.version || '' });
      for (const q of queued.splice(0)) {
        handle(q);
      }
    }, (err) => post({ t: 'error', message: String(err && err.message || err) }));
  }

  function create() {
    const ts = monaco.typescript || (monaco.languages && monaco.languages.typescript);
    if (cfg.language === 'hxt') {
      registerTemplateLanguage(cfg.hxt || {});
    }

    if (cfg.language === 'javascript' && ts) {
      configureJavaScript(ts);
    }

    if (cfg.hostCompletion) {
      registerHostCompletion(cfg.language, cfg.completionTriggers || ['.']);
    }

    if (cfg.hostFormat) {
      registerHostFormat(cfg.language);
    }

    applyTheme(cfg.theme);
    const uri = monaco.Uri.parse('file:///' + encodeURIComponent(cfg.fileName || 'untitled'));
    model = monaco.editor.createModel(cfg.text || '', cfg.language, uri);
    model.setEOL(monaco.editor.EndOfLineSequence.LF);
    model.updateOptions({ tabSize: 4, insertSpaces: true });
    editor = monaco.editor.create(document.getElementById('container'), {
      model,
      theme: 'hex',
      automaticLayout: true,
      fontFamily: cfg.fontFamily || "'Cascadia Mono', Consolas, monospace",
      fontSize: cfg.fontSize || 14,
      readOnly: !!cfg.readOnly,
      glyphMargin: true,
      lineNumbers: 'on',
      folding: true,
      showFoldingControls: 'always',
      matchBrackets: 'always',
      bracketPairColorization: { enabled: false },
      autoIndent: 'full',
      formatOnType: false,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      renderLineHighlight: 'line',
      accessibilitySupport: 'on',
      ariaLabel: cfg.ariaLabel || '',
      wordBasedSuggestions: cfg.language === 'csharp' ? 'currentDocument' : 'off',
      quickSuggestions: cfg.language === 'hxt' ? false : { other: true, comments: false, strings: false },
      suggestOnTriggerCharacters: true,
      tabFocusMode: false,
      contextmenu: true,
      links: false,
      dragAndDrop: true,
      'semanticHighlighting.enabled': false,
    });
    // Monaco 自身のコマンドパレット (F1) は使わない (AUTO-20 の仕様 5 の 4。F1 はアプリのコマンドパレット)
    monaco.editor.addKeybindingRule({ keybinding: monaco.KeyCode.F1, command: null });
    monaco.editor.addKeybindingRule({ keybinding: monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyP, command: null });
    window.hexEditor = { editor, model, monaco };

    model.onDidChangeContent((e) => {
      if (suppress > 0) {
        return;
      }

      post({ t: 'changed', c: e.changes.map((c) => [c.rangeOffset, c.rangeLength, c.text]), len: model.getValueLength() });
    });
    editor.onDidChangeCursorSelection(() => postSelection());
    editor.onDidFocusEditorText(() => post({ t: 'focus', has: true }));
    editor.onDidBlurEditorText(() => post({ t: 'focus', has: false }));
    editor.onMouseDown((e) => {
      const type = e.target.type;
      if ((type === monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN) && e.target.position) {
        post({ t: 'gutter', line: e.target.position.lineNumber });
      }
    });
    monaco.editor.onDidChangeMarkers((uris) => {
      if (uris.some((u) => u.toString() === model.uri.toString())) {
        postEditorMarkers();
      }
    });
    document.getElementById('container').addEventListener('keydown', onKeyDown, true);
    applyForward(cfg.keys || []);
    setBreakpoints(cfg.breakpoints || []);
    setMarkers(cfg.markers || []);
    setCurrentLine(cfg.currentLine == null ? null : cfg.currentLine);
    if (typeof cfg.caret === 'number' && cfg.caret > 0) {
      const p = model.getPositionAt(cfg.caret);
      editor.setPosition(p);
      editor.revealPositionInCenterIfOutsideViewport(p);
    }
  }

  function handle(m) {
    switch (m.t) {
      case 'setText': setText(m.text, !!m.reset); break;
      case 'replace': replace(m.start, m.length, m.text); break;
      case 'readOnly': editor.updateOptions({ readOnly: !!m.value }); break;
      case 'caret': setCaret(m.start, m.end); break;
      case 'goto': goTo(m.line, m.column, !!m.focus); break;
      case 'focus': editor.focus(); break;
      case 'breakpoints': setBreakpoints(m.items || []); break;
      case 'currentLine': setCurrentLine(m.line == null ? null : m.line); break;
      case 'markers': setMarkers(m.items || []); break;
      case 'theme': applyTheme(m.theme); break;
      case 'keys': applyForward(m.items || []); break;
      case 'chord': chordPending = !!m.on; break;
      case 'action': runAction(m.id); break;
      case 'aria': editor.updateOptions({ ariaLabel: m.value || '' }); break;
      case 'getText': post({ t: 'text', text: model.getValue() }); break;
      case 'reply': {
        const resolve = pending.get(m.id);
        if (resolve) {
          pending.delete(m.id);
          resolve(m);
        }

        break;
      }
    }
  }

  // ───── 本文・カーソル ─────

  function setText(text, reset) {
    text = String(text || '').replace(/\r\n?/g, '\n');
    suppress++;
    try {
      const old = model.getValue();
      if (reset || old.length === 0 || text.length > 1048576 || old.length > 1048576) {
        model.setValue(text);
      } else if (old !== text) {
        // 変わった部分だけを置き換える (元に戻す操作の履歴とカーソルの位置を保つ)
        let start = 0;
        const max = Math.min(old.length, text.length);
        while (start < max && old.charCodeAt(start) === text.charCodeAt(start)) {
          start++;
        }

        let endOld = old.length;
        let endNew = text.length;
        while (endOld > start && endNew > start && old.charCodeAt(endOld - 1) === text.charCodeAt(endNew - 1)) {
          endOld--;
          endNew--;
        }

        const range = monaco.Range.fromPositions(model.getPositionAt(start), model.getPositionAt(endOld));
        model.pushEditOperations(editor.getSelections(), [{ range, text: text.substring(start, endNew) }], () => null);
      }
    } finally {
      suppress--;
    }

    postSelection();
  }

  function replace(start, length, text) {
    suppress++;
    try {
      const range = monaco.Range.fromPositions(model.getPositionAt(start), model.getPositionAt(start + length));
      editor.executeEdits('hexeditor', [{ range, text: String(text || ''), forceMoveMarkers: true }]);
      const p = model.getPositionAt(start + String(text || '').length);
      editor.setPosition(p);
    } finally {
      suppress--;
    }
  }

  function setCaret(start, end) {
    const a = model.getPositionAt(start);
    const b = model.getPositionAt(end == null ? start : end);
    editor.setSelection(monaco.Selection.fromPositions(a, b));
    editor.revealPositionInCenterIfOutsideViewport(b);
  }

  function goTo(line, column, focus) {
    const p = model.validatePosition({ lineNumber: line, column });
    editor.setPosition(p);
    editor.revealPositionInCenterIfOutsideViewport(p);
    if (focus) {
      editor.focus();
    }
  }

  function postSelection() {
    const s = editor.getSelection();
    if (!s) {
      return;
    }

    const caret = s.getPosition();
    const pos = editor.getScrolledVisiblePosition(caret) || { top: 0, left: 0, height: 0 };
    post({
      t: 'sel',
      a: model.getOffsetAt(s.getStartPosition()),
      b: model.getOffsetAt(s.getEndPosition()),
      c: model.getOffsetAt(caret),
      x: pos.left,
      y: pos.top,
      h: pos.height,
    });
  }

  // ───── 余白の印 (ブレークポイント・現在の行)・エラーの印 ─────

  function setBreakpoints(items) {
    // items: [行, 有効か, 条件付きか, 説明]
    breakpointIds = editor.deltaDecorations(breakpointIds, items.map((b) => ({
      range: new monaco.Range(b[0], 1, b[0], 1),
      options: {
        glyphMarginClassName: (b[2] ? 'hex-bp-cond' : 'hex-bp') + (b[1] ? '' : '-disabled'),
        glyphMarginHoverMessage: b[3] ? { value: b[3] } : undefined,
        stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
      },
    })));
  }

  function setCurrentLine(line) {
    currentLine = line;
    currentIds = editor.deltaDecorations(currentIds, line == null ? [] : [{
      range: new monaco.Range(line, 1, line, 1),
      options: { isWholeLine: true, className: 'hex-current-line', glyphMarginClassName: 'hex-current-arrow' },
    }]);
    if (line != null) {
      editor.revealLineInCenterIfOutsideViewport(line);
    }
  }

  function setMarkers(items) {
    // items: [行, 列, 長さ, エラーか, 理由]
    markers = items;
    monaco.editor.setModelMarkers(model, 'hexeditor', items.map((m) => ({
      startLineNumber: m[0],
      startColumn: m[1],
      endLineNumber: m[0],
      endColumn: m[1] + Math.max(1, m[2]),
      severity: m[3] ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
      message: m[4] || '',
    })));
    // 行の印 (余白の小さな印。ブレークポイントのない行だけ)
    markerIds = editor.deltaDecorations(markerIds, items.map((m) => ({
      range: new monaco.Range(m[0], 1, m[0], 1),
      options: {
        overviewRuler: { color: m[3] ? (cfg.theme && cfg.theme.error) || '#e51400' : (cfg.theme && cfg.theme.warning) || '#bf8803', position: monaco.editor.OverviewRulerLane.Left },
        linesDecorationsClassName: m[3] ? 'hex-line-error' : 'hex-line-warning',
      },
    })));
  }

  function postEditorMarkers() {
    // Monaco の言語サービス (JavaScript の型のチェックなど) が付けた印をアプリの問題の一覧に渡す
    const list = monaco.editor.getModelMarkers({ resource: model.uri })
      .filter((m) => m.owner !== 'hexeditor')
      .map((m) => [m.startLineNumber, m.startColumn, Math.max(1, (m.endLineNumber === m.startLineNumber ? m.endColumn - m.startColumn : 1)),
        m.severity >= monaco.MarkerSeverity.Error, typeof m.message === 'string' ? m.message : String(m.message && m.message.value || ''), m.code && typeof m.code === 'object' ? String(m.code.value) : String(m.code || '')]);
    post({ t: 'emarkers', items: list });
  }

  // ───── テーマ ─────

  function applyTheme(theme) {
    if (!theme) {
      return;
    }

    if (cfg) {
      cfg.theme = theme;
    }

    monaco.editor.defineTheme('hex', { base: theme.base, inherit: true, rules: theme.rules || [], colors: theme.colors || {} });
    monaco.editor.setTheme('hex');
    const root = document.documentElement.style;
    root.setProperty('--hex-background', theme.colors['editor.background'] || 'transparent');
    root.setProperty('--hex-bp', theme.breakpoint || '#e51400');
    root.setProperty('--hex-bp-text', theme.breakpointText || '#ffffff');
    root.setProperty('--hex-current', theme.current || '#0078d4');
    root.setProperty('--hex-current-line', theme.currentLine || 'rgba(255,238,0,0.25)');
    root.setProperty('--hex-error', theme.error || '#e51400');
    root.setProperty('--hex-warning', theme.warning || '#bf8803');
  }

  // ───── キー: アプリが処理するキーはアプリに渡す (AUTO-20 の仕様 5) ─────

  function applyForward(items) {
    forward = new Set(items);
  }

  function onKeyDown(e) {
    if (e.isComposing || e.keyCode === 229) {
      return; // IME の変換中 (UI-20 の仕様 5)
    }

    const vk = e.keyCode;
    if (vk === 16 || vk === 17 || vk === 18 || vk === 91 || vk === 92) {
      return;
    }

    const mods = (e.ctrlKey ? 1 : 0) | (e.shiftKey ? 2 : 0) | (e.altKey ? 4 : 0);
    // AltGr (Ctrl+Alt) で入力される文字はショートカットより優先する (仕様 5 の 2、09 の UI-20)
    if (!chordPending && e.ctrlKey && e.altKey && typeof e.key === 'string' && e.key.length === 1) {
      return;
    }

    if (chordPending || forward.has(vk + ':' + mods)) {
      e.preventDefault();
      e.stopPropagation();
      e.stopImmediatePropagation();
      chordPending = false;
      post({ t: 'key', vk, m: mods });
    }
  }

  // ───── 編集の操作 (アプリのコマンドから) ─────

  function runAction(id) {
    const map = {
      format: 'editor.action.formatDocument',
      comment: 'editor.action.commentLine',
      goToLine: 'editor.action.gotoLine',
      complete: 'editor.action.triggerSuggest',
      find: 'actions.find',
      replace: 'editor.action.startFindReplaceAction',
      undo: 'undo',
      redo: 'redo',
    };
    const name = map[id] || id;
    editor.focus();
    if (name === 'undo' || name === 'redo') {
      editor.trigger('hexeditor', name, null);
      return;
    }

    const action = editor.getAction(name);
    if (action) {
      action.run();
    } else {
      editor.trigger('hexeditor', name, {});
    }
  }

  // ───── アプリに尋ねる機能 (テンプレート言語・C# の補完、テンプレートの整形) ─────

  function request(message) {
    return new Promise((resolve) => {
      const id = nextRequest++;
      pending.set(id, resolve);
      message.id = id;
      post(message);
      setTimeout(() => {
        if (pending.has(id)) {
          pending.delete(id);
          resolve(null);
        }
      }, 5000);
    });
  }

  const kinds = () => ({
    keyword: monaco.languages.CompletionItemKind.Keyword,
    type: monaco.languages.CompletionItemKind.Class,
    builtinType: monaco.languages.CompletionItemKind.TypeParameter,
    function: monaco.languages.CompletionItemKind.Function,
    method: monaco.languages.CompletionItemKind.Method,
    member: monaco.languages.CompletionItemKind.Field,
    field: monaco.languages.CompletionItemKind.Field,
    property: monaco.languages.CompletionItemKind.Property,
    pattern: monaco.languages.CompletionItemKind.Variable,
    variable: monaco.languages.CompletionItemKind.Variable,
    attribute: monaco.languages.CompletionItemKind.Property,
    pragma: monaco.languages.CompletionItemKind.Snippet,
    constant: monaco.languages.CompletionItemKind.Constant,
    namespace: monaco.languages.CompletionItemKind.Module,
    event: monaco.languages.CompletionItemKind.Event,
    text: monaco.languages.CompletionItemKind.Text,
  });

  function registerHostCompletion(language, triggers) {
    monaco.languages.registerCompletionItemProvider(language, {
      triggerCharacters: triggers,
      provideCompletionItems: async (m, position, context) => {
        const offset = m.getOffsetAt(position);
        const reply = await request({ t: 'complete', o: offset, trigger: context.triggerKind === monaco.languages.CompletionTriggerKind.TriggerCharacter ? context.triggerCharacter || '' : '' });
        if (!reply || !reply.items || m.isDisposed()) {
          return { suggestions: [] };
        }

        const k = kinds();
        const range = monaco.Range.fromPositions(m.getPositionAt(reply.start), m.getPositionAt(reply.start + reply.length));
        return {
          // C# の候補は入力中の名前で絞ってあるので、入力が進むたびに尋ね直す
          incomplete: language === 'csharp',
          suggestions: reply.items.map((it, i) => ({
            label: it.label,
            kind: k[it.kind] || monaco.languages.CompletionItemKind.Text,
            detail: it.detail || undefined,
            documentation: it.doc || undefined,
            insertText: it.insert || it.label,
            range,
            sortText: String(i).padStart(5, '0'),
          })),
        };
      },
    });
  }

  function registerHostFormat(language) {
    monaco.languages.registerDocumentFormattingEditProvider(language, {
      provideDocumentFormattingEdits: async (m) => {
        const before = m.getValue();
        const reply = await request({ t: 'format' });
        if (!reply || typeof reply.text !== 'string' || m.isDisposed() || m.getValue() !== before || reply.text === before) {
          return [];
        }

        const text = reply.text;
        let start = 0;
        const max = Math.min(before.length, text.length);
        while (start < max && before.charCodeAt(start) === text.charCodeAt(start)) {
          start++;
        }

        let endOld = before.length;
        let endNew = text.length;
        while (endOld > start && endNew > start && before.charCodeAt(endOld - 1) === text.charCodeAt(endNew - 1)) {
          endOld--;
          endNew--;
        }

        return [{ range: monaco.Range.fromPositions(m.getPositionAt(start), m.getPositionAt(endOld)), text: text.substring(start, endNew) }];
      },
    });
  }

  // ───── JavaScript: hexeditor.d.ts を読み込ませる (AUTO-20 の仕様 3) ─────

  function configureJavaScript(ts) {
    const defaults = ts.javascriptDefaults;
    defaults.setCompilerOptions({
      target: ts.ScriptTarget.ES2022,
      module: ts.ModuleKind.ESNext,
      moduleResolution: ts.ModuleResolutionKind.NodeJs,
      // スクリプトはモジュールとして動く (トップレベルの const doc がグローバルの doc とぶつからない)
      moduleDetection: 3,
      allowJs: true,
      checkJs: true,
      allowNonTsExtensions: true,
      noLib: false,
      lib: ['es2023'],
      strict: false,
      noImplicitAny: false,
      esModuleInterop: true,
    });
    // 構文のエラーはアプリのチェック (Acornima。スクリプトの実行と同じ解析) が出す。Monaco は型のチェックだけ
    defaults.setDiagnosticsOptions({ noSemanticValidation: false, noSyntaxValidation: true, diagnosticCodesToIgnore: [1375, 1378, 80001, 80004, 7044, 7043] });
    defaults.setEagerModelSync(true);
    for (const lib of cfg.extraLibs || []) {
      defaults.addExtraLib(lib.content, lib.path);
    }
  }

  // ───── テンプレート言語 (07 の TPL-31): Monarch の色分けと言語の設定 ─────

  function registerTemplateLanguage(lists) {
    monaco.languages.register({ id: 'hxt', extensions: ['.hxt'], aliases: ['HexEditor template', 'hxt'] });
    monaco.languages.setLanguageConfiguration('hxt', {
      comments: { lineComment: '//', blockComment: ['/*', '*/'] },
      brackets: [['{', '}'], ['[', ']'], ['(', ')']],
      autoClosingPairs: [
        { open: '{', close: '}' }, { open: '[', close: ']' }, { open: '(', close: ')' },
        { open: '"', close: '"', notIn: ['string', 'comment'] }, { open: "'", close: "'", notIn: ['string', 'comment'] },
        { open: '/*', close: ' */', notIn: ['string'] },
      ],
      surroundingPairs: [{ open: '{', close: '}' }, { open: '[', close: ']' }, { open: '(', close: ')' }, { open: '"', close: '"' }, { open: "'", close: "'" }],
      folding: { markers: { start: /^\s*\/\/\s*#?region\b/, end: /^\s*\/\/\s*#?endregion\b/ } },
      indentationRules: { increaseIndentPattern: /^.*\{[^}"']*$/, decreaseIndentPattern: /^\s*\}/ },
      wordPattern: /(-?\d*\.\d\w*)|([^`~!@#%^&*()\-=+[{\]}\\|;:'",.<>/?\s]+)/g,
    });
    monaco.languages.setMonarchTokensProvider('hxt', {
      defaultToken: '',
      tokenPostfix: '.hxt',
      keywords: lists.keywords || [],
      types: lists.types || [],
      attributes: lists.attributes || [],
      brackets: [
        { open: '{', close: '}', token: 'delimiter.curly' },
        { open: '[', close: ']', token: 'delimiter.square' },
        { open: '(', close: ')', token: 'delimiter.parenthesis' },
      ],
      tokenizer: {
        root: [
          [/^\s*#.*$/, 'meta'],
          [/\[\[/, { token: 'delimiter.attribute', next: '@attribute' }],
          { include: '@common' },
        ],
        common: [
          { include: '@whitespace' },
          [/x"[^"]*"?/, 'string'],
          [/[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*(?=\s*\()/, { cases: { '@keywords': 'keyword', '@types': 'type', '@default': 'function' } }],
          [/[A-Za-z_]\w*(?:::[A-Za-z_]\w*)*/, { cases: { '@keywords': 'keyword', '@types': 'type', '@default': 'identifier' } }],
          [/[{}()[\]]/, '@brackets'],
          [/0[xX][0-9a-fA-F_]+\w*/, 'number.hex'],
          [/0[bB][01_]+\w*/, 'number.binary'],
          [/\d[\d_]*(?:\.\d+)?(?:[eE][+-]?\d+)?\w*/, 'number'],
          [/"/, 'string', '@stringDouble'],
          [/'/, 'string', '@stringSingle'],
          [/[;,.]/, 'delimiter'],
          [/[<>=!&|+\-*/%^~?:@$]+/, 'operator'],
        ],
        attribute: [
          [/\]\]/, { token: 'delimiter.attribute', next: '@pop' }],
          [/[A-Za-z_]\w*(?=\s*[(\],]|\s*$)/, { cases: { '@attributes': 'attribute', '@default': 'identifier' } }],
          { include: '@common' },
        ],
        whitespace: [
          [/[ \t\r\n]+/, ''],
          [/\/\*/, 'comment', '@comment'],
          [/\/\/.*$/, 'comment'],
        ],
        comment: [
          [/[^/*]+/, 'comment'],
          [/\*\//, 'comment', '@pop'],
          [/[/*]/, 'comment'],
        ],
        stringDouble: [
          [/[^\\"]+/, 'string'],
          [/\\./, 'string.escape'],
          [/"/, 'string', '@pop'],
        ],
        stringSingle: [
          [/[^\\']+/, 'string'],
          [/\\./, 'string.escape'],
          [/'/, 'string', '@pop'],
        ],
      },
    });
  }
})();
