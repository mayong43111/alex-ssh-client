namespace SSHClient.App.ViewModels;

public static class RuleTypes
{
    public static readonly string[] Options = new[]
    {
        "All",
        "DomainSuffix",
        "DomainKeyword",
        "IpCidr",
        "Port",
        "ProcessName"
    };
}
