namespace SSHClient.Core.Models;

public enum RuleAction
{
    Proxy,
    Direct,
    Reject
}

public sealed record ProxyRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Lower value means higher priority. Typical range: 1-9999.
    /// </summary>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// Could be domain glob, CIDR, process name, etc. (extensible)
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Optional type hint (All/DomainSuffix/IpCidr).
    /// If omitted, runtime mapping infers by pattern.
    /// </summary>
    public string? Type { get; set; }

    public RuleAction Action { get; set; } = RuleAction.Proxy;

}
