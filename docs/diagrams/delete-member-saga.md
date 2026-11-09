# "Delete member" saga

Orchestrated by the Member Service, over HTTP, without a message broker. The saga state is
saved in member_db after every step (`DeletionSagas` table), so the background worker can
continue it after a failure or a restart. Code: `services/MemberService/DeletionSagaRunner.cs`.

| # | Step | Service | Type |
|---|------|---------|------|
| 1 | Status → `PendingDeletion` | Member | compensatable |
| 2 | Any active loans? | Loan | pivot |
| 3 | Anonymize loan history | Loan | retryable |
| 4 | Anonymize member, status `Deleted` | Member | retryable |

## Happy path and compensation

```mermaid
sequenceDiagram
    autonumber
    actor Client
    participant GW as API Gateway
    participant MS as Member Service<br/>(orchestrator)
    participant MDB as member_db
    participant W as Saga worker<br/>(BackgroundService)
    participant LS as Loan Service
    participant LDB as loan_db

    Client->>GW: DELETE /api/members/{id}
    GW->>MS: DELETE /members/{id}
    Note over MS,MDB: Step 1 (compensatable), one local transaction
    MS->>MDB: Status = PendingDeletion<br/>INSERT saga (Running, CheckActiveLoans)
    MS-->>GW: 202 Accepted, Location: /members/{id}/deletion
    GW-->>Client: 202 Accepted, Location: /api/members/{id}/deletion

    W->>MDB: find unfinished sagas
    Note over W,LS: Step 2 (pivot)
    W->>LS: GET /loans/active?memberId={id}<br/>X-Correlation-Id: saga id
    LS->>LDB: SELECT loans WHERE ReturnedAt IS NULL
    LS-->>W: active loans

    alt no active loans: go
        W->>MDB: saga step = AnonymizeLoans
        Note over W,LS: Step 3 (retryable, idempotent)
        W->>LS: POST /loans/anonymize { memberId }
        LS->>LDB: UPDATE loans SET MemberId = NULL
        LS-->>W: 200 { anonymized: n }
        W->>MDB: saga step = AnonymizeMember
        Note over W,MDB: Step 4 (retryable), one local transaction
        W->>MDB: Name = "", CardNumber = NULL, Status = Deleted<br/>saga = Completed
    else active loans: no-go
        W->>MDB: saga = Compensating ("still has n books")
        Note over W,MDB: Compensation of step 1
        W->>MDB: Status = Active<br/>saga = Failed
    end

    Client->>GW: GET /api/members/{id}/deletion
    GW->>MS: GET /members/{id}/deletion
    MS-->>Client: state, currentStep, failureReason
```

## When the Loan Service cannot be reached

```mermaid
sequenceDiagram
    participant W as Saga worker
    participant MDB as member_db
    participant LS as Loan Service (down)

    W->>LS: step 2 or 3
    LS--xW: no answer (timeout / circuit open)
    W->>MDB: RetryCount + 1, NextAttemptAt = now + 2, 4, 8, 16, 30... s
    Note over W: member stays PendingDeletion,<br/>so the Loan Service refuses new loans
    W->>LS: same step again, once NextAttemptAt has passed
    Note over W,LS: Before the pivot (step 2): give up after<br/>Saga__MaxAttemptsBeforePivot attempts and compensate.<br/>After the pivot (step 3): retry until it works.
```
