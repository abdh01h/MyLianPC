using System.Text.RegularExpressions;

namespace MyLianPC.Core.Drivers;

/// <summary>
/// Compares the two version shapes this app has to deal with: dotted numeric driver versions
/// ("11.29.50.0202") and GIGABYTE's BIOS letters ("F12d"). Returns null when the two strings cannot be
/// compared, which the UI reports as "Check manually" rather than guessing.
/// </summary>
public static partial class Versions
{
    [GeneratedRegex(@"^\s*([A-Za-z])\s*(\d+)\s*([A-Za-z]?)\s*$")]
    private static partial Regex BiosPattern();

    /// <summary>Numeric segments, in order. Leading zeros are ignored, so 18.4032.2510.901 equals ...0901.</summary>
    private static List<string> Segments(string value) =>
        Regex.Matches(value ?? "", @"\d+").Select(m => m.Value.TrimStart('0') is { Length: > 0 } s ? s : "0").ToList();

    /// <summary>Compares dotted numeric versions. Null when either side has no digits.</summary>
    public static int? CompareNumeric(string? left, string? right)
    {
        var a = Segments(left ?? "");
        var b = Segments(right ?? "");
        if (a.Count == 0 || b.Count == 0) return null;

        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            string x = i < a.Count ? a[i] : "0";
            string y = i < b.Count ? b[i] : "0";
            // Very long digit groups (unusual) are compared by length, then lexically: both sides are digits.
            int cmp = x.Length != y.Length ? x.Length.CompareTo(y.Length) : string.CompareOrdinal(x, y);
            if (cmp != 0) return cmp < 0 ? -1 : 1;
        }
        return 0;
    }

    /// <summary>Compares BIOS versions: F12d &lt; F12e &lt; F13. Null when either side is not that shape.</summary>
    public static int? CompareBios(string? installed, string? latest)
    {
        var a = BiosPattern().Match(installed ?? "");
        var b = BiosPattern().Match(latest ?? "");
        if (!a.Success || !b.Success) return null;

        int cmp = string.Compare(a.Groups[1].Value, b.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
        if (cmp != 0) return cmp < 0 ? -1 : 1;

        if (!long.TryParse(a.Groups[2].Value, out long x) || !long.TryParse(b.Groups[2].Value, out long y)) return null;
        if (x != y) return x < y ? -1 : 1;

        string suffixA = a.Groups[3].Value.ToLowerInvariant();
        string suffixB = b.Groups[3].Value.ToLowerInvariant();
        if (suffixA == suffixB) return 0;
        // No suffix sorts before "a", so F12 < F12a.
        if (suffixA.Length == 0) return -1;
        if (suffixB.Length == 0) return 1;
        return string.CompareOrdinal(suffixA, suffixB) < 0 ? -1 : 1;
    }
}
