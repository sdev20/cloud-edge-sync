using SyncService.Domain;

namespace SyncService.Infrastructure.Client.Core;

public interface ISyncToInstrument
{
    Task<bool> SendInstrumentAsync(Instrument instrument, CancellationToken cancellationToken = default);

    Task<bool> SendInstrumentsAsync(IReadOnlyCollection<Instrument> instruments, CancellationToken cancellationToken = default);
}
