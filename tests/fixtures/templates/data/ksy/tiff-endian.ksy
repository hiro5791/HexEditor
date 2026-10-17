# TD-TPL-KSY-ENDIAN
meta:
  id: tiff_endian
  endian:
    switch-on: _root.order
    cases:
      '[0x49, 0x49]': le
      '[0x4d, 0x4d]': be
seq:
  - id: order
    size: 2
  - id: magic
    type: u2
  - id: ifd_offset
    type: u4
instances:
  ifd:
    pos: ifd_offset
    type: ifd
types:
  ifd:
    seq:
      - id: num_fields
        type: u2
      - id: fields
        type: field
        repeat: expr
        repeat-expr: num_fields
  field:
    seq:
      - id: tag
        type: u2
      - id: field_type
        type: u2
      - id: length
        type: u4
      - id: ofs_or_data
        type: u4
