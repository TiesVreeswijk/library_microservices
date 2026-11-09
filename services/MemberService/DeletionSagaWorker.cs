using Microsoft.EntityFrameworkCore;

namespace MemberService;

// Runs in the background for as long as the Member Service runs. Every few seconds it picks up
// every saga that is not finished and whose retry time has come, and lets the runner continue it.
// This is what makes steps 3 and 4 "retryable": if the Loan Service is down, the saga just waits
// here, and it finishes by itself once the Loan Service is back. Because the state is in member_db,
// this also works after the Member Service itself was restarted.
//
// Note: this assumes one Member Service instance. With several instances two workers could pick
// up the same saga; that would need a lock (e.g. SELECT ... FOR UPDATE SKIP LOCKED).
public class DeletionSagaWorker(IServiceScopeFactory scopes, ILogger<DeletionSagaWorker> logger, IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Saga:PollSeconds", 2));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // A new scope per round, so every round gets a fresh DbContext.
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemberDbContext>();
                var runner = scope.ServiceProvider.GetRequiredService<DeletionSagaRunner>();

                var now = DateTime.UtcNow;
                var due = await db.DeletionSagas
                    .Where(s => (s.State == SagaState.Running || s.State == SagaState.Compensating) && s.NextAttemptAt <= now)
                    .OrderBy(s => s.CreatedAt)
                    .ToListAsync(stoppingToken);

                foreach (var saga in due)
                    await runner.RunAsync(saga, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // E.g. the database is briefly unreachable. Log it and try again next round.
                logger.LogError(e, "Deletion saga worker round failed");
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
