using Serilog.Core;
using Serilog.Events;
using System.Net.Sockets;

namespace SSHClient.App.Logging;

public sealed class UiLogSink : ILogEventSink
{
    private readonly IUiLogService _uiLogService;

    public UiLogSink(IUiLogService uiLogService)
    {
        _uiLogService = uiLogService;
    }

    public void Emit(LogEvent logEvent)
    {
        var line = $"{logEvent.Timestamp:HH:mm:ss} [{ToZhLevel(logEvent.Level)}] {logEvent.RenderMessage()}";
        if (logEvent.Exception is not null)
        {
            line = $"{line} | {ToZhException(logEvent.Exception)}";
        }

        _uiLogService.Append(line);
    }

    private static string ToZhException(Exception exception)
    {
        var message = exception.Message;

        if (message.Contains("Permission denied (publickey)", StringComparison.OrdinalIgnoreCase))
        {
            return "SSH 公钥认证被拒绝";
        }

        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "连接超时（请检查网络与 22 端口）";
        }

        var typeName = exception.GetType().Name;
        if (typeName.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
        {
            return "认证失败";
        }

        if (typeName.Contains("Socket", StringComparison.OrdinalIgnoreCase))
        {
            if (exception is SocketException socketException)
            {
                return $"网络连接异常（错误码 {(int)socketException.SocketErrorCode}）";
            }

            return "网络连接异常（请检查网络与 22 端口）";
        }

        return "发生异常（详情见文件日志）";
    }

    private static string ToZhLevel(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => "详细",
            LogEventLevel.Debug => "调试",
            LogEventLevel.Information => "信息",
            LogEventLevel.Warning => "警告",
            LogEventLevel.Error => "错误",
            LogEventLevel.Fatal => "致命",
            _ => level.ToString(),
        };
    }
}
