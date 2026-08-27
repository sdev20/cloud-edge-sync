using SyncService.Domain;

namespace SyncService.DomainServices.BusinessLogic;

public class InMemoryDataStore
{
    private readonly Lock _lock = new();

    private readonly List<Instrument> _instruments =
    [
        new Instrument(Guid.NewGuid(), "Micropipettes", "transfer tiny, exact volumes of liquid", "Available"),
        new Instrument(Guid.NewGuid(), "Spectrophotometers", "measure the concentration of biomolecules.", "Available"),
        new Instrument(Guid.NewGuid(), "PCR Machines", "amplify DNA sequences", "Available"),
        new Instrument(Guid.NewGuid(), "Centrifuges", "Spin samples at high speeds", "Unavailable"),
        new Instrument(Guid.NewGuid(), "Electrophoresis Units", "electrical charge to separate DNA, RNA, or proteins",
            "Available")
    ];

    public List<Instrument> GetInstruments()
    {
        lock (_lock)
        {
            return [.. _instruments];
        }
    }

    public Instrument? GetInstrument(Guid instrumentId)
    {
        lock (_lock)
        {
            return _instruments.FirstOrDefault(i => i.InstrumentId == instrumentId);
        }
    }

    public Instrument AddInstrument(Instrument instrument)
    {
        lock (_lock)
        {
            _instruments.Add(instrument);
            return instrument;
        }
    }

    public Instrument? UpdateInstrument(Instrument instrument)
    {
        lock (_lock)
        {
            var index = _instruments.FindIndex(i => i.InstrumentId == instrument.InstrumentId);
            if (index == -1)
            {
                return null;
            }

            _instruments[index] = instrument;
            return instrument;
        }
    }
}