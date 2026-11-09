namespace BookService;

// Demo only: makes the Book Service slow or broken on purpose, so the gateway's timeout, retry and
// circuit breaker can be shown without stopping the container. Off by default; turned on with:
//   FaultInjection__DelayMs=3000      every request waits 3 seconds before it is handled
//   FaultInjection__FailureRate=0.5   half of the requests get a 500 Internal Server Error
// /health, /swagger and /openapi are left alone, so Docker keeps seeing the service as healthy
// and the faults only hit real API calls.
public class FaultInjectionSettings
{
    public int DelayMs { get; set; }
    public double FailureRate { get; set; }
}

public static class FaultInjection
{
    public static void UseFaultInjection(this WebApplication app)
    {
        var settings = app.Configuration.GetSection("FaultInjection").Get<FaultInjectionSettings>() ?? new();
        if (settings.DelayMs <= 0 && settings.FailureRate <= 0)
            return;

        app.Logger.LogWarning("Fault injection is ON: {DelayMs} ms delay, {FailureRate:P0} of requests fail with 500",
            settings.DelayMs, settings.FailureRate);

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/health") || path.StartsWithSegments("/swagger") || path.StartsWithSegments("/openapi"))
            {
                await next();
                return;
            }

            if (settings.DelayMs > 0)
                await Task.Delay(settings.DelayMs, context.RequestAborted);

            if (Random.Shared.NextDouble() < settings.FailureRate)
            {
                app.Logger.LogWarning("Fault injection: answering {Method} {Path} with 500", context.Request.Method, path);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = "Injected failure (FaultInjection__FailureRate)" });
                return;
            }

            await next();
        });
    }
}
