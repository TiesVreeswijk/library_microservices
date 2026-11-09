using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace ApiGateway;

// The resilience values for one service. The defaults are deliberately low so the circuit breaker
// trips quickly in a demo. Externalized Configuration: every value can be changed with an
// environment variable without rebuilding, for all services at once or for one service:
//   Resilience__FailureRatio=0.8                 (all services)
//   Resilience__BookService__TimeoutSeconds=5    (only the Book Service)
public class ResilienceSettings
{
    // How long one attempt may take before Polly gives up on it.
    public double TimeoutSeconds { get; set; } = 2;

    // How many extra attempts a failed GET gets (so 2 means at most 3 calls in total).
    public int RetryAttempts { get; set; } = 2;

    // The circuit opens when at least FailureRatio of the calls in the last SamplingDurationSeconds
    // failed, and there were at least MinimumThroughput calls. It then stays open for BreakDurationSeconds.
    public double FailureRatio { get; set; } = 0.5;
    public int MinimumThroughput { get; set; } = 4;
    public int SamplingDurationSeconds { get; set; } = 10;
    public int BreakDurationSeconds { get; set; } = 15;
}

public static class Resilience
{
    // One named HttpClient per service, each with its own pipeline, so each service has its own circuit.
    // A broken Book Service never stops the gateway from calling the Member or Loan Service.
    public static void AddServiceClient(this IServiceCollection services, IConfiguration config,
        string name, string displayName, string url)
    {
        var settings = new ResilienceSettings();
        // POST /loans makes the Loan Service call two other services with a 5 second timeout each,
        // so 2 seconds would cut off normal requests. The Loan Service gets a longer default.
        if (name == "LoanService")
            settings.TimeoutSeconds = 12;
        config.GetSection("Resilience").Bind(settings);
        config.GetSection($"Resilience:{name}").Bind(settings);

        services.AddHttpClient(name, c => c.BaseAddress = new Uri(url))
            .AddResilienceHandler("resilience", (pipeline, context) =>
            {
                var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Resilience");
                var circuits = context.ServiceProvider.GetRequiredService<CircuitStates>();

                // The order matters. The strategy added first is the outermost:
                //   retry( circuit breaker( timeout( HTTP call ) ) )
                // So every attempt of the retry goes through the circuit breaker and is counted there,
                // and a timeout counts as a failure for both.

                // 1. Retry: a failed GET is tried again after about 0.2s, then 0.4s (exponential backoff).
                //    Jitter adds a bit of randomness, so many clients don't all retry at the same moment.
                if (settings.RetryAttempts > 0)
                {
                    var retry = new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = settings.RetryAttempts,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromMilliseconds(200),
                        ShouldHandle = args => ValueTask.FromResult(IsFailure(args.Outcome)),
                        OnRetry = args =>
                        {
                            logger.LogWarning("{Service}: attempt {Attempt} failed ({Reason}), retrying in {Delay} ms",
                                displayName, args.AttemptNumber + 1, Describe(args.Outcome), (int)args.RetryDelay.TotalMilliseconds);
                            return default;
                        },
                    };
                    // Only GET, HEAD, OPTIONS etc. are retried. Retrying POST /loans after a timeout could
                    // borrow the same book twice: the first attempt may have worked, we just didn't hear back.
                    retry.DisableForUnsafeHttpMethods();
                    pipeline.AddRetry(retry);
                }

                // 2. Circuit breaker. Closed: calls go through and failures are counted.
                //    Open: too many failures, so for BreakDuration the gateway does not call the service at
                //    all and fails at once with a BrokenCircuitException, instead of letting every client wait.
                //    Half-open: after BreakDuration the next call is let through as a trial. If it works the
                //    circuit closes, if it fails it opens again.
                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = settings.FailureRatio,
                    MinimumThroughput = settings.MinimumThroughput,
                    SamplingDuration = TimeSpan.FromSeconds(settings.SamplingDurationSeconds),
                    BreakDuration = TimeSpan.FromSeconds(settings.BreakDurationSeconds),
                    ShouldHandle = args => ValueTask.FromResult(IsFailure(args.Outcome)),
                    OnOpened = args =>
                    {
                        circuits.Set(displayName, "Open");
                        logger.LogWarning("Circuit for {Service}: Closed/HalfOpen -> OPEN for {Seconds}s (last failure: {Reason})",
                            displayName, args.BreakDuration.TotalSeconds, Describe(args.Outcome));
                        return default;
                    },
                    OnHalfOpened = _ =>
                    {
                        circuits.Set(displayName, "HalfOpen");
                        logger.LogWarning("Circuit for {Service}: Open -> HALF-OPEN, letting one trial call through", displayName);
                        return default;
                    },
                    OnClosed = _ =>
                    {
                        circuits.Set(displayName, "Closed");
                        logger.LogInformation("Circuit for {Service}: HalfOpen -> CLOSED, the service works again", displayName);
                        return default;
                    },
                });

                // 3. Timeout per attempt.
                pipeline.AddTimeout(new HttpTimeoutStrategyOptions
                {
                    Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
                    OnTimeout = args =>
                    {
                        logger.LogWarning("{Service}: no answer within {Seconds}s, attempt cancelled", displayName, args.Timeout.TotalSeconds);
                        return default;
                    },
                });
            });
    }

    // What counts as a failure for the retry and the circuit breaker:
    // - no connection (HttpRequestException) or the timeout above;
    // - 500, 502 or 504: the service answered, but it is broken.
    // A 503 does not count. The Loan Service answers 503 when the Book or Member Service is down;
    // if that counted, a broken Book Service would also open the circuit of the healthy Loan Service.
    // 4xx answers are the client's fault and never count.
    private static bool IsFailure(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is HttpRequestException or TimeoutRejectedException ||
        outcome.Result?.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;

    private static string Describe(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception?.GetType().Name ?? $"HTTP {(int?)outcome.Result?.StatusCode}";
}
