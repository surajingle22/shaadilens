using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ShaadiLens.Application.Common;
using ShaadiLens.Application.Guests;

namespace ShaadiLens.Api.Infrastructure;

/// <summary>Maps use-case exceptions to HTTP status codes + ProblemDetails JSON.</summary>
public sealed class AppExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        // Selfie not usable → 422 with a machine-readable code the UI can react to (e.g. "too_small").
        if (exception is SelfieRejectedException selfie)
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            var pd = new ProblemDetails { Status = 422, Title = "Selfie not usable", Detail = selfie.Message };
            pd.Extensions["code"] = selfie.Code;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = pd });
        }

        var (status, title) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            BadRequestException => (StatusCodes.Status400BadRequest, "Bad request"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden"),
            _ => (0, ""),
        };
        if (status == 0) return false;

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = exception.Message },
        });
    }
}
