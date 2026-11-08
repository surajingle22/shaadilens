using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Api.Auth;
using ShaadiLens.Application.People;

namespace ShaadiLens.Api.Controllers;

/// <summary>Admin "People": faces grouped into Person 1, Person 2… (clustering), name them, link them to guests.</summary>
[ApiController]
[Route("api/v1")]
[Authorize(Policy = AuthConstants.AdminPolicy)]
public sealed class PeopleController(PeopleService people) : ControllerBase
{
    [HttpGet("events/{eventId:guid}/people")]
    public Task<PeopleDto> List(Guid eventId, CancellationToken ct) => people.GetAsync(eventId, ct);

    /// <summary>Group all faces again (background job; takes seconds to a minute).</summary>
    [HttpPost("events/{eventId:guid}/people/run")]
    public async Task<IActionResult> Run(Guid eventId, RunClusteringRequest req, CancellationToken ct)
    {
        await people.StartAsync(eventId, req, ct);
        return Accepted();
    }

    [HttpPut("people/{clusterId:guid}")]
    public async Task<IActionResult> Update(Guid clusterId, UpdatePersonRequest req, CancellationToken ct)
    {
        await people.UpdateAsync(clusterId, req, ct);
        return NoContent();
    }
}
