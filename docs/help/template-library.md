# Template standard library / テンプレートの標準ライブラリ

Built-in functions of the `std` namespace (TPL-18). They can be called with their full name without `import`. Arguments with `?` are optional.

`std` 名前空間の組み込み関数 (TPL-18)。インポートなしで完全な名前で呼べます。`?` の付いた引数は省けます。

## std::bits

### std::bits::align

`align()`

- English: Moves to the next byte boundary.
- 日本語: 次のバイト境界に進む。

### std::bits::offset

`offset() -> u64`

- English: Bit position inside the current byte (0 to 7).
- 日本語: 今のバイトの中のビット位置 (0〜7)。

### std::bits::popcount

`popcount(value) -> u64`

- English: Number of 1 bits.
- 日本語: 1 のビットの数。

### std::bits::read

`read(bits) -> u64`

- English: Reads 1 to 64 bits at the current bit position.
- 日本語: 現在のビット位置から 1〜64 ビットを読む。

### std::bits::reverse

`reverse(value, bits) -> u64`

- English: Reverses the order of the lowest bits.
- 日本語: 下位 bits ビットの並びを逆にする。

## std::compress

### std::compress::brotli

`brotli(section, offset, length)`

- English: Decompresses Brotli data of the document range into the section (lazily, up to limit.section).
- 日本語: ドキュメントの範囲の Brotli を展開してセクションに入れる (必要になったとき。limit.section まで)。

### std::compress::bzip2

`bzip2(section, offset, length)`

- English: Not available in this version (error HXT2035; added with F4-04).
- 日本語: この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::gzip

`gzip(section, offset, length)`

- English: Decompresses gzip data of the document range into the section (lazily, up to limit.section).
- 日本語: ドキュメントの範囲の gzip を展開してセクションに入れる (必要になったとき。limit.section まで)。

### std::compress::inflate

`inflate(section, offset, length)`

- English: Decompresses raw Deflate data of the document range into the section (lazily, up to limit.section).
- 日本語: ドキュメントの範囲の raw Deflate を展開してセクションに入れる (必要になったとき。limit.section まで)。

### std::compress::lz4

`lz4(section, offset, length)`

- English: Not available in this version (error HXT2035; added with F4-04).
- 日本語: この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::lzma

`lzma(section, offset, length)`

- English: Not available in this version (error HXT2035; added with F4-04).
- 日本語: この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::lzss

`lzss(section, offset, length)`

- English: Not available in this version (error HXT2035; added with F4-04).
- 日本語: この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::xz

`xz(section, offset, length)`

- English: Not available in this version (error HXT2035; added with F4-04).
- 日本語: この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::zlib

`zlib(section, offset, length)`

- English: Decompresses zlib data of the document range into the section (lazily, up to limit.section).
- 日本語: ドキュメントの範囲の zlib を展開してセクションに入れる (必要になったとき。limit.section まで)。

### std::compress::zstd

`zstd(section, offset, length)`

- English: Not available in this version (error HXT2035; added with F4-04).
- 日本語: この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

## std::core

### std::core::array_index

`array_index() -> s64`

- English: Index of the enclosing array element (-1 outside arrays).
- 日本語: 囲んでいる配列の要素の添字 (配列の外では -1)。

### std::core::assert

`assert(condition, message?)`

- English: Stops the evaluation with an error (HXT2023) when the condition is false.
- 日本語: 条件が偽ならエラー (HXT2023) で評価を止める。

### std::core::base_offset

`base_offset() -> u64`

- English: Start offset given when the template was applied.
- 日本語: 適用したときの開始オフセット。

### std::core::eof

`eof() -> bool`

- English: True when the current position ($) is at the end of the data (document, section or substream).
- 日本語: 現在位置 ($) がデータ (ドキュメント・セクション・部分ストリーム) の末尾なら真。

### std::core::get_endian

`get_endian() -> endian`

- English: Current byte order.
- 日本語: 今のエンディアン。

### std::core::len

`len(value) -> u64`

- English: Number of array elements, string characters, bytes or list items.
- 日本語: 配列の要素数、文字列の文字数、バイト列・一覧の長さ。

### std::core::print

`print(values…)`

- English: Writes the values to the template output panel (up to 10,000 lines per evaluation).
- 日本語: 値を出力パネルに書く (1 回の評価で 1 万行まで)。

### std::core::raw

`raw(pattern) -> value`

- English: Value of the pattern before the read() conversion.
- 日本語: read 変換の前の値。

### std::core::set_endian

`set_endian(big | little)`

- English: Changes the byte order for the rest of the current struct.
- 日本語: 今の構造体の残りの文のエンディアンを変える。

### std::core::size

`size() -> u64`

- English: Length of the data.
- 日本語: データの長さ。

### std::core::template_version

`template_version() -> string`

- English: The version pragma of the template.
- 日本語: テンプレートの version プラグマの値。

### std::core::warn

`warn(message)`

- English: Records a warning (HXT2040) and continues.
- 日本語: 警告 (HXT2040) を記録し、評価を続ける。

## std::encoding

### std::encoding::base64

`base64(section, offset, length)`

- English: Decodes Base64 text into the section.
- 日本語: Base64 の文字を復号してセクションに入れる。

### std::encoding::convert

`convert(section, offset, length, from?, to = "utf-8")`

- English: Converts text from one encoding to another into the section.
- 日本語: 文字コードを変換してセクションに入れる。

### std::encoding::hex

`hex(section, offset, length)`

- English: Decodes hexadecimal text into the section.
- 日本語: 16 進の文字を復号してセクションに入れる。

### std::encoding::rol

`rol(section, offset, length, bits)`

- English: Rotates each byte left into the section.
- 日本語: 各バイトを左に回転してセクションに入れる。

### std::encoding::ror

`ror(section, offset, length, bits)`

- English: Rotates each byte right into the section.
- 日本語: 各バイトを右に回転してセクションに入れる。

### std::encoding::xor

`xor(section, offset, length, key)`

- English: XORs the range with the key (integer or bytes) into the section.
- 日本語: 範囲を鍵 (整数またはバイト列) で XOR してセクションに入れる。

## std

### std::eof

`eof() -> bool`

- English: Same as std::core::eof.
- 日本語: std::core::eof と同じ。

### std::format

`format(format, values…) -> string`

- English: Same as std::format::format.
- 日本語: std::format::format と同じ。

## std::format

### std::format::format

`format(format, values…) -> string`

- English: Formats {} {:x} {:08X} {:.3f} placeholders (invariant culture).
- 日本語: {} {:x} {:08X} {:.3f} の書式で文字列を作る (地域設定に依存しない)。

### std::format::hex

`hex(value, digits?) -> string`

- English: 0x-prefixed hexadecimal text.
- 日本語: 0x 付きの 16 進の文字列。

### std::format::size

`size(bytes) -> string`

- English: Size text such as 1.5 MB.
- 日本語: 1.5 MB のような大きさの文字列。

## std::hash

### std::hash::adler32

`adler32(offset, length) -> u32`

- English: Adler-32 of the range.
- 日本語: 範囲の Adler-32。

### std::hash::crc

`crc(offset, length, preset) -> u64`

- English: CRC with a preset name of the CRC catalog (for example "CRC-16/ARC").
- 日本語: CRC カタログのプリセット名 (例: "CRC-16/ARC") の CRC。

### std::hash::crc16

`crc16(offset, length) -> u16`

- English: CRC-16/ARC of the range.
- 日本語: 範囲の CRC-16/ARC。

### std::hash::crc32

`crc32(offset, length) -> u32`

- English: CRC-32 of the range (same as the hash panel). At most 256 MB per call (HXT2036).
- 日本語: 範囲の CRC-32 (ハッシュパネルと同じ)。1 回 256 MB まで (HXT2036)。

### std::hash::md5

`md5(offset, length) -> bytes`

- English: MD5 of the range.
- 日本語: 範囲の MD5。

### std::hash::sha1

`sha1(offset, length) -> bytes`

- English: SHA-1 of the range.
- 日本語: 範囲の SHA-1。

### std::hash::sha256

`sha256(offset, length) -> bytes`

- English: SHA-256 of the range.
- 日本語: 範囲の SHA-256。

## std::math

### std::math::abs

`abs(value)`

- English: Absolute value.
- 日本語: 絶対値。

### std::math::align_down

`align_down(value, alignment) -> s128`

- English: Rounds down to a multiple of the alignment.
- 日本語: alignment の倍数に切り下げる。

### std::math::align_up

`align_up(value, alignment) -> s128`

- English: Rounds up to a multiple of the alignment.
- 日本語: alignment の倍数に切り上げる。

### std::math::ceil

`ceil(value) -> f64`

- English: Rounds up.
- 日本語: 切り上げ。

### std::math::clamp

`clamp(value, low, high)`

- English: Value limited to the range.
- 日本語: 範囲に収めた値。

### std::math::floor

`floor(value) -> f64`

- English: Rounds down.
- 日本語: 切り捨て。

### std::math::is_nan

`is_nan(value) -> bool`

- English: True for NaN.
- 日本語: NaN なら真。

### std::math::log2

`log2(value)`

- English: Base-2 logarithm (integer: position of the highest bit, -1 for 0).
- 日本語: 2 を底とする対数 (整数なら最上位ビットの位置。0 は -1)。

### std::math::max

`max(a, b)`

- English: Larger value.
- 日本語: 大きい方。

### std::math::min

`min(a, b)`

- English: Smaller value.
- 日本語: 小さい方。

### std::math::pow

`pow(base, exponent)`

- English: Power (integer when both are integers and the exponent is 0 to 127).
- 日本語: べき乗 (両方が整数で指数が 0〜127 なら整数)。

### std::math::sqrt

`sqrt(value) -> f64`

- English: Square root.
- 日本語: 平方根。

## std::mem

### std::mem::compare

`compare(offset, bytes) -> bool`

- English: True when the data at the offset equals the bytes.
- 日本語: オフセットのデータがバイト列と一致すれば真。

### std::mem::find

`find(bytes, start?, end?) -> s64`

- English: First position of the bytes (?? wildcards allowed), or -1. Costs length / 1,024 steps.
- 日本語: バイト列 (?? のワイルドカード可) の最初の位置。なければ -1。長さ / 1,024 のステップを使う。

### std::mem::find_all

`find_all(bytes, start?, end?, max = 10000) -> list`

- English: All positions of the bytes, up to max.
- 日本語: バイト列のすべての位置 (max 件まで)。

### std::mem::read_bytes

`read_bytes(offset, length) -> bytes`

- English: Reads up to 64 MB of bytes.
- 日本語: バイト列を読む (64 MB まで)。

### std::mem::read_f32

`read_f32(offset, endian?) -> f32`

- English: Reads a f32 (4 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの f32 (4 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_f64

`read_f64(offset, endian?) -> f64`

- English: Reads a f64 (8 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの f64 (8 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s16

`read_s16(offset, endian?) -> s16`

- English: Reads a s16 (2 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの s16 (2 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s24

`read_s24(offset, endian?) -> s24`

- English: Reads a s24 (3 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの s24 (3 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s32

`read_s32(offset, endian?) -> s32`

- English: Reads a s32 (4 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの s32 (4 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s64

`read_s64(offset, endian?) -> s64`

- English: Reads a s64 (8 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの s64 (8 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s8

`read_s8(offset, endian?) -> s8`

- English: Reads a s8 (1 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの s8 (1 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_string

`read_string(offset, max_length, encoding?) -> string`

- English: Reads a 0-terminated string of at most max_length bytes.
- 日本語: 最大 max_length バイトの、0 で終わる文字列を読む。

### std::mem::read_u16

`read_u16(offset, endian?) -> u16`

- English: Reads a u16 (2 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの u16 (2 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_u24

`read_u24(offset, endian?) -> u24`

- English: Reads a u24 (3 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの u24 (3 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_u32

`read_u32(offset, endian?) -> u32`

- English: Reads a u32 (4 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの u32 (4 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_u64

`read_u64(offset, endian?) -> u64`

- English: Reads a u64 (8 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの u64 (8 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_u8

`read_u8(offset, endian?) -> u8`

- English: Reads a u8 (1 bytes) at the offset. Error HXT2010 outside the data.
- 日本語: オフセットの u8 (1 バイト) を読む。範囲外はエラー HXT2010。

## std::ptr

### std::ptr::null_if

`null_if(value) -> function`

- English: pointer_map function that treats the value as null.
- 日本語: その値を null とする pointer_map の関数。

### std::ptr::relative_to

`relative_to(base) -> function`

- English: pointer_map function that adds the base.
- 日本語: 基準を足す pointer_map の関数。

## std::section

### std::section::append

`append(section, bytes)`

- English: Appends bytes to the section.
- 日本語: セクションにバイト列を加える。

### std::section::copy

`copy(section, offset, length)`

- English: Copies a document range into the section.
- 日本語: ドキュメントの範囲をセクションにコピーする。

### std::section::create

`create(name?) -> section`

- English: Creates an empty section.
- 日本語: 空のセクションを作る。

### std::section::open_in_tab

`open_in_tab(section)`

- English: Does nothing: open the section from the result tree (templates cannot open tabs).
- 日本語: 何もしない (タブは結果ツリーから開く。テンプレートはタブを開かない)。

### std::section::size

`size(section) -> u64`

- English: Length of the section (produces its data).
- 日本語: セクションの長さ (データを作る)。

## std::string

### std::string::ends_with

`ends_with(string, text) -> bool`

- English: Ordinal suffix test.
- 日本語: 末尾が一致するか (序数比較)。

### std::string::find

`find(string, text) -> s64`

- English: First position of the text (ordinal), or -1.
- 日本語: text の最初の位置 (序数比較)。なければ -1。

### std::string::from_bytes

`from_bytes(bytes, encoding?) -> string`

- English: Decodes bytes as text.
- 日本語: バイト列を文字列に復号する。

### std::string::length

`length(string) -> u64`

- English: Number of characters.
- 日本語: 文字数。

### std::string::parse_float

`parse_float(string) -> f64`

- English: Parses a number with '.' as the decimal point.
- 日本語: 小数点を . とする数値として読む。

### std::string::parse_int

`parse_int(string, radix = 10) -> s128`

- English: Parses an integer. Error HXT2053 for invalid digits.
- 日本語: 整数として読む。不正な桁はエラー HXT2053。

### std::string::split

`split(string, separator) -> list`

- English: Splits the string.
- 日本語: 区切りで分ける。

### std::string::starts_with

`starts_with(string, text) -> bool`

- English: Ordinal prefix test.
- 日本語: 先頭が一致するか (序数比較)。

### std::string::substr

`substr(string, start, length?) -> string`

- English: Part of the string.
- 日本語: 部分文字列。

### std::string::to_lower

`to_lower(string) -> string`

- English: Lower case, independent of the regional settings.
- 日本語: 小文字にする (地域設定に依存しない)。

### std::string::to_string

`to_string(value) -> string`

- English: Display text of the value.
- 日本語: 値の表示文字列。

### std::string::to_upper

`to_upper(string) -> string`

- English: Upper case, independent of the regional settings.
- 日本語: 大文字にする (地域設定に依存しない)。

### std::string::trim

`trim(string) -> string`

- English: Removes leading and trailing white space.
- 日本語: 前後の空白を除く。

## std::sys

### std::sys::demangle

`demangle(name) -> string`

- English: Returns the name unchanged in this version (demangling comes with ANA-30).
- 日本語: この版では名前をそのまま返す (デマングルは ANA-30 と同時)。

### std::sys::detected_mime

`detected_mime() -> string`

- English: MIME type from the file type detection.
- 日本語: ファイル形式の判定の MIME。

### std::sys::file_extension

`file_extension() -> string`

- English: Extension of the document file name, without the dot.
- 日本語: ファイル名の拡張子 (. なし)。

### std::sys::file_name

`file_name() -> string`

- English: File name of the document, without folders.
- 日本語: ドキュメントのファイル名 (フォルダを含まない)。

## std::time

### std::time::from_dos

`from_dos(date, time) -> datetime`

- English: Date and time from MS-DOS values (null when invalid).
- 日本語: MS-DOS の日付・時刻からの日時 (不正なら null)。

### std::time::from_filetime

`from_filetime(value) -> datetime`

- English: Date and time from a Windows FILETIME (UTC).
- 日本語: Windows FILETIME からの日時 (UTC)。

### std::time::from_unix

`from_unix(seconds) -> datetime`

- English: Date and time from UNIX seconds (UTC).
- 日本語: UNIX 時刻 (秒) からの日時 (UTC)。

### std::time::to_string

`to_string(datetime, format = "yyyy-MM-dd HH:mm:ss") -> string`

- English: Text of the date and time in UTC.
- 日本語: 日時の文字列 (UTC)。
