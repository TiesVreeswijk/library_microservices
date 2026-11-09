# Circuit breaker states

One circuit per service in the gateway (`services/ApiGateway/Resilience.cs`), and one in the
Member Service for its calls to the Loan Service. Values are the defaults from
`docker-compose.yml` / `.env.example` and can be changed with environment variables.

A **failure** is: no connection, the 2 second timeout (12 s for the Loan Service), or a
500/502/504 answer. A 503 and 4xx answers do not count.

```mermaid
stateDiagram-v2
    direction LR
    [*] --> Closed

    Closed --> Closed: call works, or too few failures
    Closed --> Open: ≥ 50% of the calls in the last 10 s failed<br/>and there were ≥ 4 calls
    Open --> Open: every call fails at once<br/>(BrokenCircuitException → 503)
    Open --> HalfOpen: 15 s passed and<br/>the next call comes in
    HalfOpen --> Closed: trial call works
    HalfOpen --> Open: trial call fails<br/>(open for another 15 s)

    note right of Closed
        CIRCUIT_FAILURE_RATIO = 0.5
        CIRCUIT_MINIMUM_THROUGHPUT = 4
        CIRCUIT_SAMPLING_SECONDS = 10
    end note
    note right of Open
        CIRCUIT_BREAK_SECONDS = 15
    end note
```

## Where it sits in the pipeline

Every call goes through three Polly strategies. The outer one wraps the inner ones, so every
retry attempt passes through the circuit breaker and is counted there.

```mermaid
flowchart LR
    A[Gateway handler] --> R[Retry<br/>GET only, 2 extra attempts<br/>~0.2 s, 0.4 s + jitter]
    R --> CB[Circuit breaker<br/>one per service]
    CB --> T[Timeout<br/>2 s per attempt]
    T --> S[(Service)]
    CB -. open .-> F[BrokenCircuitException<br/>→ 503 with circuit: Open<br/>or overview fallback]
```

You can watch the state with `GET /api/diagnostics/circuits` (demo only,
`Gateway__EnableDiagnostics=true`) and in the logs (`docker compose logs -f api-gateway`).
Polly only moves from Open to HalfOpen when a call comes in after the break, so the endpoint
can show `Open` for a while after the 15 seconds are over.
