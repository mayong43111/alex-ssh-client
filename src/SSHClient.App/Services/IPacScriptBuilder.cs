using System.Net;
using System.Net.Sockets;
using System.Text;
using SSHClient.Core.Models;

namespace SSHClient.App.Services;

public interface IPacScriptBuilder
{
    string Build(int proxyPort, IEnumerable<ProxyRule> rules);
}

public sealed class PacScriptBuilder : IPacScriptBuilder
{
    public string Build(int proxyPort, IEnumerable<ProxyRule> rules)
    {
        if (proxyPort is <= 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(proxyPort), "代理端口必须在 1-65535 之间。");
        }

        var snapshot = (rules ?? Array.Empty<ProxyRule>())
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        return BuildPacScript(proxyPort, snapshot);
    }

    private static string BuildPacScript(int proxyPort, IReadOnlyList<ProxyRule> rules)
    {
        var proxyRoute = $"PROXY 127.0.0.1:{proxyPort}";

        var domainPatterns = new List<string>();
        var domainSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cidrRules = new List<(string Network, string Mask)>();
        var cidrSet = new HashSet<string>(StringComparer.Ordinal);
        var proxyAll = false;

        foreach (var rule in rules)
        {
            if (rule.Action != RuleAction.Proxy)
            {
                continue;
            }

            var ruleType = NormalizeRuleType(rule.Type, rule.Pattern);
            if (ruleType == "All")
            {
                proxyAll = true;
                break;
            }

            if (ruleType == "DomainSuffix")
            {
                foreach (var pattern in SplitPatterns(rule.Pattern))
                {
                    var escaped = EscapeJsString(pattern.ToLowerInvariant());
                    if (domainSet.Add(escaped))
                    {
                        domainPatterns.Add(escaped);
                    }
                }

                continue;
            }

            if (ruleType == "IpCidr")
            {
                foreach (var cidr in SplitPatterns(rule.Pattern))
                {
                    if (TryConvertIpv4Cidr(cidr, out var network, out var mask))
                    {
                        var cidrKey = network + "/" + mask;
                        if (cidrSet.Add(cidrKey))
                        {
                            cidrRules.Add((network, mask));
                        }
                    }
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("function FindProxyForURL(url, host) {");
        sb.AppendLine("    host = (host || \"\").toLowerCase();");
        sb.AppendLine("    if (host === \"\") {");
        sb.AppendLine("        return \"DIRECT\";");
        sb.AppendLine("    }");
        sb.AppendLine();

        if (proxyAll)
        {
            sb.AppendLine($"    return \"{proxyRoute}\";");
            sb.AppendLine("}");
            return sb.ToString();
        }

        if (domainPatterns.Count > 0)
        {
            sb.AppendLine("    var domainRules = [");
            for (var i = 0; i < domainPatterns.Count; i++)
            {
                var suffix = i < domainPatterns.Count - 1 ? "," : string.Empty;
                sb.AppendLine($"        \"{domainPatterns[i]}\"{suffix}");
            }

            sb.AppendLine("    ];");
            sb.AppendLine();
            sb.AppendLine("    for (var i = 0; i < domainRules.length; i++) {");
            sb.AppendLine("        if (pacMatchDomain(host, domainRules[i])) {");
            sb.AppendLine($"            return \"{proxyRoute}\";");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        if (cidrRules.Count > 0)
        {
            sb.AppendLine("    var ip = pacResolveIPv4(host);");
            sb.AppendLine("    if (ip) {");
            sb.AppendLine("        var cidrRules = [");
            for (var i = 0; i < cidrRules.Count; i++)
            {
                var rule = cidrRules[i];
                var suffix = i < cidrRules.Count - 1 ? "," : string.Empty;
                sb.AppendLine($"            {{ network: \"{rule.Network}\", mask: \"{rule.Mask}\" }}{suffix}");
            }

            sb.AppendLine("        ];");
            sb.AppendLine();
            sb.AppendLine("        for (var i = 0; i < cidrRules.length; i++) {");
            sb.AppendLine("            var rule = cidrRules[i];");
            sb.AppendLine("            if (isInNet(ip, rule.network, rule.mask)) {");
            sb.AppendLine($"                return \"{proxyRoute}\";");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("    return \"DIRECT\";");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function pacMatchDomain(host, pattern) {");
        sb.AppendLine("    if (pattern === \"*\") {");
        sb.AppendLine("        return true;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    if (pattern.indexOf(\"*\") >= 0) {");
        sb.AppendLine("        if (pattern.substring(0, 2) === \"*.\") {");
        sb.AppendLine("            var baseDomain = pattern.substring(2);");
        sb.AppendLine("            return host === baseDomain || dnsDomainIs(host, \".\" + baseDomain);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        return shExpMatch(host, pattern);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    return host === pattern || dnsDomainIs(host, \".\" + pattern);");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function pacResolveIPv4(host) {");
        sb.AppendLine("    if (pacIsIPv4Literal(host)) {");
        sb.AppendLine("        return host;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    return dnsResolve(host);");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("function pacIsIPv4Literal(host) {");
        sb.AppendLine("    var parts = host.split(\".\");");
        sb.AppendLine("    if (parts.length !== 4) {");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    for (var i = 0; i < 4; i++) {");
        sb.AppendLine("        var part = parseInt(parts[i], 10);");
        sb.AppendLine("        if (isNaN(part) || part < 0 || part > 255) {");
        sb.AppendLine("            return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    return true;");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string NormalizeRuleType(string? type, string? pattern)
    {
        if (string.Equals(type, "All", StringComparison.OrdinalIgnoreCase))
        {
            return "All";
        }

        if (string.Equals(type, "IpCidr", StringComparison.OrdinalIgnoreCase))
        {
            return "IpCidr";
        }

        if (string.Equals(type, "DomainSuffix", StringComparison.OrdinalIgnoreCase))
        {
            return "DomainSuffix";
        }

        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return "All";
        }

        if (!string.IsNullOrWhiteSpace(pattern) && pattern.Contains('/'))
        {
            return "IpCidr";
        }

        return "DomainSuffix";
    }

    private static IEnumerable<string> SplitPatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            yield break;
        }

        var values = raw
            .Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x));

        foreach (var value in values)
        {
            yield return value;
        }
    }

    private static string EscapeJsString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static bool TryConvertIpv4Cidr(string cidr, out string network, out string mask)
    {
        network = string.Empty;
        mask = string.Empty;

        var parts = cidr.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var prefix) || prefix < 0 || prefix > 32)
        {
            return false;
        }

        var ipBytes = ip.GetAddressBytes();
        var ipValue = ((uint)ipBytes[0] << 24) | ((uint)ipBytes[1] << 16) | ((uint)ipBytes[2] << 8) | ipBytes[3];
        var maskValue = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var networkValue = ipValue & maskValue;

        network = ToIpv4String(networkValue);
        mask = ToIpv4String(maskValue);
        return true;
    }

    private static string ToIpv4String(uint value)
    {
        var a = (value >> 24) & 0xFF;
        var b = (value >> 16) & 0xFF;
        var c = (value >> 8) & 0xFF;
        var d = value & 0xFF;
        return $"{a}.{b}.{c}.{d}";
    }
}
