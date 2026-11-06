using System.Globalization;
using System.Text.RegularExpressions;

namespace WebCrawler;

/// <summary>
/// Parser and matcher for robots.txt (RFC 9309 semantics): most specific user-agent group wins, the longest matching
/// rule wins, Allow beats Disallow on ties, and patterns support <c>*</c> and a trailing <c>$</c>.
/// </summary>
public sealed class RobotsTxt
{
    private sealed record Rule(bool Allow, string Pattern, Regex Matcher);

    private sealed class Group
    {
        public List<string> Agents { get; } = new();
        public List<Rule> Rules { get; } = new();
        public TimeSpan? CrawlDelay { get; set; }
    }

    private readonly List<Group> _groups;

    private RobotsTxt(List<Group> groups) => _groups = groups;

    public static RobotsTxt AllowAll { get; } = new(new List<Group>());

    public static RobotsTxt DisallowAll { get; } = Parse("User-agent: *\nDisallow: /");

    public static RobotsTxt Parse(string content)
    {
        var groups = new List<Group>();
        Group? current = null;
        var currentHasRules = false;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var field = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            switch (field)
            {
                case "user-agent":
                    if (current is null || currentHasRules)
                    {
                        current = new Group();
                        groups.Add(current);
                        currentHasRules = false;
                    }
                    current.Agents.Add(value.ToLowerInvariant());
                    break;
                case "allow" or "disallow" when current is not null:
                    currentHasRules = true;
                    if (value.Length > 0) current.Rules.Add(new Rule(field == "allow", value, ToRegex(value)));
                    break;
                case "crawl-delay" when current is not null:
                    currentHasRules = true;
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs) && secs >= 0)
                        current.CrawlDelay = TimeSpan.FromSeconds(secs);
                    break;
            }
        }
        return new RobotsTxt(groups);
    }

    /// <param name="userAgentToken">Product token such as "mybot" (without version).</param>
    /// <param name="pathAndQuery">Path plus query string, starting with '/'.</param>
    public bool IsAllowed(string userAgentToken, string pathAndQuery)
    {
        var group = SelectGroup(userAgentToken);
        if (group is null) return true;
        Rule? best = null;
        foreach (var rule in group.Rules)
        {
            if (!rule.Matcher.IsMatch(pathAndQuery)) continue;
            if (best is null || rule.Pattern.Length > best.Pattern.Length ||
                (rule.Pattern.Length == best.Pattern.Length && rule.Allow && !best.Allow))
                best = rule;
        }
        return best?.Allow ?? true;
    }

    public TimeSpan? GetCrawlDelay(string userAgentToken) => SelectGroup(userAgentToken)?.CrawlDelay;

    private Group? SelectGroup(string token)
    {
        token = token.ToLowerInvariant();
        Group? best = null;
        var bestLength = -1;
        foreach (var g in _groups)
            foreach (var agent in g.Agents)
            {
                if (agent == "*") { if (bestLength < 0) { best = g; bestLength = 0; } continue; }
                if (token.Contains(agent, StringComparison.Ordinal) && agent.Length > bestLength) { best = g; bestLength = agent.Length; }
            }
        return best;
    }

    private static Regex ToRegex(string pattern)
    {
        var anchored = pattern.EndsWith('$');
        if (anchored) pattern = pattern[..^1];
        var body = string.Join(".*", pattern.Split('*').Select(Regex.Escape));
        return new Regex("^" + body + (anchored ? "$" : ""), RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
