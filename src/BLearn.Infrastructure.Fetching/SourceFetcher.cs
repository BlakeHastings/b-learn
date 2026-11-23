using System.Net;
using System.Text.RegularExpressions;
using BLearn.Application;

namespace BLearn.Infrastructure.Fetching;

/// <summary>Fetches http(s) URLs and local files, returning readable text for quote matching.</summary>
public sealed class SourceFetcher(HttpClient http) : ISourceFetcher
{
    public const long MaxBytes = 10 * 1024 * 1024;

    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("b-learn/0.1 (+evidence verification)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,text/plain,application/xhtml+xml;q=0.9,*/*;q=0.5");
        return client;
    }

    public async Task<FetchedSource> FetchAsync(string locator, CancellationToken ct = default)
    {
        if (Uri.TryCreate(locator, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "text/plain";
            if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException($"Source is larger than {MaxBytes / 1024 / 1024} MB.");
            if (mediaType == "application/pdf") throw new ArgumentException("PDF sources are not supported yet. Capture the text through the harness instead.");
            var body = await response.Content.ReadAsStringAsync(ct);
            return new FetchedSource(ToText(body, mediaType), mediaType, response.RequestMessage?.RequestUri?.ToString() ?? locator);
        }

        var path = uri is { IsFile: true } ? uri.LocalPath : locator;
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new IOException($"File not found: {path}");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") throw new ArgumentException("PDF sources are not supported yet. Capture the text through the harness instead.");
        var media = ext switch { ".html" or ".htm" => "text/html", ".md" => "text/markdown", ".json" => "application/json", _ => "text/plain" };
        return new FetchedSource(ToText(await File.ReadAllTextAsync(path, ct), media), media, path);
    }

    public static string ToText(string body, string mediaType) =>
        mediaType is "text/html" or "application/xhtml+xml" ? HtmlText.Extract(body) : body;
}

/// <summary>Reduces HTML to its visible text. Good enough for verbatim quote matching, not a renderer.</summary>
public static partial class HtmlText
{
    [GeneratedRegex(@"<(script|style|noscript|svg|template|head)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"</?(p|div|br|li|ul|ol|h[1-6]|tr|td|th|table|section|article|pre|blockquote|dd|dt)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();

    public static string Extract(string html)
    {
        var s = Comments().Replace(html, " ");
        s = Hidden().Replace(s, " ");
        s = Blocks().Replace(s, "\n");
        s = Tags().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = Spaces().Replace(s, " ");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }
}
