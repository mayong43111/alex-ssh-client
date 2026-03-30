namespace SSHClient.Core.Configuration;

public static class AppConfigPaths
{
    private const string AppDirectoryName = "AlexSSHClient";
    private const string ConfigFileName = "appsettings.json";

    public static string GetAppDataDirectory()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, AppDirectoryName);
    }

    public static string GetUserConfigPath()
    {
        return Path.Combine(GetAppDataDirectory(), ConfigFileName);
    }

    public static string GetPackagedConfigPath()
    {
        return Path.Combine(AppContext.BaseDirectory, ConfigFileName);
    }
}
