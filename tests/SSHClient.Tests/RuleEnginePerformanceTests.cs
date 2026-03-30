using FluentAssertions;
using SSHClient.Core.Models;
using SSHClient.Core.Proxy;
using System.Diagnostics;

namespace SSHClient.Tests;

public class RuleEnginePerformanceTests
{
    [Fact]
    public void Match_PerformanceBaseline_Should_Handle_HighVolume_Lookups()
    {
        var rules = BuildRules(180);
        var engine = new RuleEngine(rules);

        var hosts = new[]
        {
            "api.example.com",
            "service.internal.local",
            "10.8.12.34",
            "cdn.contoso.net",
            "www.doodles.google",
        };

        // Warm up JIT and regex caches.
        for (var i = 0; i < 5_000; i++)
        {
            _ = engine.Match(hosts[i % hosts.Length], 443);
        }

        const int total = 100_000;
        var matched = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < total; i++)
        {
            if (engine.Match(hosts[i % hosts.Length], 443) is not null)
            {
                matched++;
            }
        }
        sw.Stop();

        matched.Should().Be(total);

        // Baseline guardrail: prevent large regressions on regular dev machines.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Match_Should_Ignore_Rules_After_First_All_Rule()
    {
        var rules = new[]
        {
            new ProxyRuleEx { Name = "AllFirst", Type = RuleMatchType.All, Pattern = "*", Action = RuleAction.Direct },
            new ProxyRuleEx { Name = "LaterRule", Type = RuleMatchType.DomainSuffix, Pattern = "*.example.com", Action = RuleAction.Proxy },
        };

        var engine = new RuleEngine(rules);
        var match = engine.Match("api.example.com", 443);

        match.Should().NotBeNull();
        match!.Name.Should().Be("AllFirst");
        match.Action.Should().Be(RuleAction.Direct);
    }

    private static IReadOnlyList<ProxyRuleEx> BuildRules(int ruleCount)
    {
        var list = new List<ProxyRuleEx>(ruleCount + 1);

        for (var i = 0; i < ruleCount; i++)
        {
            list.Add(new ProxyRuleEx
            {
                Name = $"Rule-{i}",
                Type = (i % 3) switch
                {
                    0 => RuleMatchType.DomainSuffix,
                    1 => RuleMatchType.IpCidr,
                    _ => RuleMatchType.DomainSuffix,
                },
                Pattern = (i % 3) switch
                {
                    0 => "*.example.com",
                    1 => "10.0.0.0/8",
                    _ => "*.contoso.net",
                },
                Action = RuleAction.Proxy,
            });
        }

        list.Add(new ProxyRuleEx
        {
            Name = "Fallback",
            Type = RuleMatchType.All,
            Pattern = "*",
            Action = RuleAction.Direct,
        });

        return list;
    }
}
