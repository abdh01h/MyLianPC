using System.Net;
using System.Text.RegularExpressions;

namespace MyLianPC.Core.Drivers;

/// <summary>
/// Reads the download list out of GIGABYTE's server-rendered support page. The page is plain HTML (one
/// &lt;h2&gt; per category and one &lt;tr class="item-group"&gt; per release), but it also contains a hidden
/// mobile copy — only the table rows are read, so nothing is counted twice.
/// </summary>
public static partial class GigabyteSupportParser
{
    private static readonly string[] CategoryOrder = ["Chipset", "Audio", "LAN", "WLAN+BT", "SATA RAID/AHCI"];

    [GeneratedRegex(@"<h2>(.*?)</h2>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<tr class=""item-group"">(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Row();

    [GeneratedRegex(@"<td class=""item-version""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex VersionCell();

    [GeneratedRegex(@"<td class=""item-os""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex OsCell();

    [GeneratedRegex(@"<td class=""item-size""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SizeCell();

    [GeneratedRegex(@"<td class=""item-date""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DateCell();

    [GeneratedRegex(@"<span class=""support-item-desc"">(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DescriptionSpan();

    [GeneratedRegex(@"<li[^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"href=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static SupportCatalog Parse(string html, string sourceUrl, string board, DateTimeOffset fetchedUtc)
    {
        var bios = new List<BiosEntry>();
        var drivers = new List<DriverEntry>();

        var headings = Heading().Matches(html);
        for (int i = 0; i < headings.Count; i++)
        {
            string category = Text(headings[i].Groups[1].Value);
            if (category.Length == 0) continue;
            int start = headings[i].Index + headings[i].Length;
            int end = i + 1 < headings.Count ? headings[i + 1].Index : html.Length;
            string section = html[start..end];

            if (category.Equals("BIOS", StringComparison.OrdinalIgnoreCase)) ParseBios(section, bios);
            else ParseDrivers(category, section, drivers);
        }

        if (bios.Count == 0 && drivers.Count == 0)
            throw new InvalidDataException("The GIGABYTE support page did not contain a BIOS or driver list. The page layout may have changed.");

        return new SupportCatalog
        {
            SourceUrl = sourceUrl,
            Board = board,
            FetchedUtc = fetchedUtc,
            Bios = bios.OrderByDescending(b => b, Comparer<BiosEntry>.Create((a, b) => Versions.CompareBios(a.Version, b.Version) ?? 0)).ToList(),
            Drivers = drivers
                .GroupBy(d => (d.Category, d.Name))
                .Select(g => g.Aggregate(Newest))
                .OrderBy(d => IndexOfCategory(d.Category))
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
        };
    }

    private static void ParseBios(string section, List<BiosEntry> into)
    {
        foreach (Match row in Row().Matches(section))
        {
            string version = Cell(VersionCell(), row.Groups[1].Value);
            string url = Url(row.Groups[1].Value);
            if (version.Length == 0 || url.Length == 0) continue;

            var notes = ListItem().Matches(row.Groups[1].Value)
                .Select(m => Text(m.Groups[1].Value))
                .Where(n => n.Length > 0)
                .ToList();
            if (notes.Count == 0)
            {
                string description = Text(DescriptionSpan().Match(row.Groups[1].Value).Groups[1].Value);
                if (description.Length > 0) notes.Add(description);
            }

            into.Add(new BiosEntry(version, Cell(DateCell(), row.Groups[1].Value), Cell(SizeCell(), row.Groups[1].Value), url, notes));
        }
    }

    private static void ParseDrivers(string category, string section, List<DriverEntry> into)
    {
        foreach (Match row in Row().Matches(section))
        {
            string body = row.Groups[1].Value;
            string name = Text(DescriptionSpan().Match(body).Groups[1].Value);
            string version = Cell(VersionCell(), body);
            string url = Url(body);
            if (name.Length == 0 || version.Length == 0 || url.Length == 0) continue;

            // "AMD RAID Preinstall Driver (Note) Windows setup…" — the note is about Windows Setup, not the name.
            int note = name.IndexOf("(Note)", StringComparison.OrdinalIgnoreCase);
            if (note > 0) name = name[..note].Trim();

            var os = OsCell().Match(body) is { Success: true } osMatch ? SplitOs(osMatch.Groups[1].Value) : [];
            // The app runs on Windows 11; Windows 10-only packages are of no use and would only add noise.
            if (os.Count > 0 && !os.Any(o => o.Contains("Windows 11", StringComparison.OrdinalIgnoreCase))) continue;

            into.Add(new DriverEntry(category, name, version, Cell(DateCell(), body), Cell(SizeCell(), body), os, url));
        }
    }

    private static DriverEntry Newest(DriverEntry a, DriverEntry b)
    {
        int? cmp = Versions.CompareNumeric(a.Version, b.Version);
        if (cmp is { } c) return c >= 0 ? a : b;
        return DateValue(a.Date) >= DateValue(b.Date) ? a : b;
    }

    private static DateTime DateValue(string? text) =>
        DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : DateTime.MinValue;

    private static int IndexOfCategory(string category)
    {
        int i = Array.FindIndex(CategoryOrder, c => c.Equals(category, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : CategoryOrder.Length;
    }

    private static string Cell(Regex regex, string body) => Text(regex.Match(body).Groups[1].Value);

    private static string Url(string body)
    {
        var match = Href().Match(body);
        string url = WebUtility.HtmlDecode(match.Groups[1].Value);
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "";
    }

    private static IReadOnlyList<string> SplitOs(string cell)
    {
        string text = WebUtility.HtmlDecode(Regex.Replace(cell, "<[^>]+>", m => m.Value.Contains("br", StringComparison.OrdinalIgnoreCase) ? "\n" : " "));
        return text.Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Tags out, entities in, whitespace collapsed — enough for names, versions and short notes.</summary>
    private static string Text(string fragment)
    {
        string spaced = Regex.Replace(fragment, @"<(br|/li|/p|/div)[^>]*>", " ", RegexOptions.IgnoreCase);
        string tagsOut = Regex.Replace(spaced, "<[^>]+>", "");
        return Spaces().Replace(WebUtility.HtmlDecode(tagsOut), " ").Trim();
    }
}
