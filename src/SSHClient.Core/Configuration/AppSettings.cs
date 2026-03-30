using SSHClient.Core.Models;

namespace SSHClient.Core.Configuration;

public sealed class AppSettings
{
    public const string SectionName = "SSHClient";

    public IList<ProxyProfile> Profiles { get; set; } = new List<ProxyProfile>();

    public ProxyListenerSettings Proxy { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
}

public sealed class ProxyListenerSettings
{
    public int HttpPort { get; set; } = 8888;
    public int SocksPort { get; set; } = 1080;
    public bool EnableOnStartup { get; set; } = true;
    public bool ToggleSystemProxy { get; set; } = false;
}

public sealed class LoggingSettings
{
    public string MinimumLevel { get; set; } = "Information";
    public string LogPath { get; set; } = "logs/sshclient-.log"; // Serilog rolling file format
}
