using System.Net;
using System.Text.RegularExpressions;

namespace WebCrawler;

/// <summary>Minimal, dependency-free HTML helpers: enough for link discovery, not a full parser.</summary>
public static partial class HtmlParsing
{
    [GeneratedRegex(@"<a\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnchorTag();

    [GeneratedRegex(@"\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefAttr();

    [GeneratedRegex(@"\brel\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelAttr();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    /// <summary>Returns absolute http(s) links found in anchors, resolved against <paramref name="baseUri"/>. Skips rel=nofollow.</summary>
    public static IReadOnlyList<Uri> ExtractLinks(string html, Uri baseUri)
    {
        var links = new List<Uri>();
        foreach (Match tag in AnchorTag().Matches(Comment().Replace(html, "")))
        {
            var rel = RelAttr().Match(tag.Value);
            if (rel.Success && First(rel).Contains("nofollow", StringComparison.OrdinalIgnoreCase)) continue;
            var href = HrefAttr().Match(tag.Value);
            if (!href.Success) continue;
            var value = WebUtility.HtmlDecode(First(href)).Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            if (!Uri.TryCreate(baseUri, value, out var resolved)) continue;
            if (resolved.Scheme is not ("http" or "https")) continue;
            links.Add(resolved);
        }
        return links;
    }

    public static string? ExtractTitle(string html)
    {
        var m = TitleTag().Match(html);
        if (!m.Success) return null;
        var text = Regex.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    private static string First(Match m) =>
        m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
}
