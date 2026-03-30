using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using SSHClient.Core.Models;

namespace SSHClient.Core.Proxy;

public enum RuleMatchType
{
    DomainSuffix,
    DomainKeyword,
    IpCidr,
    ProcessName,
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
    ProxyRuleEx? Match(string host, int port, string? processName = null, IPAddress? destIp = null);
}

public sealed class RuleEngine : IRuleEngine
{
    private readonly IList<ProxyRuleEx> _rules;

    public RuleEngine(IEnumerable<ProxyRuleEx> rules)
    {
        _rules = rules.ToList();
    }

    public ProxyRuleEx? Match(string host, int port, string? processName = null, IPAddress? destIp = null)
    {
        foreach (var rule in _rules)
        {
            if (IsMatch(rule, host, port, processName, destIp))
            {
                return rule;
            }
        }

        return null;
    }

    private static bool IsMatch(ProxyRuleEx rule, string host, int port, string? processName, IPAddress? destIp)
    {
        switch (rule.Type)
        {
            case RuleMatchType.All:
                return true;
            case RuleMatchType.Port:
                return rule.Port == port;
            case RuleMatchType.ProcessName:
                return processName is not null && rule.Pattern.Equals(processName, StringComparison.OrdinalIgnoreCase);
            case RuleMatchType.DomainSuffix:
                return IsDomainMatch(rule, host);
            case RuleMatchType.DomainKeyword:
                return IsDomainMatch(rule, host);
            case RuleMatchType.IpCidr:
                var targetIp = destIp;
                if (targetIp is null && IPAddress.TryParse(host, out var parsedIp))
                {
                    targetIp = parsedIp;
                }

                if (targetIp is null)
                {
                    return false;
                }

                return CheckCidr(targetIp, rule.Pattern ?? rule.Cidr ?? string.Empty);
            default:
                return false;
        }
    }

    private static bool IsDomainMatch(ProxyRuleEx rule, string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalizedHost = host.Trim().TrimEnd('.');
        if (normalizedHost.Length == 0)
        {
            return false;
        }

        var patterns = SplitDomainPatterns(rule.Pattern);
        if (patterns.Count == 0)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (pattern == "*")
            {
                return true;
            }

            // Treat leading wildcard as both root and subdomain match for better UX.
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var baseDomain = pattern[2..];
                if (normalizedHost.Equals(baseDomain, StringComparison.OrdinalIgnoreCase)
                    || normalizedHost.EndsWith('.' + baseDomain, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (pattern.Contains('*'))
            {
                var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                if (Regex.IsMatch(normalizedHost, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    return true;
                }

                continue;
            }

            if (rule.Type == RuleMatchType.DomainKeyword && normalizedHost.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (normalizedHost.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                || normalizedHost.EndsWith('.' + pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static bool CheckCidr(IPAddress ip, string cidr)
    {
        // simple IPv4 CIDR check
        var parts = cidr.Split('/');
        if (parts.Length != 2) return false;
        if (!IPAddress.TryParse(parts[0], out var network)) return false;
        if (!int.TryParse(parts[1], out var prefix)) return false;
        var ipBytes = ip.GetAddressBytes();
        var netBytes = network.GetAddressBytes();
        if (ipBytes.Length != netBytes.Length) return false;

        int bytes = prefix / 8;
        int bits = prefix % 8;

        for (int i = 0; i < bytes; i++)
        {
            if (ipBytes[i] != netBytes[i]) return false;
        }
        if (bits == 0) return true;
        int mask = (byte)~(0xFF >> bits);
        return (ipBytes[bytes] & mask) == (netBytes[bytes] & mask);
    }
}
