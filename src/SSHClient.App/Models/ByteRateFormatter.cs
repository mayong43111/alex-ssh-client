namespace SSHClient.App.Models;

public static class ByteRateFormatter
{
    public static string Format(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_048_576)
            return $"{bytesPerSecond / 1_048_576:F1} MB/s";
        if (bytesPerSecond >= 1024)
            return $"{bytesPerSecond / 1024:F1} KB/s";
        return $"{bytesPerSecond:F0} B/s";
    }

    public static string FormatTotal(long bytes)
    {
        if (bytes >= 1_073_741_824)
            return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)
            return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
