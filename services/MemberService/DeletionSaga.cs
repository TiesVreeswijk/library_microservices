namespace MemberService;

// The state of one "delete member" saga, stored in member_db (the orchestrator owns it).
// It is saved after every step, so when the service crashes halfway, the worker simply
// continues from CurrentStep after the restart. Nothing is kept only in memory.
public class DeletionSaga
{
    public Guid Id { get; set; }
    public int MemberId { get; set; }

    // The step that runs next (or is being retried).
    public SagaStep CurrentStep { get; set; }
    public SagaState State { get; set; }
    public string? FailureReason { get; set; }

    // How often the current step failed because a service could not be reached, and when the
    // worker may try it again (backoff: 2, 4, 8, 16, 30, 30... seconds).
    public int RetryCount { get; set; }
    public DateTime NextAttemptAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// The four steps of the saga. Step 1 runs inside DELETE /members/{id} itself.
public enum SagaStep
{
    MarkPendingDeletion = 1, // Member Service, compensatable: set back to Active
    CheckActiveLoans = 2,    // Loan Service, pivot: decides go / no-go
    AnonymizeLoans = 3,      // Loan Service, retryable: must eventually succeed
    AnonymizeMember = 4,     // Member Service, retryable
    Done = 5,
}

// Running: going forward. Compensating: a step said no, undoing step 1.
// Completed: member deleted. Failed: compensated, the member is Active again.
public enum SagaState { Running, Compensating, Completed, Failed }
