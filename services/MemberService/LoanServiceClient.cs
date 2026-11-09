using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace MemberService;

// The deletion saga needs two things from the Loan Service. The Member Service never touches
// loan_db; it asks over HTTP. Every call carries the saga id in X-Correlation-Id, so one deletion
// can be followed through the logs of both services.
public class LoanServiceClient(HttpClient http)
{
    // Only the fields the saga needs.
    public record ActiveLoan(int Id, int BookId, DateTime DueDate);
    public record AnonymizeResult(int MemberId, int Anonymized);

    // Step 2: how many books does this member still have?
    public async Task<int> CountActiveLoansAsync(int memberId, Guid sagaId, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/loans/active?memberId={memberId}");
        request.Headers.Add("X-Correlation-Id", sagaId.ToString());
        using var response = await http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        var loans = await response.Content.ReadFromJsonAsync<List<ActiveLoan>>(cancel);
        return loans?.Count ?? 0;
    }

    // Step 3: remove the member id from their loan history. Idempotent on the Loan Service side.
    public async Task<int> AnonymizeLoansAsync(int memberId, Guid sagaId, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/loans/anonymize")
        {
            Content = JsonContent.Create(new { memberId }),
        };
        request.Headers.Add("X-Correlation-Id", sagaId.ToString());
        using var response = await http.SendAsync(request, cancel);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AnonymizeResult>(cancel);
        return result?.Anonymized ?? 0;
    }
}

public static class LoanServiceClientSetup
{
    // The same resilience setup as the gateway (services/ApiGateway/Resilience.cs): retry, circuit
    // breaker and a timeout per attempt, with the values from Resilience__* configuration. It is a
    // copy on purpose: services do not share code, so each service owns its own setup.
    //
    // One difference: here POST is retried too. That is safe because the only POST is
    // /loans/anonymize, which is idempotent (doing it twice gives the same result).
    public static void AddLoanServiceClient(this IServiceCollection services, IConfiguration config, string url)
    {
        var timeout = TimeSpan.FromSeconds(config.GetValue("Resilience:TimeoutSeconds", 2.0));
        var retries = config.GetValue("Resilience:RetryAttempts", 2);

        services.AddHttpClient<LoanServiceClient>(c => c.BaseAddress = new Uri(url))
            .AddResilienceHandler("resilience", (pipeline, context) =>
            {
                var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Resilience");

                if (retries > 0)
                {
                    pipeline.AddRetry(new HttpRetryStrategyOptions
                    {
                        MaxRetryAttempts = retries,
                        BackoffType = DelayBackoffType.Exponential,
                        UseJitter = true,
                        Delay = TimeSpan.FromMilliseconds(200),
                        ShouldHandle = args => ValueTask.FromResult(IsFailure(args.Outcome)),
                        OnRetry = args =>
                        {
                            logger.LogWarning("loan-service: attempt {Attempt} failed, retrying in {Delay} ms",
                                args.AttemptNumber + 1, (int)args.RetryDelay.TotalMilliseconds);
                            return default;
                        },
                    });
                }

                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio = config.GetValue("Resilience:FailureRatio", 0.5),
                    MinimumThroughput = config.GetValue("Resilience:MinimumThroughput", 4),
                    SamplingDuration = TimeSpan.FromSeconds(config.GetValue("Resilience:SamplingDurationSeconds", 10)),
                    BreakDuration = TimeSpan.FromSeconds(config.GetValue("Resilience:BreakDurationSeconds", 15)),
                    ShouldHandle = args => ValueTask.FromResult(IsFailure(args.Outcome)),
                    OnOpened = args =>
                    {
                        logger.LogWarning("Circuit for loan-service: -> OPEN for {Seconds}s", args.BreakDuration.TotalSeconds);
                        return default;
                    },
                    OnHalfOpened = _ =>
                    {
                        logger.LogWarning("Circuit for loan-service: Open -> HALF-OPEN");
                        return default;
                    },
                    OnClosed = _ =>
                    {
                        logger.LogInformation("Circuit for loan-service: HalfOpen -> CLOSED");
                        return default;
                    },
                });

                pipeline.AddTimeout(timeout);
            });
    }

    // Same rule as the gateway: no connection, a timeout or a 500/502/504 is a failure.
    private static bool IsFailure(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is HttpRequestException or TimeoutRejectedException ||
        outcome.Result?.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;
}
