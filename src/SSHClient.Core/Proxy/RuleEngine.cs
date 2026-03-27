using System.Net;
using System.Net.Sockets;
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
                return host.EndsWith(rule.Pattern, StringComparison.OrdinalIgnoreCase);
            case RuleMatchType.DomainKeyword:
                return host.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase);
            case RuleMatchType.IpCidr:
                if (destIp is null) return false;
                return CheckCidr(destIp, rule.Pattern ?? rule.Cidr ?? string.Empty);
            default:
                return false;
        }
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
