# Monaco の min/vs から必要なファイルだけを選んで写す。引数: <min/vs のフォルダ> <出力の vs フォルダ> [--dry]
import os, re, sys, shutil, posixpath
src, dst = sys.argv[1], sys.argv[2]
dry = '--dry' in sys.argv
files = {}
for root, _, names in os.walk(src):
    for n in names:
        rel = os.path.relpath(os.path.join(root, n), src).replace('\\', '/')
        files[rel] = os.path.join(root, n)

contrib = open(files['basic-languages/monaco.contribution.js'], encoding='utf-8').read()
lang_chunks = dict(re.findall(r'id:"([^"]+)"[^}]*?loader:\(\)=>new Promise\(\(e,s\)=>a\(\["\.\./([^"]+)"\]', contrib))
KEEP_LANGS = {'javascript', 'typescript', 'csharp'}
drop_lazy = {c for l, c in lang_chunks.items() if l not in KEEP_LANGS}
drop_lazy |= {os.path.splitext(f)[0] for f in files if re.match(r'(cssMode|htmlMode|jsonMode)-', f)}

def mod_of(rel):
    return 'vs/' + rel[:-3]

def resolve(base_mod, dep):
    if dep.startswith('./') or dep.startswith('../'):
        return posixpath.normpath(posixpath.join(posixpath.dirname(base_mod), dep))
    return dep

keep = set()
queue = []
def add(rel):
    # CSS・HTML・JSON の worker の本体は使わない (editor.main が参照する小さな包みのチャンクは残す)
    if re.match(r'assets/(css|html|json)\.worker-', rel):
        return
    if rel in files and rel not in keep:
        keep.add(rel)
        queue.append(rel)

for r in ['loader.js', 'editor/editor.main.js', 'editor/editor.main.css', 'nls.messages-loader.js']:
    add(r)
for f in files:
    if re.match(r'nls/lang/[^/]+\.js$', f):
        add(f)
    if re.match(r'assets/(editor\.worker|editorWebWorkerMain|ts\.worker)-', f):
        add(f)
for l in KEEP_LANGS:
    add(lang_chunks[l] + '.js')
for f in files:
    if re.match(r'tsMode-', f):
        add(f)

while queue:
    rel = queue.pop()
    if not rel.endswith('.js'):
        continue
    text = open(files[rel], encoding='utf-8').read()
    m = mod_of(rel)
    d = re.match(r'\s*define\("[^"]+",\[([^\]]*)\]', text)
    deps = re.findall(r'"([^"]+)"', d.group(1)) if d else []
    lazy = re.findall(r'\(\["(\.\.?/[^"]+)"\]', text)
    for dep in deps + lazy:
        dep = dep.rstrip('!')
        r = resolve(m, dep)
        if not r.startswith('vs/'):
            continue
        if r[3:] in drop_lazy:
            continue
        add(r[3:] + '.js')
    for u in re.findall(r'toUrl\("([^"]+)"\)', text):
        r = resolve(m, u)
        if r.startswith('vs/'):
            add(r[3:])

total = sum(os.path.getsize(files[k]) for k in keep)
dropped = sorted(set(files) - keep)
print('languages', len(lang_chunks), 'keep', len(keep), 'files', round(total / 1048576, 2), 'MB; drop', len(dropped))
for k in sorted(keep):
    print('  +', k, os.path.getsize(files[k]))
print('dropped (non-language):', [x for x in dropped if os.path.splitext(x)[0] not in drop_lazy])
if not dry:
    for k in sorted(keep):
        out = os.path.join(dst, k)
        os.makedirs(os.path.dirname(out), exist_ok=True)
        shutil.copyfile(files[k], out)
