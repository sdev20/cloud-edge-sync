using Microsoft.AspNetCore.Mvc;
using SyncService.Infrastructure.Client.Core;

namespace SyncService.WebAPI.Controllers;

[ApiController]
[Route("instrument-connection")]
public class InstrumentConnectionController(ISyncToInstrument syncToInstrument) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Check(CancellationToken cancellationToken)
    {
        var reachable = await syncToInstrument.CheckConnectivityAsync(cancellationToken);
        return reachable ? Ok() : StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
