using System.Net.Http.Json;
using SyncService.Domain;
using SyncService.Infrastructure.Client.Core;

namespace SyncService.Infrastructure.Client;

public class SyncToInstrument(HttpClient httpClient) : ISyncToInstrument
{
    private const string InstrumentUpdateRoute = "/external/api/instruments";
    private const string InstrumentHealthRoute = "/external/api/instruments/health";

    public async Task<bool> SendInstrumentAsync(Instrument instrument, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync(InstrumentUpdateRoute, instrument, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> SendInstrumentsAsync(IReadOnlyCollection<Instrument> instruments, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync(InstrumentUpdateRoute, instruments, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> CheckConnectivityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync(InstrumentHealthRoute, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
