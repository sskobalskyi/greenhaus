namespace Greenhaus.MockServer;

/// <summary>RFC 9457 problem responses with a stable machine-readable code, shared by all the mock APIs.</summary>
public static class Problems
{
    public static IResult Fail(int status, string code, string title, string? detail = null, int? retryAfterSeconds = null)
    {
        var body = new Problem { Title = title, Status = status, Code = code, Detail = detail };
        var result = Results.Json(body, contentType: "application/problem+json", statusCode: status);
        return retryAfterSeconds is null ? result : new WithHeader(result, "Retry-After", retryAfterSeconds.Value.ToString());
    }

    public static IResult NotFound(string what = "Not found") => Fail(404, "not_found", what);

    private sealed class WithHeader(IResult inner, string name, string value) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers[name] = value;
            return inner.ExecuteAsync(httpContext);
        }
    }
}
