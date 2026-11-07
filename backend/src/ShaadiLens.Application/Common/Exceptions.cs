namespace ShaadiLens.Application.Common;

/// <summary>Thrown by use cases; the API turns these into 404 / 409 / 400 ProblemDetails.</summary>
public sealed class NotFoundException(string what) : Exception($"{what} was not found");

public sealed class ConflictException(string message) : Exception(message);

public sealed class BadRequestException(string message) : Exception(message);

/// <summary>403 — e.g. a guest asking for a photo they're not matched in, or downloads disabled.</summary>
public sealed class ForbiddenException(string message) : Exception(message);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);
