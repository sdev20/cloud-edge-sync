using Microsoft.AspNetCore.Mvc;
using SyncService.Domain;
using SyncService.DomainServices.BusinessLogic.Core;

namespace SyncService.WebAPI.Controllers;

[ApiController]
[Route("[controller]")]
public class InstrumentsController(IInstrumentService instrumentService) : ControllerBase
{
    [HttpGet]
    public ActionResult<List<Instrument>> GetAll()
    {
        return Ok(instrumentService.GetInstruments());
    }

    [HttpGet("{instrumentId:guid}")]
    public ActionResult<Instrument> GetById(Guid instrumentId)
    {
        var instrument = instrumentService.GetInstrument(instrumentId);
        return instrument is null ? NotFound() : Ok(instrument);
    }

    [HttpPost]
    public ActionResult<Instrument> Add(Instrument instrument)
    {
        var created = instrumentService.AddInstrument(instrument with { InstrumentId = Guid.NewGuid() });
        return CreatedAtAction(nameof(GetById), new { instrumentId = created.InstrumentId }, created);
    }

    [HttpPut("{instrumentId:guid}")]
    public async Task<ActionResult<Instrument>> Update(Guid instrumentId, Instrument instrument)
    {
        var updated = await instrumentService.UpdateInstrument(instrument with { InstrumentId = instrumentId });
        return updated is null ? NotFound() : Ok(updated);
    }
}
