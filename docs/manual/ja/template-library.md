# テンプレートの標準ライブラリ

`std` 名前空間の組み込み関数。インポートなしで完全な名前で呼べます。`?` の付いた引数は省けます。

## std::bits

### std::bits::align

`align()`

次のバイト境界に進む。

### std::bits::offset

`offset() -> u64`

今のバイトの中のビット位置 (0〜7)。

### std::bits::popcount

`popcount(value) -> u64`

1 のビットの数。

### std::bits::read

`read(bits) -> u64`

現在のビット位置から 1〜64 ビットを読む。

### std::bits::reverse

`reverse(value, bits) -> u64`

下位 bits ビットの並びを逆にする。

## std::compress

### std::compress::brotli

`brotli(section, offset, length)`

ドキュメントの範囲の Brotli を展開してセクションに入れる (必要になったとき。limit.section まで)。

### std::compress::bzip2

`bzip2(section, offset, length)`

この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::gzip

`gzip(section, offset, length)`

ドキュメントの範囲の gzip を展開してセクションに入れる (必要になったとき。limit.section まで)。

### std::compress::inflate

`inflate(section, offset, length)`

ドキュメントの範囲の raw Deflate を展開してセクションに入れる (必要になったとき。limit.section まで)。

### std::compress::lz4

`lz4(section, offset, length)`

この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::lzma

`lzma(section, offset, length)`

この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::lzss

`lzss(section, offset, length)`

この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::xz

`xz(section, offset, length)`

この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

### std::compress::zlib

`zlib(section, offset, length)`

ドキュメントの範囲の zlib を展開してセクションに入れる (必要になったとき。limit.section まで)。

### std::compress::zstd

`zstd(section, offset, length)`

この版では使えない (エラー HXT2035。F4-04 と同時に追加)。

## std::core

### std::core::array_index

`array_index() -> s64`

囲んでいる配列の要素の添字 (配列の外では -1)。

### std::core::assert

`assert(condition, message?)`

条件が偽ならエラー (HXT2023) で評価を止める。

### std::core::base_offset

`base_offset() -> u64`

適用したときの開始オフセット。

### std::core::eof

`eof() -> bool`

現在位置 ($) がデータ (ドキュメント・セクション・部分ストリーム) の末尾なら真。

### std::core::get_endian

`get_endian() -> endian`

今のエンディアン。

### std::core::len

`len(value) -> u64`

配列の要素数、文字列の文字数、バイト列・一覧の長さ。

### std::core::print

`print(values…)`

値を出力パネルに書く (1 回の評価で 1 万行まで)。

### std::core::raw

`raw(pattern) -> value`

read 変換の前の値。

### std::core::set_endian

`set_endian(big | little)`

今の構造体の残りの文のエンディアンを変える。

### std::core::size

`size() -> u64`

データの長さ。

### std::core::template_version

`template_version() -> string`

テンプレートの version プラグマの値。

### std::core::warn

`warn(message)`

警告 (HXT2040) を記録し、評価を続ける。

## std::encoding

### std::encoding::base64

`base64(section, offset, length)`

Base64 の文字を復号してセクションに入れる。

### std::encoding::convert

`convert(section, offset, length, from?, to = "utf-8")`

文字コードを変換してセクションに入れる。

### std::encoding::hex

`hex(section, offset, length)`

16 進の文字を復号してセクションに入れる。

### std::encoding::rol

`rol(section, offset, length, bits)`

各バイトを左に回転してセクションに入れる。

### std::encoding::ror

`ror(section, offset, length, bits)`

各バイトを右に回転してセクションに入れる。

### std::encoding::xor

`xor(section, offset, length, key)`

範囲を鍵 (整数またはバイト列) で XOR してセクションに入れる。

## std

### std::eof

`eof() -> bool`

std::core::eof と同じ。

### std::format

`format(format, values…) -> string`

std::format::format と同じ。

## std::format

### std::format::format

`format(format, values…) -> string`

{} {:x} {:08X} {:.3f} の書式で文字列を作る (地域設定に依存しない)。

### std::format::hex

`hex(value, digits?) -> string`

0x 付きの 16 進の文字列。

### std::format::size

`size(bytes) -> string`

1.5 MB のような大きさの文字列。

## std::hash

### std::hash::adler32

`adler32(offset, length) -> u32`

範囲の Adler-32。

### std::hash::crc

`crc(offset, length, preset) -> u64`

CRC カタログのプリセット名 (例: "CRC-16/ARC") の CRC。

### std::hash::crc16

`crc16(offset, length) -> u16`

範囲の CRC-16/ARC。

### std::hash::crc32

`crc32(offset, length) -> u32`

範囲の CRC-32 (ハッシュパネルと同じ)。1 回 256 MB まで (HXT2036)。

### std::hash::md5

`md5(offset, length) -> bytes`

範囲の MD5。

### std::hash::sha1

`sha1(offset, length) -> bytes`

範囲の SHA-1。

### std::hash::sha256

`sha256(offset, length) -> bytes`

範囲の SHA-256。

## std::math

### std::math::abs

`abs(value)`

絶対値。

### std::math::align_down

`align_down(value, alignment) -> s128`

alignment の倍数に切り下げる。

### std::math::align_up

`align_up(value, alignment) -> s128`

alignment の倍数に切り上げる。

### std::math::ceil

`ceil(value) -> f64`

切り上げ。

### std::math::clamp

`clamp(value, low, high)`

範囲に収めた値。

### std::math::floor

`floor(value) -> f64`

切り捨て。

### std::math::is_nan

`is_nan(value) -> bool`

NaN なら真。

### std::math::log2

`log2(value)`

2 を底とする対数 (整数なら最上位ビットの位置。0 は -1)。

### std::math::max

`max(a, b)`

大きい方。

### std::math::min

`min(a, b)`

小さい方。

### std::math::pow

`pow(base, exponent)`

べき乗 (両方が整数で指数が 0〜127 なら整数)。

### std::math::sqrt

`sqrt(value) -> f64`

平方根。

## std::mem

### std::mem::compare

`compare(offset, bytes) -> bool`

オフセットのデータがバイト列と一致すれば真。

### std::mem::find

`find(bytes, start?, end?) -> s64`

バイト列 (?? のワイルドカード可) の最初の位置。なければ -1。長さ / 1,024 のステップを使う。

### std::mem::find_all

`find_all(bytes, start?, end?, max = 10000) -> list`

バイト列のすべての位置 (max 件まで)。

### std::mem::read_bytes

`read_bytes(offset, length) -> bytes`

バイト列を読む (64 MB まで)。

### std::mem::read_f32

`read_f32(offset, endian?) -> f32`

オフセットの f32 (4 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_f64

`read_f64(offset, endian?) -> f64`

オフセットの f64 (8 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s16

`read_s16(offset, endian?) -> s16`

オフセットの s16 (2 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s24

`read_s24(offset, endian?) -> s24`

オフセットの s24 (3 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s32

`read_s32(offset, endian?) -> s32`

オフセットの s32 (4 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s64

`read_s64(offset, endian?) -> s64`

オフセットの s64 (8 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_s8

`read_s8(offset, endian?) -> s8`

オフセットの s8 (1 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_string

`read_string(offset, max_length, encoding?) -> string`

最大 max_length バイトの、0 で終わる文字列を読む。

### std::mem::read_u16

`read_u16(offset, endian?) -> u16`

オフセットの u16 (2 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_u24

`read_u24(offset, endian?) -> u24`

オフセットの u24 (3 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_u32

`read_u32(offset, endian?) -> u32`

オフセットの u32 (4 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_u64

`read_u64(offset, endian?) -> u64`

オフセットの u64 (8 バイト) を読む。範囲外はエラー HXT2010。

### std::mem::read_u8

`read_u8(offset, endian?) -> u8`

オフセットの u8 (1 バイト) を読む。範囲外はエラー HXT2010。

## std::ptr

### std::ptr::null_if

`null_if(value) -> function`

その値を null とする pointer_map の関数。

### std::ptr::relative_to

`relative_to(base) -> function`

基準を足す pointer_map の関数。

## std::section

### std::section::append

`append(section, bytes)`

セクションにバイト列を加える。

### std::section::copy

`copy(section, offset, length)`

ドキュメントの範囲をセクションにコピーする。

### std::section::create

`create(name?) -> section`

空のセクションを作る。

### std::section::open_in_tab

`open_in_tab(section)`

何もしない (タブは結果ツリーから開く。テンプレートはタブを開かない)。

### std::section::size

`size(section) -> u64`

セクションの長さ (データを作る)。

## std::string

### std::string::ends_with

`ends_with(string, text) -> bool`

末尾が一致するか (序数比較)。

### std::string::find

`find(string, text) -> s64`

text の最初の位置 (序数比較)。なければ -1。

### std::string::from_bytes

`from_bytes(bytes, encoding?) -> string`

バイト列を文字列に復号する。

### std::string::length

`length(string) -> u64`

文字数。

### std::string::parse_float

`parse_float(string) -> f64`

小数点を . とする数値として読む。

### std::string::parse_int

`parse_int(string, radix = 10) -> s128`

整数として読む。不正な桁はエラー HXT2053。

### std::string::split

`split(string, separator) -> list`

区切りで分ける。

### std::string::starts_with

`starts_with(string, text) -> bool`

先頭が一致するか (序数比較)。

### std::string::substr

`substr(string, start, length?) -> string`

部分文字列。

### std::string::to_lower

`to_lower(string) -> string`

小文字にする (地域設定に依存しない)。

### std::string::to_string

`to_string(value) -> string`

値の表示文字列。

### std::string::to_upper

`to_upper(string) -> string`

大文字にする (地域設定に依存しない)。

### std::string::trim

`trim(string) -> string`

前後の空白を除く。

## std::sys

### std::sys::demangle

`demangle(name) -> string`

この版では名前をそのまま返す (デマングルは ANA-30 と同時)。

### std::sys::detected_mime

`detected_mime() -> string`

ファイル形式の判定の MIME。

### std::sys::file_extension

`file_extension() -> string`

ファイル名の拡張子 (. なし)。

### std::sys::file_name

`file_name() -> string`

ドキュメントのファイル名 (フォルダを含まない)。

## std::time

### std::time::from_dos

`from_dos(date, time) -> datetime`

MS-DOS の日付・時刻からの日時 (不正なら null)。

### std::time::from_filetime

`from_filetime(value) -> datetime`

Windows FILETIME からの日時 (UTC)。

### std::time::from_unix

`from_unix(seconds) -> datetime`

UNIX 時刻 (秒) からの日時 (UTC)。

### std::time::to_string

`to_string(datetime, format = "yyyy-MM-dd HH:mm:ss") -> string`

日時の文字列 (UTC)。
