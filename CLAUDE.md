# library_microservices

School project (Fontys ICT) for learning microservices. The goal is understanding
the patterns and being able to explain them, not production readiness. Budget is
about 4 hours a week across 6 two-week sprints, so prefer the simple version that
can be reasoned about over clever abstractions.

## What this project has to demonstrate

Main pattern: **API Gateway**. Supporting patterns: Database per Service,
API Composition, Health Check API, Circuit Breaker, Externalized Configuration,
and (added later, on request) an orchestrated **Saga** for deleting a member.
All taken from microservices.io.

Deliberately out of scope: CQRS, Event Sourcing, Transactional Outbox, message
brokers, Kubernetes and a frontend. Don't introduce them unless asked — they are
the "if I get more hours" options in the planning doc. The saga runs over plain
HTTP with its state in member_db, so it does not need a broker.

## Plan documents

- `docs/01-semester-project-plan.md` — pattern choice and why, architecture, learning goals
- `docs/02-sprint-planning.md` — sprint-by-sprint plan, extra-hours options, risks
- `docs/DEMO.md` — the demo script (six scenarios), with `docs/demo.http`,
  `docs/postman/` and `docs/diagrams/` (Mermaid: saga sequence, circuit breaker states)

Note: those documents name TypeScript + Node as the stack, written before the
switch to C#. The actual implementation is C#/.NET 10. The pattern choices still
hold; the .NET equivalents are YARP or a minimal-API gateway, Polly for the
circuit breaker, and the built-in ASP.NET Core health checks.

## Target architecture

```
Client -> API Gateway (routing, auth, API Composition, circuit breaker)
            -> Book Service   -> book_db
            -> Loan Service   -> loan_db
            -> Member Service -> member_db
```

Each service owns its own database. Services never reference each other's code or
tables; they talk over HTTP only. The client talks only to the gateway.

The composition endpoint to build later is `GET /api/members/{id}/overview`: the
gateway asks the Member Service, the Loan Service and the Book Service and joins
the results in memory.

## Current state

API Gateway (port 5000, the only published port) in front of the Book, Member and
Loan Service, all .NET 10 minimal APIs, EF Core + Npgsql, migrations applied at
startup, demo seed data (see below), `/health`
endpoint with a database check, Swagger on `/swagger`. Postgres runs in Docker
with one database and user per service.

The gateway (`services/ApiGateway`) is hand-written on purpose (no YARP): it strips
`/api` and forwards `/api/books|members|loans/...` to the owning service, rewrites
`Location` headers, returns 503 when a service is unreachable, hides the internal
`/books/{id}/checkout` and `/return`, and requires an `X-Api-Key` header
(`GATEWAY_API_KEY`) on everything under `/api`. `/health` needs no key. The service
ports 5001-5003 are only opened by `docker-compose.debug.yml`.

API Composition is done: `GET /api/members/{id}/overview` (`MemberOverview.cs`) joins
member, loans and book details, and falls back to a partial answer with `warnings`
when the Loan or Book Service is down (503 only when the Member Service is down).
Every gateway HttpClient has a Polly pipeline (`Microsoft.Extensions.Http.Resilience`,
`Resilience.cs`): retry (GET only, 2 extra attempts) → circuit breaker per service
(50% failures, min 4 calls, 10s window, 15s break) → timeout per attempt (2s, 12s
for the Loan Service). No connection, timeouts and 500/502/504 count as failures;
503 and 4xx do not. Values come from `Resilience__*` (`CIRCUIT_*` in `.env`). State
changes are logged and shown on `GET /api/diagnostics/circuits` (only when
`Gateway__EnableDiagnostics=true`). The Book Service has demo-only fault injection
(`FaultInjection__DelayMs`, `FaultInjection__FailureRate`, off by default). The
gateway adds and forwards an `X-Correlation-Id`. Docker Compose healthchecks call
`/health` with curl (installed in each Dockerfile) and the gateway waits for the
three services to be healthy.

Saga: `DELETE /api/members/{id}` starts an orchestrated "delete member" saga in the
Member Service (`DeletionSagaRunner.cs`, `DeletionSagaWorker.cs`, table
`DeletionSagas` in member_db) and returns 202; `GET /api/members/{id}/deletion`
shows its state. Steps: 1 status `PendingDeletion` (compensatable), 2 Loan Service
`GET /loans/active` (pivot), 3 `POST /loans/anonymize` (idempotent, retryable),
4 anonymize the member, status `Deleted`. Members have a `Status`
(`Active`/`PendingDeletion`/`Deleted`); the Loan Service only lends to `Active`
members. `/loans/active` and `/loans/anonymize` are hidden by the gateway.

Seed data (only on empty tables) is the demo data from `docs/DEMO.md`: 7 books,
5 members, 5 loans. Each service seeds its own database; the loan seed and the
book copy counts are kept in step by hand.

Sprint 6 (externalized config, logging with a request ID, reflection) is next.

The Loan Service calls the Member Service (is the member blocked?) and the Book
Service (`POST /books/{id}/checkout` and `/return`, atomic `UPDATE ... WHERE`) over
HTTP with a 5 second timeout, and returns 503 when one of them is unreachable. It
gives the copy back if saving the loan fails. Borrowing is deliberately not a saga;
only deleting a member is.

## Conventions

- `README.md` holds the recipe for adding a new service and for creating EF Core
  migrations. Follow it rather than inventing a different layout.
- Configuration comes from environment variables (see `.env.example`); no secrets
  in code.
- Every service gets a `/health` endpoint that also checks its database connection.
- Keep the code explainable: it is assessed on whether the author can justify it.

## Branching

- `main` is stable and only receives merges from `develop`; CI/CD will build from it later.
- `develop` is the integration branch where features are tested together.
- New work goes on `feature/<short-name>` branched from `develop`, merged back
  into `develop` via a pull request. Never commit directly to `main`.
