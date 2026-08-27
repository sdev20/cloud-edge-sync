using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncService.Domain;
using SyncService.DomainServices.BusinessLogic;
using SyncService.DomainServices.BusinessLogic.Core;
using SyncService.Infrastructure.Client.Core;


namespace SyncService.DomainServices;

public class InstrumentService(
    InMemoryDataStore dataStore,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<InstrumentService> logger) : IInstrumentService
{
    private const string InstrumentStatusChangedRoutingKey = "instrument.status.changed";

    public List<Instrument> GetInstruments() => dataStore.GetInstruments();

    public Instrument? GetInstrument(Guid instrumentId) => dataStore.GetInstrument(instrumentId);

    public Instrument AddInstrument(Instrument instrument) => dataStore.AddInstrument(instrument);

    public Task<Instrument?> UpdateInstrument(Instrument instrument)
    {
        var updated = dataStore.UpdateInstrument(instrument);
        if (updated is not null)
        {
            FireAndForgetSyncToInstrument(updated);
        }

        return Task.FromResult(updated);
    }

    private void FireAndForgetSyncToInstrument(Instrument instrument)
    {
        _ = Task.Run(async () =>
        {
            // New scope: the caller's request scope (and its ISyncToInstrument) may
            // already be disposed by the time this background work runs.
            using var scope = serviceScopeFactory.CreateScope();
            var syncToInstrument = scope.ServiceProvider.GetRequiredService<ISyncToInstrument>();

            try
            {
                var succeeded = await syncToInstrument.SendInstrumentAsync(instrument);
                if (succeeded)
                {
                    logger.LogInformation(
                        "Synced instrument {InstrumentId} to instrument endpoint", instrument.InstrumentId);
                }
                else
                {
                    logger.LogWarning(
                        "Instrument endpoint rejected sync for instrument {InstrumentId}", instrument.InstrumentId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex, "Failed to sync instrument {InstrumentId} to instrument endpoint", instrument.InstrumentId);
            }
        });
    }
}