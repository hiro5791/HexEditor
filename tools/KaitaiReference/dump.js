// TC-TPL-33-01 の期待値 (参照の解析結果) を作る。CI のジョブ (kaitai-reference) だけで動かす。
//
// 使い方: node dump.js <kaitai_struct_formats のフォルダ> <データのフォルダ> <出力先> <コミット ID>
//   formats.json の各 .ksy を、Kaitai Struct の公式のコンパイラ (npm の kaitai-struct-compiler。Web IDE と同じ JavaScript 版) で
//   デバッグ付き (位置を記録する) の JavaScript に変換し、公式の実行時ライブラリ (npm の kaitai-struct) で <データ>/<TD の ID>.bin を
//   解析して、<出力先>/<key>.json に書く。形式は tests/HexEditor.Core.Tests/Templates/KaitaiReferenceComparer.cs の説明のとおり。
//   変換・解析の失敗も JSON に書き (compileError / error)、判定はテスト側で行う。引数の誤りだけ終了コード 2。
'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const Module = require('module');

const [formatsDir, dataDir, outDir, commit] = process.argv.slice(2);
if (!formatsDir || !dataDir || !outDir) {
  console.error('usage: node dump.js <formatsDir> <dataDir> <outDir> [commit]');
  process.exit(2);
}

// 生成したコードの require('kaitai-struct/...') を、このフォルダの node_modules から解決させる
const nodeModules = path.join(__dirname, 'node_modules');
process.env.NODE_PATH = [nodeModules, process.env.NODE_PATH || ''].filter((p) => p.length > 0).join(path.delimiter);
Module._initPaths();

const yaml = require('js-yaml');
let Ksc = require('kaitai-struct-compiler');
if (Ksc && Ksc.default) {
  Ksc = Ksc.default;
}
const compiler = typeof Ksc.compile === 'function' ? Ksc : new Ksc();

function loadRuntime() {
  for (const name of ['kaitai-struct/KaitaiStream', 'kaitai-struct']) {
    try {
      const m = require(name);
      const k = m.KaitaiStream || m.default || m;
      if (typeof k === 'function') {
        return k;
      }
    } catch (e) {
      // 次の候補
    }
  }
  throw new Error('kaitai-struct runtime not found');
}

const KaitaiStream = loadRuntime();

function packageVersion(name) {
  return JSON.parse(fs.readFileSync(path.join(nodeModules, name, 'package.json'), 'utf8')).version;
}

// Kaitai Struct の Utils.upperCamelCase (クラスの名前)
function upperCamel(id) {
  return id.split('_').filter((p) => p.length > 0).map((p) => p[0].toUpperCase() + p.slice(1)).join('');
}

function loadYaml(file) {
  return yaml.load(fs.readFileSync(file, 'utf8'));
}

function importerFor(ksyRel) {
  return {
    importYaml(name, mode) {
      const rel = mode === 'abs'
        ? name.replace(/^\/+/, '')
        : path.posix.join(path.posix.dirname(ksyRel), name);
      return Promise.resolve(loadYaml(path.join(formatsDir, ...rel.split('/')) + '.ksy'));
    },
  };
}

function hex(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i++) {
    s += (bytes[i] < 16 ? '0' : '') + bytes[i].toString(16).toUpperCase();
  }
  return s;
}

function streamOffset(io) {
  if (!io) {
    return 0;
  }
  if (typeof io.byteOffset === 'number') {
    return io.byteOffset;
  }
  return typeof io._byteOffset === 'number' ? io._byteOffset : 0;
}

// _debug の 1 項目 ({ start, ioOffset, end }) から、データの先頭からの位置と大きさ
function position(dbg, io) {
  const r = {};
  if (dbg && typeof dbg.start === 'number') {
    const base = typeof dbg.ioOffset === 'number' ? dbg.ioOffset : streamOffset(io);
    r.offset = base + dbg.start;
    if (typeof dbg.end === 'number') {
      r.size = dbg.end - dbg.start;
    }
  }
  return r;
}

function errorText(e) {
  return String((e && e.message) || e);
}

function isStruct(v) {
  return v !== null && typeof v === 'object' && v._io !== undefined && !ArrayBuffer.isView(v) && !Array.isArray(v);
}

// 値の種類ごとの表現。dbg はその値の _debug の項目 (配列の要素は arr[i])、io は値を持つ構造体のストリーム
function describe(value, dbg, io, depth, seen) {
  const node = position(dbg, io);
  if (dbg && typeof dbg.enumName === 'string') {
    node.enum = dbg.enumName;
  }
  if (value === undefined || value === null) {
    node.kind = 'none';
  } else if (ArrayBuffer.isView(value)) {
    node.kind = 'bytes';
    node.value = hex(new Uint8Array(value.buffer, value.byteOffset, value.byteLength));
  } else if (Array.isArray(value)) {
    node.kind = 'array';
    const arr = dbg && Array.isArray(dbg.arr) ? dbg.arr : [];
    node.items = value.map((v, i) => describe(v, arr[i], io, depth + 1, seen));
  } else if (typeof value === 'bigint') {
    node.kind = 'number';
    node.value = value.toString();
  } else if (typeof value === 'number') {
    node.kind = 'number';
    if (Number.isNaN(value)) {
      node.value = 'NaN';
    } else if (!Number.isFinite(value)) {
      node.value = value > 0 ? 'Inf' : '-Inf';
    } else {
      node.value = value;
      if (Number.isInteger(value) && !Number.isSafeInteger(value)) {
        node.unsafe = true;
      }
    }
  } else if (typeof value === 'boolean') {
    node.kind = 'bool';
    node.value = value;
  } else if (typeof value === 'string') {
    node.kind = 'string';
    node.value = value;
  } else if (isStruct(value)) {
    node.kind = 'struct';
    if (seen.has(value) || depth > 64) {
      node.kind = 'ref';
    } else {
      seen.add(value);
      node.fields = structFields(value, depth, seen);
      seen.delete(value);
    }
  } else {
    node.kind = 'unknown';
  }
  return node;
}

// 構造体の seq (_debug の順) と instances (プロトタイプのゲッター。すべて評価する)
function structFields(obj, depth, seen) {
  const fields = [];
  const dbgAll = obj._debug || {};
  for (const key of Object.keys(dbgAll)) {
    if (key.startsWith('_m_') || key.startsWith('_raw_')) {
      continue;
    }
    let value;
    let err = null;
    try {
      value = obj[key];
    } catch (e) {
      err = errorText(e);
    }
    const f = { name: key, ...describe(value, dbgAll[key], obj._io, depth + 1, seen) };
    if (err !== null) {
      f.error = err;
    } else if (dbgAll[key] && typeof dbgAll[key].start === 'number' && typeof dbgAll[key].end !== 'number') {
      // 読み始めて終わらなかった項目 (解析の途中の例外)
      f.error = 'incomplete';
    }
    fields.push(f);
  }
  const proto = Object.getPrototypeOf(obj);
  for (const name of Object.getOwnPropertyNames(proto)) {
    const desc = Object.getOwnPropertyDescriptor(proto, name);
    if (name === 'constructor' || !desc || typeof desc.get !== 'function') {
      continue;
    }
    let value;
    let err = null;
    try {
      value = obj[name];
    } catch (e) {
      err = errorText(e);
    }
    const dbg = dbgAll['_m_' + name] || dbgAll[name];
    const f = { name, instance: true, ...describe(value, dbg, obj._io, depth + 1, seen) };
    if (err !== null) {
      f.error = err;
    }
    fields.push(f);
  }
  return fields;
}

async function dumpOne(format, toolchain) {
  const ksyFile = path.join(formatsDir, ...format.ksy.split('/'));
  const dataFile = path.join(dataDir, format.data + '.bin');
  const data = fs.readFileSync(dataFile);
  const result = {
    key: format.key,
    ksy: format.ksy,
    data: format.data,
    sha256: crypto.createHash('sha256').update(data).digest('hex'),
    ksySha256: crypto.createHash('sha256').update(fs.readFileSync(ksyFile)).digest('hex'),
    toolchain,
    compileError: null,
    error: null,
    root: null,
  };

  let Cls;
  try {
    const spec = loadYaml(ksyFile);
    const files = await compiler.compile('javascript', spec, importerFor(format.ksy), true);
    const genDir = path.join(outDir, 'gen', format.key);
    fs.mkdirSync(genDir, { recursive: true });
    for (const name of Object.keys(files)) {
      fs.writeFileSync(path.join(genDir, name), files[name]);
    }
    const className = upperCamel(spec.meta.id);
    const m = require(path.join(genDir, className + '.js'));
    Cls = typeof m === 'function' ? m : m[className];
    if (typeof Cls !== 'function') {
      throw new Error('class ' + className + ' not exported by the generated module');
    }
  } catch (e) {
    result.compileError = errorText(e);
    return result;
  }

  // 新しい ArrayBuffer に写す (Buffer のプールの途中から始まると byteOffset がずれるため)
  const ab = data.buffer.slice(data.byteOffset, data.byteOffset + data.length);
  const io = new KaitaiStream(ab, 0);
  let obj = null;
  try {
    obj = new Cls(io);
    // デバッグ付きのコードはコンストラクタで読まない。読んだ版 (_debug がある) なら呼ばない
    if (!obj._debug || Object.keys(obj._debug).length === 0) {
      obj._read();
    }
  } catch (e) {
    result.error = errorText(e);
  }
  if (obj !== null) {
    result.root = { kind: 'struct', offset: 0, size: data.length, fields: structFields(obj, 0, new Set()) };
  }
  return result;
}

async function main() {
  const formats = JSON.parse(fs.readFileSync(path.join(__dirname, 'formats.json'), 'utf8'));
  const toolchain = {
    compiler: 'kaitai-struct-compiler ' + packageVersion('kaitai-struct-compiler'),
    runtime: 'kaitai-struct ' + packageVersion('kaitai-struct'),
    yaml: 'js-yaml ' + packageVersion('js-yaml'),
    formatsCommit: commit || null,
    node: process.version,
  };
  fs.mkdirSync(outDir, { recursive: true });
  for (const format of formats) {
    let r;
    try {
      r = await dumpOne(format, toolchain);
    } catch (e) {
      // dump.js 自身の誤り (データがないなど) も、その形式の JSON に書いて次の形式に進む (判定はテスト側)
      r = { key: format.key, ksy: format.ksy, data: format.data, toolchain, compileError: 'dump.js: ' + errorText(e), error: null, root: null };
    }
    fs.writeFileSync(path.join(outDir, format.key + '.json'), JSON.stringify(r, null, 1));
    const state = r.compileError ? 'compile error: ' + r.compileError : r.error ? 'parse error: ' + r.error : 'ok';
    console.log(`${format.key}: ${state}`);
  }
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
