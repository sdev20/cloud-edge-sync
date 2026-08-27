using SyncService.Domain;

namespace SyncService.DomainServices.BusinessLogic.Core;

public interface IInstrumentService
{
    List<Instrument> GetInstruments();

    Instrument? GetInstrument(Guid instrumentId);

    Instrument AddInstrument(Instrument instrument);

    Task<Instrument?> UpdateInstrument(Instrument instrument);
}
