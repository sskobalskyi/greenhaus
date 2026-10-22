using Greenhaus.Fulfilment.Contracts;

namespace Greenhaus.Fulfilment.Infrastructure;

/// <summary>RFC 9457 problem responses with the stable code clients translate.</summary>
public static class Problems
{
    public static IResult Fail(int status, string code, string title, string? detail = null) =>
        Results.Json(new Problem { Title = title, Status = status, Code = code, Detail = detail }, contentType: "application/problem+json", statusCode: status);

    public static IResult NotFound(string what = "Not found") => Fail(404, "not_found", what);
}