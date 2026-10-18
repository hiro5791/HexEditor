using System.Globalization;
using System.Runtime.InteropServices;

namespace HexEditor.Core.Tools;

/// <summary>
/// Unicode の文字名 (英語。TOOL-26 の仕様 4)。Windows の ICU (<c>icu.dll</c> の <c>u_charName</c>) から取得する。ICU が使えない環境では null。
/// C0 制御文字と DEL は、ICU に名前がないため、略称と名前を表で持つ。
/// </summary>
public static unsafe class UnicodeNames
{
    private static readonly string[] C0Abbreviations =
    [
        "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI",
        "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US",
    ];

    private static readonly string[] C0Names =
    [
        "NULL", "START OF HEADING", "START OF TEXT", "END OF TEXT", "END OF TRANSMISSION", "ENQUIRY", "ACKNOWLEDGE", "BELL",
        "BACKSPACE", "CHARACTER TABULATION", "LINE FEED", "LINE TABULATION", "FORM FEED", "CARRIAGE RETURN", "SHIFT OUT", "SHIFT IN",
        "DATA LINK ESCAPE", "DEVICE CONTROL ONE", "DEVICE CONTROL TWO", "DEVICE CONTROL THREE", "DEVICE CONTROL FOUR",
        "NEGATIVE ACKNOWLEDGE", "SYNCHRONOUS IDLE", "END OF TRANSMISSION BLOCK", "CANCEL", "END OF MEDIUM", "SUBSTITUTE", "ESCAPE",
        "INFORMATION SEPARATOR FOUR", "INFORMATION SEPARATOR THREE", "INFORMATION SEPARATOR TWO", "INFORMATION SEPARATOR ONE",
    ];

    private static readonly string[] C1Abbreviations =
    [
        "PAD", "HOP", "BPH", "NBH", "IND", "NEL", "SSA", "ESA", "HTS", "HTJ", "VTS", "PLD", "PLU", "RI", "SS2", "SS3",
        "DCS", "PU1", "PU2", "STS", "CCH", "MW", "SPA", "EPA", "SOS", "SGCI", "SCI", "CSI", "ST", "OSC", "PM", "APC",
    ];

    private static bool _unavailable;

    /// <summary>制御文字の略称 (<c>NUL</c>、<c>LF</c>、<c>DEL</c> など)。制御文字でなければ null。</summary>
    public static string? ControlAbbreviation(int codePoint) => codePoint switch
    {
        >= 0 and < 0x20 => C0Abbreviations[codePoint],
        0x7F => "DEL",
        >= 0x80 and < 0xA0 => C1Abbreviations[codePoint - 0x80],
        _ => null,
    };

    /// <summary>文字名(英語)。名前がなければ null。</summary>
    public static string? Get(int codePoint)
    {
        if (codePoint is >= 0 and < 0x20)
        {
            return C0Names[codePoint];
        }

        if (codePoint == 0x7F)
        {
            return "DELETE";
        }

        if (codePoint is >= 0x80 and < 0xA0)
        {
            return ControlAbbreviation(codePoint) is { } abbreviation ? $"<control-{codePoint.ToString("X4", CultureInfo.InvariantCulture)}> ({abbreviation})" : null;
        }

        if (_unavailable || codePoint is < 0 or > 0x10FFFF)
        {
            return null;
        }

        try
        {
            byte* buffer = stackalloc byte[256];
            int error = 0;
            int length = u_charName(codePoint, 0, buffer, 256, &error);
            if (error > 0 || length <= 0)
            {
                return null;
            }

            return Marshal.PtrToStringAnsi((IntPtr)buffer, Math.Min(length, 255));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return null;
        }
    }

    [DllImport("icu.dll", EntryPoint = "u_charName", CallingConvention = CallingConvention.Cdecl)]
    private static extern int u_charName(int codePoint, int choice, byte* buffer, int bufferLength, int* error);
}
