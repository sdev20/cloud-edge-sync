using Edge.WebAPI.Requests;
using Microsoft.AspNetCore.Mvc;

namespace Edge.WebAPI.Controllers;

[ApiController]
[Route("api/instruments")]
public class InstrumentsController(ILogger<InstrumentsController> logger) : ControllerBase
{
    [HttpPost]
    public IActionResult ReceiveUpdate([FromBody] InstrumentUpdateRequest instrument)
    {
        logger.LogInformation(
            "Received instrument update via sync: {InstrumentId} - {Name} ({Status})",
            instrument.InstrumentId, instrument.Name, instrument.Status);

        return Ok();
    }

    [HttpGet("health")]
    public IActionResult Health() => Ok();
}
