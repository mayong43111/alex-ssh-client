namespace SSHClient.Core.Models;

public enum RuleAction
{
    Proxy,
    Direct,
    Reject
}

public sealed record ProxyRule
{
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Could be domain glob, CIDR, process name, etc. (extensible)
    /// </summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>
    /// Optional type hint (All/DomainSuffix/Port etc.).
    /// If omitted, defaults to "All" in RuleEngine adapter.
    /// </summary>
    public string? Type { get; init; }

    public RuleAction Action { get; init; } = RuleAction.Proxy;

    /// <summary>
    /// Optional profile name to use when Action == Proxy.
    /// </summary>
    public string? Profile { get; init; }
}
