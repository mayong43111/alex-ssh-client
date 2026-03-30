using SSHClient.Core.Models;

namespace SSHClient.Core.Configuration;

public sealed class AppSettings
{
    public const string SectionName = "SSHClient";

    public IList<ProxyProfile> Profiles { get; set; } = new List<ProxyProfile>();

    public string? ActiveProfileName { get; set; }

    public string? ActiveProfileFilePath { get; set; }

    // null: 首次最小化时询问；false: 仅最小化到任务栏；true: 最小化到托盘
    public bool? MinimizeToTray { get; set; }

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
