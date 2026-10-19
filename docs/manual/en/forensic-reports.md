# Forensic reports

Make an HTML or PDF report of a case: the case information, the evidence with its hashes, bookmarks, carving results and the audit log.

## Create a report {#create}

1. Open the case.
2. Select **Forensics > Create report...**.
3. Tick what to include. Items with no data are left out.
4. Choose **HTML** or **PDF**. For PDF, choose A4 or Letter paper (portrait).
5. To limit the audit log, untick **All audit log entries** and type the start and end in UTC. Set how many bytes of Hex dump to show for each bookmark (0 to 4096; 64 by default).
6. Check the folder (the `reports` folder of the case by default) and select **Create**.

The report can contain: the cover (case number, name, investigator, organization, creation time and time zone, application and version), the evidence list with hashes and verification results, acquisition records, device information, partitions, file systems, bookmarks with a Hex dump at each position, carving results, and the audit log.

An HTML report is one file with its style inside and nothing loaded from the network, so it opens offline. A PDF report has the case number and the page number on every page.

Times are shown in UTC first, with the local time and the name of the time zone next to them. The language follows the language of the application.

Long lists show the first 1,000 rows, and the full list is saved next to the report as a CSV file. In a PDF, long lists are cut earlier and you are told to use the HTML report or the CSV for the full list. If the PDF cannot be made, an HTML report is made instead and the reason is shown.

When the report is done, a message shows its SHA-256. The same value is recorded in the audit log, so you can prove later that the report was not changed. Use **Open** or **Open folder** in the message to see it.

To show your own logo and a line of text at the top, set them in **Settings > Forensics**.
