namespace SSHClient.App.Logging;

public interface IUiLogService
{
    event EventHandler<string>? SnapshotChanged;

    string GetSnapshot();

    void Append(string entry);

    void Clear();
}
