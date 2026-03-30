namespace SSHClient.Core.Models;

public enum SshAuthMethod
{
    Password,
    PrivateKey,
    KeyboardInteractive,
}

public sealed record ProxyProfile
{
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string Username { get; init; } = string.Empty;
    public string? Password { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string? PrivateKeyPassphrase { get; init; }
    public SshAuthMethod AuthMethod { get; init; } = SshAuthMethod.PrivateKey;

    /// <summary>
    /// When set, a SOCKS5 proxy will be exposed locally on this port.
    /// </summary>
    public string LocalListenAddress { get; init; } = "127.0.0.1";

    /// <summary>
    /// When set, a SOCKS5 proxy will be exposed locally on this port.
    /// </summary>
    public int LocalSocksPort { get; init; } = 1080;

    /// <summary>
    /// Rules scoped to this profile only.
    /// </summary>
    public IList<ProxyRule> Rules { get; init; } = new List<ProxyRule>();
}
