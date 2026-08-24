using System.Globalization;
using DriveUnion.Core.Abstractions;
using DriveUnion.Core.Plans;
using DriveUnion.Infrastructure.Services;
using DriveUnion.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace DriveUnion.Web.Hosting;

/// <summary>
/// Turns the Drive failures of §9 into answers a client can act on.
///
/// None of these responses carry the exception's own message. A Drive error text can contain the
/// resumable session URI or the account it belongs to, and both are operator credentials — the
/// caller gets a code and a fixed sentence, the log gets the detail.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DriveApiExceptionFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not DriveApiException exception) return;

        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger<DriveApiExceptionFilterAttribute>();

        ProblemDetails problem;
        switch (exception)
        {
            case DriveUploadSessionExpiredException:
                logger.LogWarning(exception, "Resumable session expired on {Path}", context.HttpContext.Request.Path);
                problem = Problem(
                    StatusCodes.Status410Gone,
                    "upload_session_expired",
                    "This upload session is no longer valid. Start the upload again.");
                break;

            case DriveRateLimitedException rateLimited:
                logger.LogWarning(exception, "Drive rate limit reached on {Path}", context.HttpContext.Request.Path);
                if (rateLimited.RetryAfter is { } retryAfter)
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                problem = Problem(
                    StatusCodes.Status503ServiceUnavailable,
                    "storage_busy",
                    "Storage is busy. Retry shortly.");
                break;

            // The customer's own plan refused this, and it must not be dressed as a storage fault.
            // PlanLimitExceededException derives from DriveApiException, so without this case it
            // falls to the default and answers 502 storage_error — telling somebody whose file is
            // simply too big for their plan that our storage is broken, and inviting them to retry
            // something that will refuse identically for ever.
            //
            // 409 rather than 507 or 402: the pool is not full, and 402 would announce a bill this
            // product does not send.
            case PlanLimitExceededException planLimit:
                logger.LogInformation(
                    "Plan limit {Limit} refused a request on {Path}",
                    planLimit.Limit,
                    context.HttpContext.Request.Path);

                context.Result = new ObjectResult(PlanLimitBodies.For(planLimit))
                {
                    StatusCode = StatusCodes.Status409Conflict,
                };
                context.ExceptionHandled = true;
                return;

            // Infrastructure's refusal when no account in the pool can take the file. It derives
            // from DriveApiException, so without this case a full pool would read as a Drive fault
            // and the operator would go looking at Google.
            case UploadRejectedException:
                logger.LogWarning(exception, "Upload refused by the pool on {Path}", context.HttpContext.Request.Path);
                problem = Problem(
                    StatusCodes.Status507InsufficientStorage,
                    "no_storage_available",
                    "No connected account can take this file.");
                break;

            case DriveAccountUnavailableException:
                logger.LogError(exception, "Storage credentials unusable on {Path}", context.HttpContext.Request.Path);
                problem = Problem(
                    StatusCodes.Status503ServiceUnavailable,
                    "storage_unavailable",
                    "Storage is unavailable. The operator has been notified.");
                break;

            default:
                logger.LogError(exception, "Drive call failed on {Path}", context.HttpContext.Request.Path);
                problem = Problem(
                    StatusCodes.Status502BadGateway,
                    "storage_error",
                    "Storage did not answer as expected.");
                break;
        }

        context.Result = new ObjectResult(problem) { StatusCode = problem.Status };
        context.ExceptionHandled = true;
    }

    private static ProblemDetails Problem(int status, string code, string detail) => new()
    {
        Status = status,
        Title = code,
        Detail = detail,
    };
}
