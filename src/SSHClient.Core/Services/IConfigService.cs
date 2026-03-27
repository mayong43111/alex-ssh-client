using SSHClient.Core.Configuration;

namespace SSHClient.Core.Services;

public interface IConfigService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
