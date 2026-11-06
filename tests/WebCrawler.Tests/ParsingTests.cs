namespace WebCrawler.Tests;

public class RobotsTxtTests
{
    [Fact]
    public void EmptyFile_AllowsEverything() => Assert.True(RobotsTxt.Parse("").IsAllowed("bot", "/anything"));

    [Fact]
    public void Disallow_BlocksPrefix()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /private");
        Assert.False(r.IsAllowed("bot", "/private/data"));
        Assert.False(r.IsAllowed("bot", "/private"));
        Assert.True(r.IsAllowed("bot", "/public"));
    }

    [Fact]
    public void EmptyDisallow_AllowsAll() =>
        Assert.True(RobotsTxt.Parse("User-agent: *\nDisallow:").IsAllowed("bot", "/x"));

    [Fact]
    public void LongestMatchWins_AllowOverridesDisallow()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /docs\nAllow: /docs/public");
        Assert.False(r.IsAllowed("bot", "/docs/secret"));
        Assert.True(r.IsAllowed("bot", "/docs/public/page"));
    }

    [Fact]
    public void Tie_PrefersAllow() =>
        Assert.True(RobotsTxt.Parse("User-agent: *\nDisallow: /a\nAllow: /a").IsAllowed("bot", "/a"));

    [Fact]
    public void Wildcards_AndEndAnchor()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /*.pdf$\nDisallow: /tmp/*/cache");
        Assert.False(r.IsAllowed("bot", "/files/report.pdf"));
        Assert.True(r.IsAllowed("bot", "/files/report.pdf?download=1"));
        Assert.False(r.IsAllowed("bot", "/tmp/42/cache/x"));
        Assert.True(r.IsAllowed("bot", "/tmp/cache"));
    }

    [Fact]
    public void SpecificAgentGroup_BeatsWildcard()
    {
        var r = RobotsTxt.Parse("User-agent: *\nDisallow: /\n\nUser-agent: goodbot\nAllow: /\n");
        Assert.True(r.IsAllowed("GoodBot", "/page"));
        Assert.False(r.IsAllowed("otherbot", "/page"));
    }

    [Fact]
    public void ConsecutiveUserAgents_ShareRules()
    {
        var r = RobotsTxt.Parse("User-agent: a\nUser-agent: b\nDisallow: /x");
        Assert.False(r.IsAllowed("a", "/x"));
        Assert.False(r.IsAllowed("b", "/x"));
        Assert.True(r.IsAllowed("c", "/x"));
    }

    [Fact]
    public void CommentsAndCrlf_AreHandled()
    {
        var r = RobotsTxt.Parse("# hello\r\nUser-agent: * # everyone\r\nDisallow: /no # secret\r\n");
        Assert.False(r.IsAllowed("bot", "/no"));
    }

    [Fact]
    public void CrawlDelay_IsParsed()
    {
        var r = RobotsTxt.Parse("User-agent: *\nCrawl-delay: 2.5\nDisallow: /x");
        Assert.Equal(TimeSpan.FromSeconds(2.5), r.GetCrawlDelay("bot"));
        Assert.Null(RobotsTxt.AllowAll.GetCrawlDelay("bot"));
    }

    [Fact]
    public void DisallowAll_Constant() => Assert.False(RobotsTxt.DisallowAll.IsAllowed("bot", "/"));
}

public class HtmlParsingTests
{
    private static readonly Uri Base = new("https://example.com/dir/page.html");

    [Fact]
    public void ExtractLinks_ResolvesRelativeUrls()
    {
        var html = """<a href="/abs">1</a> <a href="rel.html">2</a> <a href='../up'>3</a> <a href=bare>4</a> <a HREF="https://other.org/x">5</a>""";
        var links = HtmlParsing.ExtractLinks(html, Base).Select(u => u.AbsoluteUri).ToArray();
        Assert.Equal(new[]
        {
            "https://example.com/abs", "https://example.com/dir/rel.html", "https://example.com/up",
            "https://example.com/dir/bare", "https://other.org/x"
        }, links);
    }

    [Fact]
    public void ExtractLinks_SkipsNonHttpFragmentsNofollowAndComments()
    {
        var html = """
            <a href="mailto:a@b.c">m</a> <a href="javascript:void(0)">j</a> <a href="#top">f</a>
            <a href="/no" rel="nofollow">n</a> <!-- <a href="/commented">c</a> --> <a href="/yes">y</a> <a name="x">none</a>
            """;
        var links = HtmlParsing.ExtractLinks(html, Base);
        Assert.Equal(new[] { "https://example.com/yes" }, links.Select(u => u.AbsoluteUri));
    }

    [Fact]
    public void ExtractLinks_DecodesEntities() =>
        Assert.Equal("https://example.com/a?x=1&y=2", HtmlParsing.ExtractLinks("""<a href="/a?x=1&amp;y=2">l</a>""", Base)[0].AbsoluteUri);

    [Fact]
    public void ExtractTitle()
    {
        Assert.Equal("Hello & welcome", HtmlParsing.ExtractTitle("<html><TITLE>\n  Hello &amp;   welcome \n</TITLE>"));
        Assert.Null(HtmlParsing.ExtractTitle("<html></html>"));
    }
}

public class UrlNormalizerTests
{
    [Theory]
    [InlineData("HTTP://Example.COM/Path#frag", "http://example.com/Path")]
    [InlineData("https://example.com:443/a", "https://example.com/a")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("https://example.com:8443/a?q=1#x", "https://example.com:8443/a?q=1")]
    public void Normalize(string input, string expected) =>
        Assert.Equal(expected, UrlNormalizer.Normalize(new Uri(input)).AbsoluteUri);
}
