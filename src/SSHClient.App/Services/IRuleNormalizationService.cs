using SSHClient.Core.Models;

namespace SSHClient.App.Services;

public interface IRuleNormalizationService
{
    int DefaultRulePriority { get; }
    bool IsDefaultRule(ProxyRule? rule);
    ProxyRule CreateDefaultRule(RuleAction action);
    List<ProxyRule> NormalizeRules(IEnumerable<ProxyRule> rules);
    string NormalizeRuleType(string? type, string? pattern);
    int GetNextRulePriority(IEnumerable<ProxyRule> rules);
}

public sealed class RuleNormalizationService : IRuleNormalizationService
{
    private const string DefaultRuleName = "默认";
    public int DefaultRulePriority => 9999;

    public bool IsDefaultRule(ProxyRule? rule)
    {
        return rule is not null && string.Equals(rule.Name, DefaultRuleName, StringComparison.Ordinal);
    }

    public ProxyRule CreateDefaultRule(RuleAction action)
    {
        return new ProxyRule
        {
            Name = DefaultRuleName,
            Priority = DefaultRulePriority,
            Pattern = "*",
            Type = "All",
            Action = action,
        };
    }

    public List<ProxyRule> NormalizeRules(IEnumerable<ProxyRule> rules)
    {
        var input = rules.ToList();
        var defaultAction = input.FirstOrDefault(IsDefaultRule)?.Action ?? RuleAction.Direct;

        var normalized = input
            .Where(r => !IsDefaultRule(r))
            .Select(r => new ProxyRule
            {
                Name = r.Name,
                Priority = Math.Clamp(r.Priority <= 0 ? 100 : r.Priority, 1, DefaultRulePriority - 1),
                Pattern = r.Pattern,
                Type = NormalizeRuleType(r.Type, r.Pattern),
                Action = r.Action,
            })
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        normalized.Add(CreateDefaultRule(defaultAction));
        return normalized;
    }

    public string NormalizeRuleType(string? type, string? pattern)
    {
        if (string.Equals(type, "All", StringComparison.OrdinalIgnoreCase))
        {
            return "All";
        }

        if (string.Equals(type, "IpCidr", StringComparison.OrdinalIgnoreCase))
        {
            return "IpCidr";
        }

        if (!string.IsNullOrWhiteSpace(type) && string.Equals(type, "DomainSuffix", StringComparison.OrdinalIgnoreCase))
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

    public int GetNextRulePriority(IEnumerable<ProxyRule> rules)
    {
        var max = rules
            .Where(r => !IsDefaultRule(r))
            .Select(r => r.Priority)
            .DefaultIfEmpty(0)
            .Max();

        if (max <= 0)
        {
            return 10;
        }

        var stepped = ((max / 10) + 1) * 10;
        return Math.Min(stepped, DefaultRulePriority - 1);
    }
}
