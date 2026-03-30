using System.Net;
using System.Text.RegularExpressions;
using SSHClient.Core.Models;

namespace SSHClient.Core.Proxy;

public enum RuleMatchType
{
    DomainSuffix,
    IpCidr,
    Port,
    All
}

public sealed record ProxyRuleEx 
{
    public string Name { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public RuleAction Action { get; init; } = RuleAction.Proxy;
    public string? Profile { get; init; }
    public RuleMatchType Type { get; init; } = RuleMatchType.All;
    public string? Cidr { get; init; }
    public int? Port { get; init; }
}

public interface IRuleEngine
{
    ProxyRuleEx? Match(string host, int port, IPAddress? destIp = null);
}

public sealed class RuleEngine : IRuleEngine
{
    private readonly List<CompiledRule> _compiledRules;

    public RuleEngine(IEnumerable<ProxyRuleEx> rules)
    {
        _compiledRules = BuildCompiledRules(rules);
    }

    public ProxyRuleEx? Match(string host, int port, IPAddress? destIp = null)
    {
        var normalizedHost = NormalizeHost(host);
        IPAddress? parsedHostIp = null;
        if (!string.IsNullOrEmpty(normalizedHost) && IPAddress.TryParse(normalizedHost, out var hostIp))
        {
            parsedHostIp = hostIp;
        }

        var ctx = new MatchContext(
            NormalizedHost: normalizedHost,
            Port: port,
            DestIp: destIp,
            ParsedHostIp: parsedHostIp);

        foreach (var compiledRule in _compiledRules)
        {
            if (compiledRule.IsMatch(ctx))
            {
                return compiledRule.Rule;
            }
        }

        return null;
    }

    private static List<CompiledRule> BuildCompiledRules(IEnumerable<ProxyRuleEx> rules)
    {
        var compiled = new List<CompiledRule>();
        foreach (var rule in rules)
        {
            compiled.Add(new CompiledRule(rule, BuildMatcher(rule)));

            // RuleAction-independent short-circuit: first All rule matches any request,
            // so all subsequent rules are unreachable under first-match semantics.
            if (rule.Type == RuleMatchType.All)
            {
                break;
            }
        }

        return compiled;
    }

    private static Func<MatchContext, bool> BuildMatcher(ProxyRuleEx rule)
    {
        return rule.Type switch
        {
            RuleMatchType.All => _ => true,
            RuleMatchType.Port => BuildPortMatcher(rule),
            RuleMatchType.DomainSuffix => BuildDomainMatcher(rule.Pattern),
            RuleMatchType.IpCidr => BuildIpCidrMatcher(rule),
            _ => _ => false,
        };
    }

    private static Func<MatchContext, bool> BuildPortMatcher(ProxyRuleEx rule)
    {
        var targetPort = rule.Port;
        if (!targetPort.HasValue && int.TryParse(rule.Pattern, out var parsedPort))
        {
            targetPort = parsedPort;
        }

        if (!targetPort.HasValue)
        {
            return _ => false;
        }

        return ctx => ctx.Port == targetPort.Value;
    }

    private static Func<MatchContext, bool> BuildIpCidrMatcher(ProxyRuleEx rule)
    {
        var cidr = string.IsNullOrWhiteSpace(rule.Pattern) ? rule.Cidr : rule.Pattern;
        if (!TryBuildCidrMatcher(cidr ?? string.Empty, out var matcher))
        {
            return _ => false;
        }

        return ctx =>
        {
            var targetIp = ctx.DestIp ?? ctx.ParsedHostIp;
            return targetIp is not null && matcher.IsMatch(targetIp);
        };
    }

    private static Func<MatchContext, bool> BuildDomainMatcher(string? pattern)
    {
        var patterns = SplitDomainPatterns(pattern);
        if (patterns.Count == 0)
        {
            return _ => false;
        }

        var compiledPatternMatchers = new List<Func<string, bool>>();
        foreach (var p in patterns)
        {
            if (p == "*")
            {
                compiledPatternMatchers.Add(_ => true);
                continue;
            }

            if (p.StartsWith("*.", StringComparison.Ordinal))
            {
                var baseDomain = p[2..];
                if (baseDomain.Length == 0)
                {
                    continue;
                }

                compiledPatternMatchers.Add(host =>
                    host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith('.' + baseDomain, StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (p.Contains('*'))
            {
                var regex = new Regex(
                    "^" + Regex.Escape(p).Replace("\\*", ".*") + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                compiledPatternMatchers.Add(host => regex.IsMatch(host));
                continue;
            }

            compiledPatternMatchers.Add(host =>
                host.Equals(p, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith('.' + p, StringComparison.OrdinalIgnoreCase));
        }

        if (compiledPatternMatchers.Count == 0)
        {
            return _ => false;
        }

        return ctx =>
        {
            if (string.IsNullOrEmpty(ctx.NormalizedHost))
            {
                return false;
            }

            foreach (var matchPattern in compiledPatternMatchers)
            {
                if (matchPattern(ctx.NormalizedHost))
                {
                    return true;
                }
            }

            return false;
        };
    }

    private static string NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return string.Empty;
        }

        return host.Trim().TrimEnd('.');
    }

    private static List<string> SplitDomainPatterns(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new List<string>();
        }

        return pattern
            .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static bool TryBuildCidrMatcher(string cidr, out CidrMatcher matcher)
    {
        matcher = default;

        var parts = cidr.Split('/');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var network))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var networkBytes = network.GetAddressBytes();
        var maxPrefix = networkBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            return false;
        }

        matcher = new CidrMatcher(networkBytes, prefixLength);
        return true;
    }

    private readonly record struct MatchContext(
        string NormalizedHost,
        int Port,
        IPAddress? DestIp,
        IPAddress? ParsedHostIp);

    private sealed record CompiledRule(ProxyRuleEx Rule, Func<MatchContext, bool> IsMatch);

    private readonly record struct CidrMatcher(byte[] NetworkBytes, int PrefixLength)
    {
        public bool IsMatch(IPAddress ip)
        {
            var ipBytes = ip.GetAddressBytes();
            if (ipBytes.Length != NetworkBytes.Length)
            {
                return false;
            }

            var fullBytes = PrefixLength / 8;
            var remainingBits = PrefixLength % 8;

            for (var i = 0; i < fullBytes; i++)
            {
                if (ipBytes[i] != NetworkBytes[i])
                {
                    return false;
                }
            }

            if (remainingBits == 0)
            {
                return true;
            }

            var mask = (byte)~(0xFF >> remainingBits);
            return (ipBytes[fullBytes] & mask) == (NetworkBytes[fullBytes] & mask);
        }
    }
}
