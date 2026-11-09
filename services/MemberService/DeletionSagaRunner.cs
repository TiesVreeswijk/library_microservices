using Polly;

namespace MemberService;

// The orchestrator of the "delete member" saga. It lives in the Member Service because this
// service owns the member and the decision to delete them.
//
// Why a saga? Deleting a member changes data in two databases (member_db and loan_db), and there
// is no transaction across two databases. So the work is split into local steps, each saved on
// its own, with a rule for what happens when a step fails:
//
//   1. MarkPendingDeletion  Member  compensatable  (done in DELETE /members/{id})
//   2. CheckActiveLoans     Loan    pivot: active loans -> compensate step 1 and stop
//   3. AnonymizeLoans       Loan    retryable: after the pivot we only go forward
//   4. AnonymizeMember      Member  retryable
//
// Step 1 is also a lock: while the member is PendingDeletion the Loan Service refuses new loans,
// so no loan can sneak in between the check in step 2 and the anonymizing in steps 3 and 4.
//
// The runner saves the saga after every step. The worker (DeletionSagaWorker) calls RunAsync for
// every saga that is not finished, so after a crash or an unreachable Loan Service it continues
// where it stopped.
public class DeletionSagaRunner(MemberDbContext db, LoanServiceClient loans, ILogger<DeletionSagaRunner> logger, IConfiguration config)
{
    // Before the pivot we may still give up (and compensate) when the Loan Service stays away.
    // After the pivot giving up is not an option, so steps 3 and 4 are retried forever.
    private readonly int maxAttemptsBeforePivot = config.GetValue("Saga:MaxAttemptsBeforePivot", 10);

    public async Task RunAsync(DeletionSaga saga, CancellationToken cancel)
    {
        while (saga.State is SagaState.Running or SagaState.Compensating)
        {
            // Each step returns true when it finished (and was saved), false when it has to be retried later.
            var finished = saga.State == SagaState.Compensating
                ? await CompensateAsync(saga, cancel)
                : saga.CurrentStep switch
                {
                    SagaStep.CheckActiveLoans => await CheckActiveLoansAsync(saga, cancel),
                    SagaStep.AnonymizeLoans => await AnonymizeLoansAsync(saga, cancel),
                    SagaStep.AnonymizeMember => await AnonymizeMemberAsync(saga, cancel),
                    _ => throw new InvalidOperationException($"Saga {saga.Id} is Running at step {saga.CurrentStep}."),
                };

            if (!finished)
                return;
        }
    }

    // Step 2 (pivot): go or no-go.
    private async Task<bool> CheckActiveLoansAsync(DeletionSaga saga, CancellationToken cancel)
    {
        int activeLoans;
        try
        {
            activeLoans = await loans.CountActiveLoansAsync(saga.MemberId, saga.Id, cancel);
        }
        catch (Exception e) when (IsUnreachable(e) && !cancel.IsCancellationRequested)
        {
            if (saga.RetryCount + 1 >= maxAttemptsBeforePivot)
            {
                // We never got an answer, so we never passed the pivot: undoing step 1 is still allowed.
                logger.LogWarning("Saga {SagaId} member {MemberId}: step 2 CheckActiveLoans gave up after {Attempts} attempts, compensating",
                    saga.Id, saga.MemberId, saga.RetryCount + 1);
                await StartCompensationAsync(saga, $"The Loan Service could not be reached after {saga.RetryCount + 1} attempts.", cancel);
                return true;
            }

            await ScheduleRetryAsync(saga, e, cancel);
            return false;
        }

        if (activeLoans > 0)
        {
            logger.LogWarning("Saga {SagaId} member {MemberId}: step 2 CheckActiveLoans says NO ({Count} active loans), compensating",
                saga.Id, saga.MemberId, activeLoans);
            await StartCompensationAsync(saga, $"The member still has {activeLoans} book(s) on loan.", cancel);
            return true;
        }

        logger.LogInformation("Saga {SagaId} member {MemberId}: step 2 CheckActiveLoans done, no active loans (pivot passed)",
            saga.Id, saga.MemberId);
        await MoveToAsync(saga, SagaStep.AnonymizeLoans, cancel);
        return true;
    }

    // Step 3 (retryable): anonymize the loan history in the Loan Service.
    private async Task<bool> AnonymizeLoansAsync(DeletionSaga saga, CancellationToken cancel)
    {
        int anonymized;
        try
        {
            anonymized = await loans.AnonymizeLoansAsync(saga.MemberId, saga.Id, cancel);
        }
        catch (Exception e) when (IsUnreachable(e) && !cancel.IsCancellationRequested)
        {
            await ScheduleRetryAsync(saga, e, cancel);
            return false;
        }

        logger.LogInformation("Saga {SagaId} member {MemberId}: step 3 AnonymizeLoans done ({Count} loans anonymized)",
            saga.Id, saga.MemberId, anonymized);
        await MoveToAsync(saga, SagaStep.AnonymizeMember, cancel);
        return true;
    }

    // Step 4 (retryable): anonymize the member itself. The member and the saga are both in member_db,
    // so this change and "saga completed" are saved in one local transaction (one SaveChanges).
    private async Task<bool> AnonymizeMemberAsync(DeletionSaga saga, CancellationToken cancel)
    {
        var member = await db.Members.FindAsync([saga.MemberId], cancel)
            ?? throw new InvalidOperationException($"Member {saga.MemberId} of saga {saga.Id} does not exist.");

        member.Name = "";
        member.CardNumber = null;
        member.Status = MemberStatus.Deleted;

        saga.CurrentStep = SagaStep.Done;
        saga.State = SagaState.Completed;
        await SaveAsync(saga, cancel);

        logger.LogInformation("Saga {SagaId} member {MemberId}: step 4 AnonymizeMember done, saga COMPLETED", saga.Id, saga.MemberId);
        return true;
    }

    // Compensation of step 1: the member becomes Active again and the saga ends as Failed.
    // Steps 3 and 4 never ran (they only run after the pivot), so there is nothing else to undo.
    private async Task<bool> CompensateAsync(DeletionSaga saga, CancellationToken cancel)
    {
        var member = await db.Members.FindAsync([saga.MemberId], cancel)
            ?? throw new InvalidOperationException($"Member {saga.MemberId} of saga {saga.Id} does not exist.");

        member.Status = MemberStatus.Active;
        saga.State = SagaState.Failed;
        await SaveAsync(saga, cancel);

        logger.LogInformation("Saga {SagaId} member {MemberId}: compensated step 1, member is Active again, saga FAILED ({Reason})",
            saga.Id, saga.MemberId, saga.FailureReason);
        return true;
    }

    private async Task StartCompensationAsync(DeletionSaga saga, string reason, CancellationToken cancel)
    {
        saga.State = SagaState.Compensating;
        saga.FailureReason = reason;
        saga.RetryCount = 0;
        await SaveAsync(saga, cancel);
    }

    private async Task MoveToAsync(DeletionSaga saga, SagaStep next, CancellationToken cancel)
    {
        saga.CurrentStep = next;
        saga.RetryCount = 0;
        saga.FailureReason = null;
        await SaveAsync(saga, cancel);
    }

    // The Loan Service could not be reached: stay at this step and let the worker try again later,
    // waiting longer each time (2, 4, 8, 16, then every 30 seconds).
    private async Task ScheduleRetryAsync(DeletionSaga saga, Exception e, CancellationToken cancel)
    {
        saga.RetryCount++;
        var wait = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, saga.RetryCount), 30));
        saga.NextAttemptAt = DateTime.UtcNow + wait;
        saga.FailureReason = $"Waiting for the Loan Service ({e.GetType().Name}), attempt {saga.RetryCount}.";
        await SaveAsync(saga, cancel);

        logger.LogWarning("Saga {SagaId} member {MemberId}: step {Step} could not reach the Loan Service ({Error}), retry {Retry} in {Seconds}s",
            saga.Id, saga.MemberId, saga.CurrentStep, e.GetType().Name, saga.RetryCount, wait.TotalSeconds);
    }

    private async Task SaveAsync(DeletionSaga saga, CancellationToken cancel)
    {
        saga.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancel);
    }

    // No connection or an error status (HttpRequestException), a timeout, or an open circuit
    // (ExecutionRejectedException is the base class of Polly's timeout and circuit breaker exceptions).
    private static bool IsUnreachable(Exception e) =>
        e is HttpRequestException or TaskCanceledException or ExecutionRejectedException;
}
