# Template error codes / テンプレートのエラーのコード

Every message in the Template output panel has a code such as `HXT1004`. Clicking the code opens its section on this page.

テンプレートの出力パネルのメッセージには `HXT1004` のようなコードが付いています。コードを押すと、このページの該当する節を開きます。

`{0}` and similar placeholders are replaced with the actual names and values. / `{0}` などには実際の名前や値が入ります。

## Loading errors (syntax, imports, pragmas) / 読み込みのエラー (構文・インポート・プラグマ)

The template could not be read. Open it in the template editor: the line with the problem is underlined. Fix the text and save, or use Try run to check it without saving.

テンプレートを読み込めませんでした。テンプレートエディタで開くと、問題のある行に波線が付きます。直して保存するか、「試しに実行」で保存せずに確かめてください。

### HXT1001

- English: Unexpected character '{0}'
- 日本語: 予期しない文字 `{0}`

### HXT1002

- English: '{0}' expected
- 日本語: `{0}` がありません

### HXT1003

- English: Unexpected '{0}'
- 日本語: 予期しない `{0}`

### HXT1004

- English: Invalid number '{0}'
- 日本語: 不正な数値 `{0}`

### HXT1005

- English: String literal is not terminated
- 日本語: 文字列が閉じていません

### HXT1006

- English: Invalid escape sequence '{0}'
- 日本語: 不正なエスケープ `{0}`

### HXT1007

- English: Comment is not terminated
- 日本語: コメントが閉じていません

### HXT1008

- English: Nesting is too deep (limit {0})
- 日本語: 入れ子が深すぎます (上限 {0})

### HXT1009

- English: Invalid byte string '{0}'
- 日本語: 不正なバイト列 `{0}`

### HXT1010

- English: The template is not valid UTF-8
- 日本語: テンプレートが UTF-8 として不正です

### HXT1011

- English: The template is too large (limit 4 MB)
- 日本語: テンプレートが大きすぎます (上限 4 MB)

### HXT1012

- English: Too many errors; only the first {0} are shown
- 日本語: エラーが多すぎるため、最初の {0} 件だけを表示しています

### HXT1013

- English: Invalid value '{1}' for pragma '{0}'
- 日本語: プラグマ `{0}` の値 `{1}` が不正です

### HXT1014

- English: Unknown pragma '{0}' was ignored
- 日本語: 不明なプラグマ `{0}` を無視しました

### HXT1015

- English: '{0}' is already defined at {1}
- 日本語: `{0}` は {1} で定義済みです

### HXT1016

- English: '{0}' was not found (searched: {1})
- 日本語: `{0}` が見つかりません (検索したフォルダ: {1})

### HXT1017

- English: Templates cannot perform this operation: {0}
- 日本語: テンプレートはこの操作を行えません: {0}

### HXT1018

- English: This template requires app version {0} or later
- 日本語: このテンプレートには版 {0} 以降のアプリが必要です

### HXT1019

- English: Unknown attribute '{0}' was ignored
- 日本語: 不明な属性 `{0}` を無視しました

### HXT1020

- English: {0} does not fit in {1}
- 日本語: {0} は {1} に収まりません

### HXT1021

- English: Value {0} has more than one name ('{1}' is used)
- 日本語: 値 {0} に複数の名前があります (`{1}` を使います)

### HXT1022

- English: {0} {1} exceeds the maximum; {2} is used
- 日本語: {0} の {1} は最大値を超えているため、{2} を使います

### HXT1023

- English: The templates including imports are too large (limit 32 MB)
- 日本語: インポートを含めたテンプレートが大きすぎます (上限 32 MB)

## Evaluation errors and warnings / 評価のエラーと警告

The template was read, but applying it to the data failed at this point. Double-click the message in the Template output panel and choose Go to source or Go to data to see the line and the bytes involved. The results up to this point are still shown.

テンプレートは読めましたが、データに適用する途中で失敗しました。テンプレートの出力パネルでメッセージをダブルクリックし、「ソースへ」「データへ」で該当する行とバイトを確かめてください。ここまでの結果は表示されたままです。

### HXT2001

- English: '{0}' is not defined
- 日本語: `{0}` は定義されていません

### HXT2002

- English: Division by zero
- 日本語: 0 で除算しました

### HXT2003

- English: Type mismatch: {0}
- 日本語: 型が一致しません: {0}

### HXT2004

- English: Recursion is too deep (limit {0})
- 日本語: 再帰が深すぎます (上限 {0})

### HXT2005

- English: The number of executed statements reached the limit ({0})
- 日本語: 文の実行回数が上限 ({0}) に達しました

### HXT2006

- English: The number of patterns reached the limit ({0})
- 日本語: パターン数が上限 ({0}) に達しました

### HXT2007

- English: The evaluation memory reached the limit ({0})
- 日本語: 評価のメモリが上限 ({0}) に達しました

### HXT2008

- English: The evaluation time reached the limit ({0} s)
- 日本語: 評価の時間が上限 ({0} 秒) に達しました

### HXT2009

- English: Placement 0x{0} is outside the data
- 日本語: 配置先 0x{0} はデータの範囲外です

### HXT2010

- English: {0}(0x{1}) is outside the data
- 日本語: {0}(0x{1}) はデータの範囲外です

### HXT2011

- English: '{0}' (0x{1}, {2} bytes) is outside the data
- 日本語: `{0}` (0x{1}、{2} バイト) はデータの範囲外です

### HXT2012

- English: The position cannot be negative ({0})
- 日本語: 位置に負の値は使えません ({0})

### HXT2013

- English: The array cannot end because the element size is 0
- 日本語: 要素のサイズが 0 のため配列を終了できません

### HXT2014

- English: The array ended because the end of the data was reached
- 日本語: データの末尾に達したため配列を終了しました

### HXT2015

- English: The array was truncated at the limit ({0} elements)
- 日本語: 配列を上限 ({0} 要素) で打ち切りました

### HXT2016

- English: The incomplete last element ({0} bytes) was not included
- 日本語: 末尾の途中までの要素 ({0} バイト) を含めませんでした

### HXT2017

- English: The string terminator was not found
- 日本語: 文字列の終端が見つかりません

### HXT2018

- English: LEB128 is longer than 10 bytes (offset 0x{0})
- 日本語: LEB128 が 10 バイトを超えています (オフセット 0x{0})

### HXT2019

- English: The content of the structure ({0} bytes) exceeds the specified size ({1} bytes)
- 日本語: 構造体の中身 ({0} バイト) が指定のサイズ ({1} バイト) を超えています

### HXT2020

- English: The pointer type must be an integer type
- 日本語: ポインタの型は整数型でなければなりません

### HXT2021

- English: Out of range (0x{0})
- 日本語: 範囲外 (0x{0})

### HXT2022

- English: Patterns cannot be declared inside functions
- 日本語: 関数の中ではパターンを宣言できません

### HXT2023

- English: Assertion failed: {0}
- 日本語: アサーションが失敗しました: {0}

### HXT2024

- English: {0}
- 日本語: {0}

### HXT2025

- English: {0}
- 日本語: {0}

### HXT2026

- English: Extension function '{0}' is not registered
- 日本語: 拡張関数 `{0}` が登録されていません

### HXT2027

- English: Extension function '{0}' failed: {1}
- 日本語: 拡張関数 `{0}` が失敗しました: {1}

### HXT2028

- English: Extension function '{0}' is not permitted to run
- 日本語: 拡張関数 `{0}` の実行が許可されていません

### HXT2029

- English: Index {0} is out of range (0 to {1})
- 日本語: 添字 {0} は範囲外です (0〜{1})

### HXT2030

- English: sizeof({0}) cannot be used because the size of the type is not fixed
- 日本語: 型のサイズが決まらないため sizeof({0}) は使えません

### HXT2031

- English: The bit field total ({0} bit) exceeds {1}
- 日本語: ビットフィールドの合計 ({0} bit) が {1} を超えています

### HXT2032

- English: A bit field cannot be wider than 64 bits ({0})
- 日本語: ビットフィールドは 64 bit を超えられません ({0})

### HXT2033

- English: {0} decompression failed: {1}
- 日本語: {0} の展開に失敗しました: {1}

### HXT2034

- English: Decompression stopped because the data reached the limit ({0})
- 日本語: 展開後のデータが上限 ({0}) に達したため打ち切りました

### HXT2035

- English: {0} is not supported in this version
- 日本語: {0} はこの版では使えません

### HXT2036

- English: The length ({0}) exceeds the limit of 256 MB per call
- 日本語: 長さ ({0}) が 1 回の呼び出しの上限 256 MB を超えています

### HXT2037

- English: Cannot assign to '{0}'
- 日本語: `{0}` には代入できません

### HXT2038

- English: {0}
- 日本語: {0}

### HXT2039

- English: The data at 0x{0} cannot be read
- 日本語: 0x{0} のデータを読めません

### HXT2040

- English: {0}
- 日本語: {0}

### HXT2041

- English: The evaluation was cancelled
- 日本語: 評価をキャンセルしました

### HXT2042

- English: {0} does not fit in {1}
- 日本語: {0} は {1} に収まりません

### HXT2043

- English: '{0}' is not a function
- 日本語: `{0}` は関数ではありません

### HXT2044

- English: '{0}' takes {1} argument(s)
- 日本語: `{0}` の引数は {1} 個です

### HXT2046

- English: Kaitai Struct feature '{0}' is not supported
- 日本語: Kaitai Struct の機能 `{0}` には対応していません

### HXT2047

- English: The contents do not match the expected bytes
- 日本語: 内容が期待するバイト列と一致しません

### HXT2048

- English: Validation failed: {0}
- 日本語: 検証に失敗しました: {0}

### HXT2049

- English: The output was truncated because it is too long
- 日本語: 出力が多すぎるため省略しました

### HXT2050

- English: The sections reached the total limit ({0})
- 日本語: セクションの合計が上限 ({0}) に達しました

### HXT2051

- English: '{0}' is not a pattern
- 日本語: `{0}` はパターンではありません

### HXT2052

- English: '{0}' has no member '{1}'
- 日本語: `{0}` にメンバー `{1}` はありません

### HXT2053

- English: Invalid argument for '{0}': {1}
- 日本語: `{0}` の引数が不正です: {1}

### HXT2054

- English: The data does not match the magic {0}
- 日本語: データがマジック {0} と一致しません

### HXT2055

- English: {0}
- 日本語: {0}

### HXT2056

- English: The temporary file could not be written: {0}
- 日本語: 一時ファイルに書き込めません: {0}

## Kaitai Struct (.ksy) conversion errors / Kaitai Struct (.ksy) の変換のエラー

The .ksy file could not be converted. Check the YAML at the reported line.

.ksy ファイルを変換できませんでした。報告された行の YAML を確かめてください。

### HXT3001

- English: YAML syntax error: {0}
- 日本語: YAML の構文エラー: {0}

### HXT3002

- English: YAML tags are not allowed ({0})
- 日本語: YAML の型の指定は使えません ({0})

### HXT3003

- English: Too many YAML alias expansions
- 日本語: YAML の別名の展開が多すぎます

### HXT3004

- English: Duplicate key '{0}'
- 日本語: キー `{0}` が重複しています

### HXT3005

- English: Tab characters cannot be used for indentation
- 日本語: インデントにタブ文字は使えません

### HXT3006

- English: Invalid .ksy: {0}
- 日本語: .ksy が不正です: {0}

### HXT3007

- English: The YAML nesting is too deep
- 日本語: YAML の入れ子が深すぎます

## Internal errors / 内部のエラー

An unexpected error occurred in the evaluator. Please report it together with the template and a small sample file.

評価器で想定外のエラーが起きました。テンプレートと小さなサンプルファイルを添えて報告してください。

### HXT9001

- English: Internal error in the template evaluator: {0}
- 日本語: テンプレートの評価器の内部エラー: {0}
