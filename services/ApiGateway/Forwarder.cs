using System.Net.Http.Headers;
using Polly;
using Polly.CircuitBreaker;

namespace ApiGateway;

// The core of the gateway: copy the incoming request to a service and copy the answer back.
public static class Forwarder
{
    public static async Task ForwardAsync(HttpContext context, HttpClient service, string servicePath,
        string serviceName, CircuitStates circuits)
    {
        // 1. Build the same request for the service: same method, path without /api, same query string.
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), servicePath + context.Request.QueryString);
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", context.Request.Headers["X-Correlation-Id"].ToString());

        if (context.Request.ContentLength > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is not null)
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }

        // 2. Send it. If the service is down or too slow, answer 503 ourselves.
        HttpResponseMessage response;
        try
        {
            response = await service.SendAsync(request, context.RequestAborted);
        }
        catch (Exception e) when (IsUnreachable(e))
        {
            // An open circuit means the gateway did not even try: the service failed too often just now.
            var detail = e is BrokenCircuitException
                ? "The service behind this route failed several times just now, so the gateway stopped calling it for a moment. Try again later."
                : "The service behind this route cannot be reached. Try again later.";
            // A standard problem+json body, plus "error" and "circuit" so a client (or the demo) can
            // see at a glance which service is down and whether the circuit breaker is the reason.
            await Results.Problem(detail, statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?>
                {
                    ["error"] = $"{serviceName} unavailable",
                    ["circuit"] = circuits.Get(serviceName),
                }).ExecuteAsync(context);
            return;
        }

        // 3. Copy the status code, the content type and the body back to the client.
        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();

            // A 201 Created points to the new resource, e.g. /books/6. That is the internal
            // address; the client only knows the gateway, so it becomes /api/books/6.
            if (response.Headers.Location is { } location)
                context.Response.Headers.Location = "/api" + location;

            await response.Content.CopyToAsync(context.Response.Body);
        }
    }

    // No connection, a timeout, or the circuit breaker refused the call (ExecutionRejectedException
    // is the base class of Polly's TimeoutRejectedException and BrokenCircuitException).
    // EnsureSuccessStatusCode also throws HttpRequestException for a 5xx answer.
    public static bool IsUnreachable(Exception e) =>
        e is HttpRequestException or TaskCanceledException or ExecutionRejectedException;
}
