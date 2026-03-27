using SSHClient.Core.Models;

namespace SSHClient.App.Models;

public static class RuleActionHelper
{
    public static readonly RuleAction[] Values = (RuleAction[])Enum.GetValues(typeof(RuleAction));
}
