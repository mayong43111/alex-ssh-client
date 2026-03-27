using Serilog.Core;
using Serilog.Events;

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
        var line = $"{logEvent.Timestamp:HH:mm:ss} [{logEvent.Level}] {logEvent.RenderMessage()}";
        if (logEvent.Exception is not null)
        {
            line = $"{line}{Environment.NewLine}{logEvent.Exception}";
        }

        _uiLogService.Append(line);
    }
}
