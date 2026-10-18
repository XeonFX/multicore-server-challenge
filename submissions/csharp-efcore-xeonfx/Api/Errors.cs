using Conduit.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Conduit.Api;

public static class Errors
{
    public static ProblemHttpResult Unauthorized => Problem(StatusCodes.Status401Unauthorized, "sign in first");
    public static ProblemHttpResult Forbidden => Problem(StatusCodes.Status403Forbidden, "only the author can do this");
    public static ProblemHttpResult NotFound => Problem(StatusCodes.Status404NotFound, "not found");

    static ProblemHttpResult Problem(int status, string message) => TypedResults.Problem(message, statusCode: status);

    /// <summary>Every error, from a handler, validation, unreadable JSON or an exception, goes out in RealWorld's shape.</summary>
    public static IServiceCollection AddRealWorldErrors(this IServiceCollection services) => services
        .AddSingleton<IProblemDetailsWriter, RealWorldErrorWriter>() // before AddProblemDetails, so it is used first
        .AddProblemDetails()
        .Configure<RouteHandlerOptions>(routes => routes.ThrowOnBadRequest = true);

    /// <summary>A body or query the server can't read, or a taken username or email, is 422; anything else is 500.</summary>
    public static IApplicationBuilder UseRealWorldErrors(this IApplicationBuilder app) => app.UseExceptionHandler(new ExceptionHandlerOptions
    {
        StatusCodeSelector = error => IsClientError(error)
            ? StatusCodes.Status422UnprocessableEntity
            : StatusCodes.Status500InternalServerError,
        SuppressDiagnosticsCallback = context => IsClientError(context.Exception), // only real failures are logged
    });

    static bool IsClientError(Exception error) =>
        error is BadHttpRequestException || error is DbUpdateException duplicate && duplicate.IsUniqueViolation();
}

/// <summary>Writes a problem as {"errors": {"body": ["&lt;message&gt;", ...]}}. Failed validation is 422, as RealWorld wants.</summary>
sealed class RealWorldErrorWriter : IProblemDetailsWriter
{
    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        string[] messages = (problem, context.Exception) switch
        {
            (HttpValidationProblemDetails validation, _) => [.. validation.Errors.SelectMany(field => field.Value)],
            (_, BadHttpRequestException unreadable) => [unreadable.Message],
            _ => [problem.Detail ?? "something went wrong"],
        };

        if (problem is HttpValidationProblemDetails)
            context.HttpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;

        return new(context.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse(new ErrorList(messages))));
    }
}
