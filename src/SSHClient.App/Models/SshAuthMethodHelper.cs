using SSHClient.Core.Models;

namespace SSHClient.App.Models;

public static class SshAuthMethodHelper
{
    public static readonly SshAuthMethod[] Values = (SshAuthMethod[])Enum.GetValues(typeof(SshAuthMethod));
}
