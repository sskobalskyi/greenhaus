using Greenhaus.Api.Contracts;
using Greenhaus.Domain;

namespace Greenhaus.Api.Infrastructure;

/// <summary>RFC 9457 problem responses with the stable code clients translate.</summary>
public static class Problems
{
    public static IResult Fail(int status, string code, string title, string? detail = null, string? limitType = null, Tier? requiredTier = null) =>
        Results.Json(
            new Problem { Title = title, Status = status, Code = code, Detail = detail, LimitType = limitType, RequiredTier = requiredTier },
            contentType: "application/problem+json",
            statusCode: status);

    public static IResult NotFound(string what = "Not found") => Fail(404, "not_found", what);

    public static IResult Invalid(string title) => Fail(400, "validation_failed", title);
}