# Memory dumps

Open memory dumps that other tools have taken, and read them by physical or virtual address.

## Open a memory dump {#open}

1. Select **Forensics > Open memory dump**, run **File: Open memory dump** from the command palette, or select **File > Open with options...** and choose the format **Memory dump**.
2. Choose the dump file.

HexEditor recognizes the format from the start of the file:

- **Physical memory image** (raw, for example the `.raw`, `.mem` or `.bin` files of WinPmem): the offset is the physical address.
- **Windows crash dump** (`MEMORY.DMP`, complete or kernel memory dump): the document shows the physical address space. Pages that are not in the dump are shown as unallocated.
- **Minidump**, including the minidumps HexEditor saves from a process: the document shows the process's virtual address space at the same addresses, and the **Memory map** panel lists its regions and modules.

A file that HexEditor does not recognize opens as a physical memory image.

Memory dumps always open read-only. When the dump opens, a notice shows the kind of dump and when it was taken. The tab's tooltip shows them too.

Only the header and the list of ranges are read when you open a dump, so even a 64 GB dump opens at once.

If the dump is damaged, or it is a kind HexEditor cannot read (for example a hibernation file, which is compressed), HexEditor asks whether to open it as raw. Select **Open as raw** to see the bytes of the file.

## Reading live memory {#live-memory}

HexEditor cannot read the physical memory of the running computer. Windows allows that only through a signed kernel driver, and HexEditor does not install one. Take a dump with another tool, and open the dump in HexEditor. To read the memory of a single process, use **File > Open Process...** instead.
