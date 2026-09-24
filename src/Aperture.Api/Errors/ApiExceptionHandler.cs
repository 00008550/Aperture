using System.Diagnostics;
using System.Text.Json;
using Aperture.SharedKernel.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Aperture.Api.Errors;

/// <summary>
/// The host's one mapping from an escaped exception to a response (ARCHITECTURE.md §5 "Errors are contracts").
/// <list type="bullet">
/// <item><see cref="DomainValidationException"/> — an aggregate's input rule, broken by the caller — is a
/// <c>400</c> <see cref="ValidationProblemDetails"/> with <c>errors.&lt;field&gt;</c>. The endpoints never
/// re-validate: the aggregate is the single definition, and the assistant (§9) can only self-correct from a
/// 400 that says which field was wrong.</item>
/// <item><see cref="BadHttpRequestException"/> — a body the framework could not bind — keeps the status the
/// framework chose (it throws rather than answers only in Development) with a generic title.</item>
/// <item>Anything else is a server fault: a <c>500</c> ProblemDetails with a <c>traceId</c> and a generic
/// title. The exception's message, type and stack go to the log, never to the body — they name internals a
/// caller has no business learning.</item>
/// </list>
/// Every body goes through <see cref="IProblemDetailsService"/> (or, when the caller's <c>Accept</c> excludes JSON,
/// is written as the same problem+json directly, so the status never degrades), so it is <c>application/problem+json</c> and
/// carries the same <c>traceId</c> (<c>Activity.Current?.Id ?? TraceIdentifier</c>) the log line does.
/// Authorization has already run by the time any of this can throw: a caller without the write permission
/// is refused before its body is bound, so it learns nothing about the rules.
/// </summary>
internal sealed partial class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var route = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? httpContext.Request.Path.Value;

        ProblemDetails body;
        switch (exception)
        {
            case DomainValidationException invalid:
                // A client mistake, not an incident: Information, and the field rather than the value.
                LogValidationFailed(logger, invalid.Field, route);
                body = new ValidationProblemDetails(
                    new Dictionary<string, string[]> { [invalid.Field] = [invalid.Message] })
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred.",
                };
                break;

            case BadHttpRequestException badRequest:
                LogBadRequest(logger, badRequest.StatusCode, route);
                body = new ProblemDetails
                {
                    Status = badRequest.StatusCode,
                    Title = "The request could not be read.",
                };
                break;

            default:
                LogUnhandled(logger, exception, route);
                body = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "An unexpected error occurred.",
                };
                break;
        }

        httpContext.Response.StatusCode = body.Status!.Value;

        // Exception deliberately NOT passed to the context: nothing downstream of here may render it.
        if (await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = body,
        }))
        {
            return true;
        }

        // No registered writer accepted the request's Accept header (e.g. text/html). Returning false here
        // would let the middleware rethrow and turn even a 400 into an empty 500, so the status would lie.
        // The status is the contract: write the same body as problem+json anyway — RFC 9110 §12.5.1 lets a
        // server disregard Accept rather than answer 406, and this body is already the generic, internals-free
        // one built above, so ignoring the preference cannot leak anything.
        // Mirror the defaults the problem-details writer would have applied, so both paths yield one shape.
        body.Type ??= body.Status switch
        {
            400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            500 => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            _ => null,
        };
        body.Extensions["traceId"] =Activity.Current?.Id ?? httpContext.TraceIdentifier;
        await httpContext.Response.WriteAsJsonAsync<object>(
            body, JsonSerializerOptions.Web, "application/problem+json", cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request rejected: invalid {ValidationField} on {Route}")]
    private static partial void LogValidationFailed(ILogger logger, string validationField, string? route);

    [LoggerMessage(Level = LogLevel.Information, Message = "Request rejected: unreadable body ({StatusCode}) on {Route}")]
    private static partial void LogBadRequest(ILogger logger, int statusCode, string? route);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on {Route}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string? route);
}
