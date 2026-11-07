using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Infrastructure.FaceService;

namespace ShaadiLens.Api.Infrastructure;

/// <summary>
/// Turns face-service failures into ProblemDetails responses:
///  - face-service said 400/413 (bad/too large image) -> pass that status through
///  - anything else (down, timeout, 500)                -> 502 Bad Gateway
/// </summary>
public sealed class FaceServiceExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not (FaceServiceException or HttpRequestException)) return false;

        var status = exception is FaceServiceException { StatusCode: 400 or 413 } fse
            ? fse.StatusCode!.Value
            : StatusCodes.Status502BadGateway;

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status == StatusCodes.Status502BadGateway ? "Face service unavailable" : "Invalid image",
                Detail = exception.Message,
            },
        });
    }
}
