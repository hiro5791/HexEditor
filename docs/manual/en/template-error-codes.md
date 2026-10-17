# Template error codes

Every message in the Template output panel has a code such as `HXT1004`. Clicking the code opens its section on this page.

`{0}` and similar placeholders are replaced with the actual names and values.

## Loading errors (syntax, imports, pragmas)

The template could not be read. Open it in the template editor: the line with the problem is underlined. Fix the text and save, or use Try run to check it without saving.

### HXT1001

- Message: Unexpected character '{0}'

### HXT1002

- Message: '{0}' expected

### HXT1003

- Message: Unexpected '{0}'

### HXT1004

- Message: Invalid number '{0}'

### HXT1005

- Message: String literal is not terminated

### HXT1006

- Message: Invalid escape sequence '{0}'

### HXT1007

- Message: Comment is not terminated

### HXT1008

- Message: Nesting is too deep (limit {0})

### HXT1009

- Message: Invalid byte string '{0}'

### HXT1010

- Message: The template is not valid UTF-8

### HXT1011

- Message: The template is too large (limit 4 MB)

### HXT1012

- Message: Too many errors; only the first {0} are shown

### HXT1013

- Message: Invalid value '{1}' for pragma '{0}'

### HXT1014

- Message: Unknown pragma '{0}' was ignored

### HXT1015

- Message: '{0}' is already defined at {1}

### HXT1016

- Message: '{0}' was not found (searched: {1})

### HXT1017

- Message: Templates cannot perform this operation: {0}

### HXT1018

- Message: This template requires app version {0} or later

### HXT1019

- Message: Unknown attribute '{0}' was ignored

### HXT1020

- Message: {0} does not fit in {1}

### HXT1021

- Message: Value {0} has more than one name ('{1}' is used)

### HXT1022

- Message: {0} {1} exceeds the maximum; {2} is used

### HXT1023

- Message: The templates including imports are too large (limit 32 MB)

## Evaluation errors and warnings

The template was read, but applying it to the data failed at this point. Double-click the message in the Template output panel and choose Go to source or Go to data to see the line and the bytes involved. The results up to this point are still shown.

### HXT2001

- Message: '{0}' is not defined

### HXT2002

- Message: Division by zero

### HXT2003

- Message: Type mismatch: {0}

### HXT2004

- Message: Recursion is too deep (limit {0})

### HXT2005

- Message: The number of executed statements reached the limit ({0})

### HXT2006

- Message: The number of patterns reached the limit ({0})

### HXT2007

- Message: The evaluation memory reached the limit ({0})

### HXT2008

- Message: The evaluation time reached the limit ({0} s)

### HXT2009

- Message: Placement 0x{0} is outside the data

### HXT2010

- Message: {0}(0x{1}) is outside the data

### HXT2011

- Message: '{0}' (0x{1}, {2} bytes) is outside the data

### HXT2012

- Message: The position cannot be negative ({0})

### HXT2013

- Message: The array cannot end because the element size is 0

### HXT2014

- Message: The array ended because the end of the data was reached

### HXT2015

- Message: The array was truncated at the limit ({0} elements)

### HXT2016

- Message: The incomplete last element ({0} bytes) was not included

### HXT2017

- Message: The string terminator was not found

### HXT2018

- Message: LEB128 is longer than 10 bytes (offset 0x{0})

### HXT2019

- Message: The content of the structure ({0} bytes) exceeds the specified size ({1} bytes)

### HXT2020

- Message: The pointer type must be an integer type

### HXT2021

- Message: Out of range (0x{0})

### HXT2022

- Message: Patterns cannot be declared inside functions

### HXT2023

- Message: Assertion failed: {0}

### HXT2024

- Message: {0}

### HXT2025

- Message: {0}

### HXT2026

- Message: Extension function '{0}' is not registered

### HXT2027

- Message: Extension function '{0}' failed: {1}

### HXT2028

- Message: Extension function '{0}' is not permitted to run

### HXT2029

- Message: Index {0} is out of range (0 to {1})

### HXT2030

- Message: sizeof({0}) cannot be used because the size of the type is not fixed

### HXT2031

- Message: The bit field total ({0} bit) exceeds {1}

### HXT2032

- Message: A bit field cannot be wider than 64 bits ({0})

### HXT2033

- Message: {0} decompression failed: {1}

### HXT2034

- Message: Decompression stopped because the data reached the limit ({0})

### HXT2035

- Message: {0} is not supported in this version

### HXT2036

- Message: The length ({0}) exceeds the limit of 256 MB per call

### HXT2037

- Message: Cannot assign to '{0}'

### HXT2038

- Message: {0}

### HXT2039

- Message: The data at 0x{0} cannot be read

### HXT2040

- Message: {0}

### HXT2041

- Message: The evaluation was cancelled

### HXT2042

- Message: {0} does not fit in {1}

### HXT2043

- Message: '{0}' is not a function

### HXT2044

- Message: '{0}' takes {1} argument(s)

### HXT2046

- Message: Kaitai Struct feature '{0}' is not supported

### HXT2047

- Message: The contents do not match the expected bytes

### HXT2048

- Message: Validation failed: {0}

### HXT2049

- Message: The output was truncated because it is too long

### HXT2050

- Message: The sections reached the total limit ({0})

### HXT2051

- Message: '{0}' is not a pattern

### HXT2052

- Message: '{0}' has no member '{1}'

### HXT2053

- Message: Invalid argument for '{0}': {1}

### HXT2054

- Message: The data does not match the magic {0}

### HXT2055

- Message: {0}

### HXT2056

- Message: The temporary file could not be written: {0}

## Kaitai Struct (.ksy) conversion errors

The .ksy file could not be converted. Check the YAML at the reported line.

### HXT3001

- Message: YAML syntax error: {0}

### HXT3002

- Message: YAML tags are not allowed ({0})

### HXT3003

- Message: Too many YAML alias expansions

### HXT3004

- Message: Duplicate key '{0}'

### HXT3005

- Message: Tab characters cannot be used for indentation

### HXT3006

- Message: Invalid .ksy: {0}

### HXT3007

- Message: The YAML nesting is too deep

## Internal errors

An unexpected error occurred in the evaluator. Please report it together with the template and a small sample file.

### HXT9001

- Message: Internal error in the template evaluator: {0}
