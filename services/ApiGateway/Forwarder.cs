using System.Net.Http.Headers;

namespace ApiGateway;

// The core of the gateway: copy the incoming request to a service and copy the answer back.
public static class Forwarder
{
    public static async Task ForwardAsync(HttpContext context, HttpClient service, string servicePath)
    {
        // 1. Build the same request for the service: same method, path without /api, same query string.
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), servicePath + context.Request.QueryString);

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
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            await Results.Problem("The service behind this route cannot be reached. Try again later.",
                statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
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
}
