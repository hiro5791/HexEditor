# YARA scan

YARA rules describe patterns of bytes and conditions. You can scan a document, the selection or the files in a folder with your own rules, and see where the rules match.

HexEditor does not include any rules. Write your own, or use rules from a source you trust.

## Scan a document {#scan}

1. Select **Search > YARA**. The **YARA** panel opens at the bottom.
2. Select **Add rule files...** and choose one or more `.yar` or `.yara` files. **Add folder...** adds all rule files in a folder and its subfolders.
3. Choose the **Scope**: **Whole document**, **Selection**, or **Files in a folder**.
4. Select **Scan**.

The scan uses the current state of the document, including edits that are not saved. A large document is scanned in the background with progress, and you can cancel it with **Cancel**; the results found so far are kept.

## Results {#results}

Matches are listed in the results list. Each matching string is one row with the rule name, namespace, tags, meta data, string identifier (such as `$a`), offset, length and the matched bytes. A rule that matched without a string (only by its condition) is shown as a row without an offset. Open a row to go to the match and to see all meta data of the rule.

The matched ranges are highlighted in the hex view as annotations. Point at a highlighted byte to see the rule name and the string identifier. Closing the results list removes the highlights. To hide them without closing the list, use the annotation settings in the **View** menu.

The results can be turned into bookmarks or a selection, and exported, like other search results.

When you scan the files in a folder, enter the **Folder** and the **Files** mask (such as `*.exe;*.dll`). The matches are listed per file in the panel. Select a row to open the file at the match.

## Rule sets and external variables {#rule-sets}

- **Save set...** saves the current rule files and external variables with a name. Choose a saved set in **Rule set** to load it. **Delete set** removes it.
- **External variables** are values that rules can use (the `-d` option of the yara command). Enter one per line as `name=value`. `true` and `false` are Boolean values, numbers are numbers, and anything else is text.

## Errors and limits {#limits}

- If a rule file has errors, the panel lists the file name, line and message, and the scan does not start.
- Compiled rule files made with `yarac` cannot be used, because their format depends on the YARA version. Add the rule source files instead.
- The modules `pe`, `elf`, `math`, `hash`, `time`, `string` and `console` are available. Modules that need outside data, such as `cuckoo`, are not.
- The data is scanned in blocks (64 MiB by default) that overlap by 64 KB. In a document larger than one block, a match longer than the overlap, or a condition that depends on strings in different blocks, may not be found. The panel always shows this limit.
- The `pe` and `elf` modules read the first block.
- A scan stops after 600 seconds by default and keeps the results found so far.
- Regular expressions match at most 4,096 bytes, and unbounded jumps in hex strings cover at most 64 KiB.
- Each string keeps at most 1,000,000 matches.

You can change the block size (**YARA: block size (MiB)**) and the time limit (**YARA: time limit (seconds)**) in the search settings.
