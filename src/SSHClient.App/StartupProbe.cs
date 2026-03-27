using System.Text;
using System.IO;

namespace SSHClient.App;

internal static class StartupProbe
{
    private static readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sshclient-startup.log");
    private static readonly object _lock = new();

    public static void Log(string message)
    {
        var line = $"[{DateTime.Now:O}] {message}";
        lock (_lock)
        {
            System.IO.File.AppendAllLines(_path, new[] { line }, Encoding.UTF8);
        }
        // Also write to Debug for IDE
        System.Diagnostics.Debug.WriteLine(line);
    }
}
