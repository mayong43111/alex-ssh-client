using FluentAssertions;
using SSHClient.Core.Models;
using SSHClient.Core.Proxy;

namespace SSHClient.Tests;

[Trait("Category", "CriticalPath")]
public class RuleEngineTests
{
    [Fact]
    public void Match_Should_Support_MultiDomain_And_Wildcard_Patterns()
    {
        var engine = new RuleEngine(new[]
        {
            new ProxyRuleEx
            {
                Name = "DomainSet",
                Type = RuleMatchType.DomainSuffix,
                Pattern = "example.com;*.corp.local",
                Action = RuleAction.Proxy,
            }
        });

        var m1 = engine.Match("api.example.com", 443);
        var m2 = engine.Match("gateway.corp.local", 443);

        m1.Should().NotBeNull();
        m1!.Name.Should().Be("DomainSet");
        m2.Should().NotBeNull();
        m2!.Name.Should().Be("DomainSet");
    }

    [Fact]
    public void Match_Should_Treat_Leading_Wildcard_As_Root_And_Subdomain()
    {
        var engine = new RuleEngine(new[]
        {
            new ProxyRuleEx
            {
                Name = "GoogleDoodles",
                Type = RuleMatchType.DomainSuffix,
                Pattern = "*.doodles.google",
                Action = RuleAction.Proxy,
            }
        });

        var root = engine.Match("doodles.google", 443);
        var sub = engine.Match("www.doodles.google", 443);

        root.Should().NotBeNull();
        root!.Name.Should().Be("GoogleDoodles");
        sub.Should().NotBeNull();
        sub!.Name.Should().Be("GoogleDoodles");
    }

    [Fact]
    public void Match_Should_Handle_Host_With_Trailing_Dot()
    {
        var engine = new RuleEngine(new[]
        {
            new ProxyRuleEx
            {
                Name = "GoogleDoodles",
                Type = RuleMatchType.DomainSuffix,
                Pattern = "*.doodles.google",
                Action = RuleAction.Proxy,
            }
        });

        var match = engine.Match("doodles.google.", 443);

        match.Should().NotBeNull();
        match!.Name.Should().Be("GoogleDoodles");
    }

    [Fact]
    public void Match_Should_Support_IpCidr_For_IpHost()
    {
        var engine = new RuleEngine(new[]
        {
            new ProxyRuleEx
            {
                Name = "CidrRule",
                Type = RuleMatchType.IpCidr,
                Pattern = "10.0.0.0/8",
                Action = RuleAction.Direct,
            }
        });

        var match = engine.Match("10.1.2.3", 80);
        match.Should().NotBeNull();
        match!.Name.Should().Be("CidrRule");
    }

    [Fact]
    public void Match_Should_Return_First_Matched_Rule_By_Input_Order()
    {
        var rules = new[]
        {
            new ProxyRuleEx { Name = "First", Type = RuleMatchType.All, Pattern = "*", Action = RuleAction.Direct },
            new ProxyRuleEx { Name = "Second", Type = RuleMatchType.All, Pattern = "*", Action = RuleAction.Proxy },
        };

        var engine = new RuleEngine(rules);
        var match = engine.Match("any.host", 1234);

        match.Should().NotBeNull();
        match!.Name.Should().Be("First");
        match.Action.Should().Be(RuleAction.Direct);
    }

    [Fact]
    public void Match_Should_Return_Null_When_No_Rules_Match()
    {
        var engine = new RuleEngine(new[]
        {
            new ProxyRuleEx { Name = "CIDR", Type = RuleMatchType.IpCidr, Pattern = "10.0.0.0/8" }
        });

        var match = engine.Match("example.com", 443);
        match.Should().BeNull();
    }
}
