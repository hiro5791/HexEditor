// スクリプトの実行環境の前置き (AUTO-02、AUTO-04 の仕様 9、AUTO-05)。スクリプトより先に 1 回だけ評価する。
// host はアプリが渡す関数の集まり。戻り値は実行環境が使う補助の関数 (スクリプトからは見えない)。
(function (host) {
    'use strict';

    // ---- 例外の型 (AUTO-04 の仕様 9。RangeError は ECMAScript の組み込みのものを使う) ----
    function defineError(name) {
        const E = class extends Error {
            constructor(message, options) {
                super(message, options);
                Object.defineProperty(this, 'name', { value: name, writable: true, configurable: true });
            }
        };
        Object.defineProperty(E, 'name', { value: name });
        Object.defineProperty(globalThis, name, { value: E, writable: false, enumerable: false, configurable: false });
        return E;
    }

    const errors = {
        ArgumentError: defineError('ArgumentError'),
        ReadOnlyError: defineError('ReadOnlyError'),
        IoError: defineError('IoError'),
        PermissionError: defineError('PermissionError'),
        CanceledError: defineError('CanceledError'),
        LimitError: defineError('LimitError'),
        RangeError: RangeError,
    };

    // ---- 値の文字列化 (console と出力パネル) ----
    const hex = (n, w) => n.toString(16).toUpperCase().padStart(w, '0');

    function inspect(value, depth, seen) {
        if (value === null) return 'null';
        if (value === undefined) return 'undefined';
        switch (typeof value) {
            case 'string': return depth === 0 ? value : JSON.stringify(value);
            case 'number': case 'boolean': return String(value);
            case 'bigint': return value + 'n (0x' + (value < 0n ? '-' + (-value).toString(16) : value.toString(16)).toUpperCase() + ')';
            case 'symbol': return value.toString();
            case 'function': return '[Function' + (value.name ? ': ' + value.name : '') + ']';
        }
        if (value instanceof Error) return value.name + ': ' + value.message;
        if (value instanceof Uint8Array) {
            const n = Math.min(value.length, 256);
            let s = 'Uint8Array(' + value.length + ') [';
            for (let i = 0; i < n; i++) s += (i ? ' ' : '') + hex(value[i], 2);
            return s + (value.length > n ? ' …' : '') + ']';
        }
        seen = seen || [];
        if (seen.indexOf(value) >= 0) return '[Circular]';
        if (depth > 3) return Array.isArray(value) ? '[Array]' : '[Object]';
        seen.push(value);
        let out;
        if (Array.isArray(value)) {
            const items = value.slice(0, 100).map(v => inspect(v, depth + 1, seen));
            if (value.length > 100) items.push('… ' + (value.length - 100) + ' more');
            out = '[' + items.join(', ') + ']';
        } else {
            const keys = Object.keys(value);
            out = '{ ' + keys.slice(0, 100).map(k => k + ': ' + inspect(value[k], depth + 1, seen)).join(', ') + (keys.length > 100 ? ', …' : '') + ' }';
            if (keys.length === 0) out = '{}';
        }
        seen.pop();
        return out;
    }

    function format(args) {
        return Array.prototype.map.call(args, (a, i) => inspect(a, 0)).join(' ');
    }

    function table(rows, columns) {
        if (rows === null || typeof rows !== 'object') return inspect(rows, 0);
        const list = Array.isArray(rows) ? rows : Object.keys(rows).map(k => Object.assign({ '(index)': k }, rows[k]));
        const cols = columns || Array.from(new Set(list.flatMap(r => r !== null && typeof r === 'object' ? Object.keys(r) : ['Values'])));
        const cell = v => v === undefined ? '' : (typeof v === 'string' ? v : inspect(v, 1));
        const header = ['(index)'].concat(cols.filter(c => c !== '(index)'));
        const body = list.map((r, i) => [String(i)].concat(header.slice(1).map(c => cell(r !== null && typeof r === 'object' ? r[c] : (c === 'Values' ? r : undefined)))));
        const widths = header.map((h, i) => Math.max(h.length, ...body.map(b => b[i].length)));
        const line = cells => cells.map((c, i) => c.padEnd(widths[i])).join(' | ');
        return [line(header), widths.map(w => '-'.repeat(w)).join('-+-')].concat(body.map(line)).join('\n');
    }

    const console = {
        log: function () { host.output('info', format(arguments)); },
        info: function () { host.output('info', format(arguments)); },
        debug: function () { host.output('info', format(arguments)); },
        warn: function () { host.output('warning', format(arguments)); },
        error: function () { host.output('error', format(arguments)); },
        table: function (rows, columns) { host.output('info', table(rows, columns)); },
    };
    Object.defineProperty(globalThis, 'console', { value: console, writable: true, enumerable: false, configurable: true });

    // ---- AbortController / AbortSignal (AUTO-05 の仕様 3) ----
    class AbortController {
        constructor() {
            const signal = host.createSignal();
            Object.defineProperty(this, 'signal', { value: signal, enumerable: true });
        }
        abort(reason) { host.abortSignal(this.signal, reason === undefined ? 'AbortError' : String(reason)); }
    }
    Object.defineProperty(globalThis, 'AbortController', { value: AbortController, writable: true, enumerable: false, configurable: true });

    // ---- 配列のように列挙できるオブジェクト (ブックマークなど) ----
    function makeIterable(target, length, at) {
        Object.defineProperty(target, 'length', { get: length, enumerable: false });
        Object.defineProperty(target, Symbol.iterator, {
            value: function* () { const n = length(); for (let i = 0; i < n; i++) yield at(i); },
            enumerable: false,
        });
        Object.defineProperty(target, 'at', { value: i => { const n = length(); if (i < 0) i += n; return i >= 0 && i < n ? at(i) : undefined; }, enumerable: false });
        Object.defineProperty(target, 'toArray', { value: () => Array.from(target), enumerable: false });
        Object.defineProperty(target, 'map', { value: f => Array.from(target).map(f), enumerable: false });
        Object.defineProperty(target, 'filter', { value: f => Array.from(target).filter(f), enumerable: false });
        Object.defineProperty(target, 'forEach', { value: f => Array.from(target).forEach(f), enumerable: false });
        return target;
    }

    // ---- 非同期イテレータ (doc.chunks。AUTO-09) ----
    function makeAsyncIterable(next, close) {
        const iterator = {
            next: () => next(),
            return: value => { close(); return Promise.resolve({ value: value, done: true }); },
            [Symbol.asyncIterator]() { return this; },
        };
        return iterator;
    }

    return { errors: errors, isIterable: v => v !== null && v !== undefined && typeof v[Symbol.iterator] === 'function', inspect: v => inspect(v, 0), makeIterable: makeIterable, makeAsyncIterable: makeAsyncIterable };
})
