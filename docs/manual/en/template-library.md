# Template standard library

Built-in functions of the `std` namespace. They can be called with their full name without `import`. Arguments with `?` are optional.

## std::bits

### std::bits::align

`align()`

Moves to the next byte boundary.

### std::bits::offset

`offset() -> u64`

Bit position inside the current byte (0 to 7).

### std::bits::popcount

`popcount(value) -> u64`

Number of 1 bits.

### std::bits::read

`read(bits) -> u64`

Reads 1 to 64 bits at the current bit position.

### std::bits::reverse

`reverse(value, bits) -> u64`

Reverses the order of the lowest bits.

## std::compress

### std::compress::brotli

`brotli(section, offset, length)`

Decompresses Brotli data of the document range into the section (lazily, up to limit.section).

### std::compress::bzip2

`bzip2(section, offset, length)`

Not available in this version (error HXT2035; added with F4-04).

### std::compress::gzip

`gzip(section, offset, length)`

Decompresses gzip data of the document range into the section (lazily, up to limit.section).

### std::compress::inflate

`inflate(section, offset, length)`

Decompresses raw Deflate data of the document range into the section (lazily, up to limit.section).

### std::compress::lz4

`lz4(section, offset, length)`

Not available in this version (error HXT2035; added with F4-04).

### std::compress::lzma

`lzma(section, offset, length)`

Not available in this version (error HXT2035; added with F4-04).

### std::compress::lzss

`lzss(section, offset, length)`

Not available in this version (error HXT2035; added with F4-04).

### std::compress::xz

`xz(section, offset, length)`

Not available in this version (error HXT2035; added with F4-04).

### std::compress::zlib

`zlib(section, offset, length)`

Decompresses zlib data of the document range into the section (lazily, up to limit.section).

### std::compress::zstd

`zstd(section, offset, length)`

Not available in this version (error HXT2035; added with F4-04).

## std::core

### std::core::array_index

`array_index() -> s64`

Index of the enclosing array element (-1 outside arrays).

### std::core::assert

`assert(condition, message?)`

Stops the evaluation with an error (HXT2023) when the condition is false.

### std::core::base_offset

`base_offset() -> u64`

Start offset given when the template was applied.

### std::core::eof

`eof() -> bool`

True when the current position ($) is at the end of the data (document, section or substream).

### std::core::get_endian

`get_endian() -> endian`

Current byte order.

### std::core::len

`len(value) -> u64`

Number of array elements, string characters, bytes or list items.

### std::core::print

`print(values…)`

Writes the values to the template output panel (up to 10,000 lines per evaluation).

### std::core::raw

`raw(pattern) -> value`

Value of the pattern before the read() conversion.

### std::core::set_endian

`set_endian(big | little)`

Changes the byte order for the rest of the current struct.

### std::core::size

`size() -> u64`

Length of the data.

### std::core::template_version

`template_version() -> string`

The version pragma of the template.

### std::core::warn

`warn(message)`

Records a warning (HXT2040) and continues.

## std::encoding

### std::encoding::base64

`base64(section, offset, length)`

Decodes Base64 text into the section.

### std::encoding::convert

`convert(section, offset, length, from?, to = "utf-8")`

Converts text from one encoding to another into the section.

### std::encoding::hex

`hex(section, offset, length)`

Decodes hexadecimal text into the section.

### std::encoding::rol

`rol(section, offset, length, bits)`

Rotates each byte left into the section.

### std::encoding::ror

`ror(section, offset, length, bits)`

Rotates each byte right into the section.

### std::encoding::xor

`xor(section, offset, length, key)`

XORs the range with the key (integer or bytes) into the section.

## std

### std::eof

`eof() -> bool`

Same as std::core::eof.

### std::format

`format(format, values…) -> string`

Same as std::format::format.

## std::format

### std::format::format

`format(format, values…) -> string`

Formats {} {:x} {:08X} {:.3f} placeholders (invariant culture).

### std::format::hex

`hex(value, digits?) -> string`

0x-prefixed hexadecimal text.

### std::format::size

`size(bytes) -> string`

Size text such as 1.5 MB.

## std::hash

### std::hash::adler32

`adler32(offset, length) -> u32`

Adler-32 of the range.

### std::hash::crc

`crc(offset, length, preset) -> u64`

CRC with a preset name of the CRC catalog (for example "CRC-16/ARC").

### std::hash::crc16

`crc16(offset, length) -> u16`

CRC-16/ARC of the range.

### std::hash::crc32

`crc32(offset, length) -> u32`

CRC-32 of the range (same as the hash panel). At most 256 MB per call (HXT2036).

### std::hash::md5

`md5(offset, length) -> bytes`

MD5 of the range.

### std::hash::sha1

`sha1(offset, length) -> bytes`

SHA-1 of the range.

### std::hash::sha256

`sha256(offset, length) -> bytes`

SHA-256 of the range.

## std::math

### std::math::abs

`abs(value)`

Absolute value.

### std::math::align_down

`align_down(value, alignment) -> s128`

Rounds down to a multiple of the alignment.

### std::math::align_up

`align_up(value, alignment) -> s128`

Rounds up to a multiple of the alignment.

### std::math::ceil

`ceil(value) -> f64`

Rounds up.

### std::math::clamp

`clamp(value, low, high)`

Value limited to the range.

### std::math::floor

`floor(value) -> f64`

Rounds down.

### std::math::is_nan

`is_nan(value) -> bool`

True for NaN.

### std::math::log2

`log2(value)`

Base-2 logarithm (integer: position of the highest bit, -1 for 0).

### std::math::max

`max(a, b)`

Larger value.

### std::math::min

`min(a, b)`

Smaller value.

### std::math::pow

`pow(base, exponent)`

Power (integer when both are integers and the exponent is 0 to 127).

### std::math::sqrt

`sqrt(value) -> f64`

Square root.

## std::mem

### std::mem::compare

`compare(offset, bytes) -> bool`

True when the data at the offset equals the bytes.

### std::mem::find

`find(bytes, start?, end?) -> s64`

First position of the bytes (?? wildcards allowed), or -1. Costs length / 1,024 steps.

### std::mem::find_all

`find_all(bytes, start?, end?, max = 10000) -> list`

All positions of the bytes, up to max.

### std::mem::read_bytes

`read_bytes(offset, length) -> bytes`

Reads up to 64 MB of bytes.

### std::mem::read_f32

`read_f32(offset, endian?) -> f32`

Reads a f32 (4 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_f64

`read_f64(offset, endian?) -> f64`

Reads a f64 (8 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_s16

`read_s16(offset, endian?) -> s16`

Reads a s16 (2 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_s24

`read_s24(offset, endian?) -> s24`

Reads a s24 (3 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_s32

`read_s32(offset, endian?) -> s32`

Reads a s32 (4 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_s64

`read_s64(offset, endian?) -> s64`

Reads a s64 (8 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_s8

`read_s8(offset, endian?) -> s8`

Reads a s8 (1 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_string

`read_string(offset, max_length, encoding?) -> string`

Reads a 0-terminated string of at most max_length bytes.

### std::mem::read_u16

`read_u16(offset, endian?) -> u16`

Reads a u16 (2 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_u24

`read_u24(offset, endian?) -> u24`

Reads a u24 (3 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_u32

`read_u32(offset, endian?) -> u32`

Reads a u32 (4 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_u64

`read_u64(offset, endian?) -> u64`

Reads a u64 (8 bytes) at the offset. Error HXT2010 outside the data.

### std::mem::read_u8

`read_u8(offset, endian?) -> u8`

Reads a u8 (1 bytes) at the offset. Error HXT2010 outside the data.

## std::ptr

### std::ptr::null_if

`null_if(value) -> function`

pointer_map function that treats the value as null.

### std::ptr::relative_to

`relative_to(base) -> function`

pointer_map function that adds the base.

## std::section

### std::section::append

`append(section, bytes)`

Appends bytes to the section.

### std::section::copy

`copy(section, offset, length)`

Copies a document range into the section.

### std::section::create

`create(name?) -> section`

Creates an empty section.

### std::section::open_in_tab

`open_in_tab(section)`

Does nothing: open the section from the result tree (templates cannot open tabs).

### std::section::size

`size(section) -> u64`

Length of the section (produces its data).

## std::string

### std::string::ends_with

`ends_with(string, text) -> bool`

Ordinal suffix test.

### std::string::find

`find(string, text) -> s64`

First position of the text (ordinal), or -1.

### std::string::from_bytes

`from_bytes(bytes, encoding?) -> string`

Decodes bytes as text.

### std::string::length

`length(string) -> u64`

Number of characters.

### std::string::parse_float

`parse_float(string) -> f64`

Parses a number with '.' as the decimal point.

### std::string::parse_int

`parse_int(string, radix = 10) -> s128`

Parses an integer. Error HXT2053 for invalid digits.

### std::string::split

`split(string, separator) -> list`

Splits the string.

### std::string::starts_with

`starts_with(string, text) -> bool`

Ordinal prefix test.

### std::string::substr

`substr(string, start, length?) -> string`

Part of the string.

### std::string::to_lower

`to_lower(string) -> string`

Lower case, independent of the regional settings.

### std::string::to_string

`to_string(value) -> string`

Display text of the value.

### std::string::to_upper

`to_upper(string) -> string`

Upper case, independent of the regional settings.

### std::string::trim

`trim(string) -> string`

Removes leading and trailing white space.

## std::sys

### std::sys::demangle

`demangle(name) -> string`

Demangles a C++, Rust or D symbol name, the same way as [demangling](disassembly.md#demangle) in the app. A name that can't be demangled is returned unchanged.

### std::sys::detected_mime

`detected_mime() -> string`

MIME type from the file type detection.

### std::sys::file_extension

`file_extension() -> string`

Extension of the document file name, without the dot.

### std::sys::file_name

`file_name() -> string`

File name of the document, without folders.

## std::time

### std::time::from_dos

`from_dos(date, time) -> datetime`

Date and time from MS-DOS values (null when invalid).

### std::time::from_filetime

`from_filetime(value) -> datetime`

Date and time from a Windows FILETIME (UTC).

### std::time::from_unix

`from_unix(seconds) -> datetime`

Date and time from UNIX seconds (UTC).

### std::time::to_string

`to_string(datetime, format = "yyyy-MM-dd HH:mm:ss") -> string`

Text of the date and time in UTC.
