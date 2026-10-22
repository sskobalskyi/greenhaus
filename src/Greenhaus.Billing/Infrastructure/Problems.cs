using Greenhaus.Billing.Contracts;

namespace Greenhaus.Billing.Infrastructure;

/// <summary>RFC 9457 problem responses with the stable code clients translate.</summary>
public static class Problems
{
    public static IResult Fail(int status, string code, string title, string? detail = null) =>
        Results.Json(new Problem { Title = title, Status = status, Code = code, Detail = detail }, contentType: "application/problem+json", statusCode: status);

    public static IResult Invalid(string title) => Fail(400, "validation_failed", title);
}