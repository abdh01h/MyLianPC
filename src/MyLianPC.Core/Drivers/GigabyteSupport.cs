using System.Net;
using System.Net.Http.Headers;

namespace MyLianPC.Core.Drivers;

/// <summary>
/// Fetches the board's public support page. This is the only network request the app makes on its own, it only
/// happens when the user asks the Drivers page to check (or the page opens and the cache is a day old), and it
/// sends nothing but the request itself — no machine identifiers, no telemetry.
/// </summary>
public static class GigabyteSupport
{
    public const string BoardName = "GIGABYTE B850 AORUS ELITE WIFI7 (rev. 1.x)";
    public const string SupportUrl = "https://www.gigabyte.com/Motherboard/B850-AORUS-ELITE-WIFI7-rev-1x/support";

    /// <summary>
    /// A browser-like request. GIGABYTE's CDN rejects clients without the usual navigation headers with HTTP
    /// 403, so the plain .NET defaults are not enough.
    /// </summary>
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml", 0.9));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        return client;
    }

    public static async Task<SupportCatalog> FetchAsync(CancellationToken stop = default)
    {
        using var response = await Http.GetAsync(SupportUrl, HttpCompletionOption.ResponseHeadersRead, stop).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string hint = response.StatusCode == HttpStatusCode.Forbidden
                ? " GIGABYTE's site refused the request; open the support page in a browser instead."
                : "";
            throw new InvalidOperationException($"GIGABYTE's support page returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}.{hint}");
        }
        string html = await response.Content.ReadAsStringAsync(stop).ConfigureAwait(false);
        return GigabyteSupportParser.Parse(html, SupportUrl, BoardName, DateTimeOffset.UtcNow);
    }
}
