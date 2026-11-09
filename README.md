# Library microservices

A library lending system built as microservices. It has an API Gateway, three
services (Book, Member and Loan Service) and one Postgres container. Every
service gets its own database, and clients only talk to the gateway.

```
services/ApiGateway    API Gateway (the only entry point, port 5000)
services/BookService   Book Service (.NET 10, Minimal APIs, EF Core + Npgsql)
services/MemberService Member Service (same setup, its own database)
services/LoanService   Loan Service (calls the other two over HTTP)
db/init                Postgres init script that creates one database + user per service
docs                   Project documentation
docker-compose.yml     Starts Postgres and all services
docker-compose.debug.yml  Opens the service ports for debugging
docker-compose.prod.yml   Production on the EduCloud VM (see docs/03-deployment.md)
deploy                 Script that deploys one service on the VM
.github/workflows      One build-and-deploy pipeline per service
.env.example           Passwords and the API key; copy to .env to change them
```

## Start it

You only need Docker.

```bash
docker compose up --build
```

Everything is reached through the gateway on http://localhost:5000. The
services themselves have no port on your machine; they are only reachable
inside the Docker network. On startup each service applies its EF Core migrations. Books and members get seed data
if their table is empty (5 books, 3 members); loans start empty.

Passwords: without a `.env` file, Compose uses the defaults from `.env.example`.
To change them, `cp .env.example .env` and edit `.env`.

The init script only runs when the Postgres volume is empty. After changing it
(or a password), reset the database with:

```bash
docker compose down -v
```

## API Gateway

Every request under `/api` needs the header `X-Api-Key` (default `dev-api-key`,
set with `GATEWAY_API_KEY`); without it the gateway answers 401. The gateway
removes `/api` and forwards the request to the service that owns the resource:

| Gateway (port 5000)  | Forwarded to                |
|----------------------|-----------------------------|
| `/api/books/...`     | Book Service `/books/...`   |
| `/api/members/...`   | Member Service `/members/...` |
| `/api/loans/...`     | Loan Service `/loans/...`   |
| `/health`            | the gateway itself, no key needed |

`/api/books/{id}/checkout` and `/return` give 404: only the Loan Service may
call them, so clients borrow through `POST /api/loans`. The same goes for
`/api/loans/active` and `/api/loans/anonymize`, which only the Member Service's
deletion saga uses. If a service cannot be
reached, the gateway answers 503.

### Member overview (API Composition)

`GET /api/members/{id}/overview` is answered by the gateway itself. It asks the
Member Service for the member and the Loan Service for their loans (both at the
same time), then the Book Service for all borrowed books in one call
(`GET /books?ids=1&ids=3`), and joins the answers:

```json
{
  "member": { "id": 1, "name": "Anna de Vries", "cardNumber": "LIB0001", "isBlocked": false },
  "loans": [
    { "id": 4, "bookId": 1, "book": { "id": 1, "title": "The Pragmatic Programmer", "author": "...", "isbn": "..." },
      "loanedAt": "...", "dueDate": "...", "returnedAt": null }
  ],
  "warnings": [],
  "degraded": []
}
```

| Service down   | Result |
|----------------|--------|
| Member Service | 503: without the member there is nothing to show |
| Loan Service   | 200 with `"loans": null`, a warning and `"degraded": ["loan-service"]` |
| Book Service   | 200 with `"book": null` on each loan, a warning and `"degraded": ["book-service"]` |

404 means the member does not exist.

```bash
curl -H "X-Api-Key: dev-api-key" localhost:5000/api/members/1/overview
```

### Timeouts, retries and circuit breaker

The gateway gives every request an `X-Correlation-Id` header (or keeps the one
the client sent), passes it to the service and returns it in the response.

Every call from the gateway to a service goes through a Polly pipeline
(`Microsoft.Extensions.Http.Resilience`, see `services/ApiGateway/Resilience.cs`).
Each service has its own pipeline, so a broken Book Service does not affect calls
to the other two. From outside to inside:

1. **Retry**: a failed `GET` is tried at most 2 more times, after about 0.2 s and
   0.4 s (exponential backoff with jitter). `POST`, `PUT` and `DELETE` are never
   retried: a `POST /loans` that timed out may still have worked, and trying it
   again could borrow the same book twice.
2. **Circuit breaker**: when at least half of the calls in the last 10 seconds
   failed (and there were at least 4), the circuit *opens*. For 15 seconds the
   gateway answers 503 straight away without calling that service. After that
   the next call is a trial (*half-open*). If it works the circuit *closes*
   again, if it fails it opens for another 15 seconds.
3. **Timeout per attempt**: 2 seconds, 12 for the Loan Service (its `POST /loans`
   can wait up to 5 seconds on each of two other services).

A failure is: no connection, the timeout, or a 500/502/504 answer. A 503 does not
count: the Loan Service answers 503 when the Book Service is down, and that should
not open the circuit of the healthy Loan Service. 4xx answers never count.

Every state change is logged with the service name (`docker compose logs -f api-gateway`),
and so is every retry and timeout. A 503 from the gateway tells you which service
it is about and what its circuit is doing:

```json
{ "status": 503, "detail": "...", "error": "book-service unavailable", "circuit": "Open" }
```

**Configuration** (Externalized Configuration): the breaker values come from the
`CIRCUIT_*` variables in `.env` (see `.env.example`). Inside the gateway they are
`Resilience__FailureRatio`, `Resilience__MinimumThroughput`,
`Resilience__SamplingDurationSeconds`, `Resilience__BreakDurationSeconds`,
`Resilience__TimeoutSeconds` and `Resilience__RetryAttempts`. Put the service
name in between to change one service only, e.g. `Resilience__BookService__TimeoutSeconds=5`.

**Diagnostics** (demo only, `Gateway__EnableDiagnostics=true`, on in
`docker-compose.yml`, off in production):

```bash
curl -H "X-Api-Key: dev-api-key" localhost:5000/api/diagnostics/circuits
# {"book-service":"Open","loan-service":"Closed","member-service":"Closed"}
```

Polly only goes from Open to HalfOpen when a call comes in after the 15 seconds,
so the state stays `Open` until someone calls that service again.

**See it happen**: `docker compose stop book-service`, then call
`/api/members/1/overview` or `/api/books/1` a few times. The first calls take a
moment (retries), then the circuit opens and the 503 comes back in a few
milliseconds. `docker compose start book-service`, wait 15 seconds, call it again:
the logs show Open → HalfOpen → Closed.

**Fault injection** (demo only): the Book Service can be made slow or broken
without stopping it. Set the values in `.env` (or in the shell) and recreate only
that container:

```bash
BOOK_FAULT_DELAY_MS=3000 docker compose up -d book-service      # every call takes 3 s -> timeouts
BOOK_FAULT_FAILURE_RATE=0.7 docker compose up -d book-service   # 70% of calls get a 500
docker compose up -d book-service                               # back to normal
```

(In PowerShell: `$env:BOOK_FAULT_DELAY_MS=3000; docker compose up -d book-service`,
and `Remove-Item Env:BOOK_FAULT_DELAY_MS` to turn it off again.)
`/health` is never affected, so Docker keeps seeing the service as healthy.

### Health checks

Every service has a `/health` endpoint that also checks its database. Docker
Compose calls it every 5 seconds (with `curl`, which the Dockerfiles install),
so `docker compose ps` shows `healthy` or `unhealthy` per service. The gateway
waits until all three services are healthy before it starts.

```bash
curl -H "X-Api-Key: dev-api-key" localhost:5000/api/books
```

(On Windows PowerShell, use `curl.exe` instead of `curl`.)

The endpoint tables below list each service's own paths; through the gateway
they start with `/api`.

## Debugging a single service

To use a service's own Swagger page, start with the debug file, which opens
the service ports again:

```bash
docker compose -f docker-compose.yml -f docker-compose.debug.yml up --build
```

Then open http://localhost:5001/swagger (books), http://localhost:5002/swagger
(members) or http://localhost:5003/swagger (loans). Click an endpoint, then
**Try it out** and **Execute**. Without the debug file these ports are closed.

## Book Service endpoints

| Method | Path          | Success | Errors   |
|--------|---------------|---------|----------|
| GET    | /books        | 200     |          |
| GET    | /books?ids=1&ids=3 | 200 |        |
| GET    | /books/{id}   | 200     | 404      |
| POST   | /books        | 201     | 400, 409 |
| PUT    | /books/{id}   | 200     | 400, 404, 409 |
| DELETE | /books/{id}   | 204     | 404      |
| POST   | /books/{id}/checkout | 200 | 404, 409 |
| POST   | /books/{id}/return   | 200 | 404, 409 |
| GET    | /health       | 200     | 503      |

`/books?ids=...` returns only those books (unknown ids are left out); the
gateway's member overview uses it to get every title in one call.

`checkout` lowers `availableCopies` by 1 and `return` raises it by 1; they are
called by the Loan Service and not reachable through the gateway. 409 means no copy is available (checkout) or all
copies are already in (return). The check and the change are one SQL `UPDATE`,
so two checkouts at the same moment can never both take the last copy.

`/health` returns `Healthy` when the service can reach its database and
`Unhealthy` (503) when it cannot.

409 means another book already has that ISBN. ISBNs must be 10 or 13 digits;
hyphens are allowed and are removed before saving.

```bash
curl -H "X-Api-Key: dev-api-key" localhost:5000/api/books/1

curl -X POST localhost:5000/api/books \
  -H "X-Api-Key: dev-api-key" -H "Content-Type: application/json" \
  -d '{"title":"Refactoring","author":"Martin Fowler","isbn":"978-0-13-475759-9","totalCopies":2,"availableCopies":2}'

curl -X DELETE -H "X-Api-Key: dev-api-key" localhost:5000/api/books/6
```

## Member Service endpoints

| Method | Path          | Success | Errors        |
|--------|---------------|---------|---------------|
| GET    | /members      | 200     |               |
| GET    | /members/{id} | 200     | 404           |
| POST   | /members      | 201     | 400, 409      |
| PUT    | /members/{id} | 200     | 400, 404, 409 |
| DELETE | /members/{id} | 202     | 404, 409      |
| GET    | /members/{id}/deletion | 200 | 404     |
| GET    | /health       | 200     | 503           |

409 means another member already has that card number. Card numbers are
stored without spaces and in capitals, so `lib 0004` becomes `LIB0004`.
Member 3 (Carla Bakker) is seeded as blocked, for the Loan Service later.

Every member has a `status`: `Active`, `PendingDeletion` (a deletion is
running) or `Deleted`. Only `Active` members can borrow books or be changed
with `PUT` (409 otherwise).

### Deleting a member (Saga)

`DELETE /members/{id}` starts a **saga**, answers `202 Accepted` at once, and
points with `Location` to `GET /members/{id}/deletion`, which shows the saga's
`state`, `currentStep`, `failureReason` and `retryCount`. The member is not
removed but anonymized: name and card number are cleared, status `Deleted`. Their
loans stay in the Loan Service without the member id.

The deletion changes two databases (member_db and loan_db), and there is no
transaction across two databases. So the Member Service orchestrates it as four
local steps and saves the saga state (table `DeletionSagas` in member_db) after each:

| # | Step | Service | Type | When it fails |
|---|------|---------|------|---------------|
| 1 | Status → `PendingDeletion` | Member | compensatable | – (part of the DELETE request) |
| 2 | Are there active loans? (`GET /loans/active`) | Loan | pivot | Active loans: undo step 1 (status back to `Active`), saga `Failed` |
| 3 | Anonymize loan history (`POST /loans/anonymize`) | Loan | retryable | Retry until it works |
| 4 | Anonymize member, status `Deleted` | Member | retryable | Retry until it works |

A background worker (`DeletionSagaWorker.cs`) runs the steps and retries a step
when the Loan Service is unreachable (2, 4, 8, 16, then every 30 seconds). Before
the pivot it gives up after `Saga__MaxAttemptsBeforePivot` (default 10) attempts
and compensates; after the pivot it never gives up. Step 1 also works as a lock:
the Loan Service refuses new loans while the member is `PendingDeletion`.

Every saga step is logged with the saga id, and the calls to the Loan Service
carry it in `X-Correlation-Id`, so one deletion can be followed through both
services: `docker compose logs -f member-service loan-service`.

```bash
curl -i -X DELETE -H "X-Api-Key: dev-api-key" localhost:5000/api/members/2
curl -H "X-Api-Key: dev-api-key" localhost:5000/api/members/2/deletion
```

```bash
curl -H "X-Api-Key: dev-api-key" localhost:5000/api/members

curl -X PUT localhost:5000/api/members/2 \
  -H "X-Api-Key: dev-api-key" -H "Content-Type: application/json" \
  -d '{"name":"Bram Jansen","cardNumber":"LIB0002","isBlocked":true}'
```

## Loan Service endpoints

| Method | Path               | Success | Errors             |
|--------|--------------------|---------|--------------------|
| GET    | /loans             | 200     |                    |
| GET    | /loans?memberId=1  | 200     |                    |
| GET    | /loans/{id}        | 200     | 404                |
| POST   | /loans             | 201     | 400, 409, 503      |
| POST   | /loans/{id}/return | 200     | 404, 409, 503      |
| GET    | /health            | 200     | 503                |

`POST /loans` takes `{"memberId": 1, "bookId": 2}` and does three steps:

1. Ask the Member Service for the member: 400 if it does not exist, 409 if
   the member is blocked or not `Active` (being deleted, or deleted).
2. Ask the Book Service to check out one copy: 400 if the book does not
   exist, 409 if no copy is available.
3. Save the loan, due in 14 days. If saving fails, the copy is given back.

503 means the Member or Book Service could not be reached within 5 seconds.
The Loan Service keeps working for everything that only needs its own
database, such as listing loans. A loan only stores `memberId` and `bookId`,
not titles or names: those belong to the other services.

The addresses of the other services come from `Services__MemberService` and
`Services__BookService` (see `docker-compose.yml`).

Two endpoints are only for the deletion saga in the Member Service and are not
reachable through the gateway: `GET /loans/active?memberId=1` (loans not returned
yet) and `POST /loans/anonymize` with `{"memberId": 1}` (sets `memberId` to
`null` on all their loans). The anonymize call is idempotent: a second call
changes nothing and still answers 200, so the saga can safely retry it.

```bash
curl -X POST localhost:5000/api/loans \
  -H "X-Api-Key: dev-api-key" -H "Content-Type: application/json" \
  -d '{"memberId":1,"bookId":2}'

curl -H "X-Api-Key: dev-api-key" "localhost:5000/api/loans?memberId=1"

curl -X POST -H "X-Api-Key: dev-api-key" localhost:5000/api/loans/1/return
```

## Add an EF Core migration

Needs the .NET 10 SDK and the EF tool (`dotnet tool install --global dotnet-ef`).

1. Change the model, e.g. `Book.cs` or `BookDbContext.cs`.
2. From the service folder (here `services/BookService`; for the Member Service
   use `ConnectionStrings__MemberDb`), run:

   ```bash
   # bash
   ConnectionStrings__BookDb="Host=unused" dotnet ef migrations add <MigrationName>
   ```
   ```powershell
   # PowerShell
   $env:ConnectionStrings__BookDb = "Host=unused"; dotnet ef migrations add <MigrationName>
   ```

   The connection string only has to exist; creating a migration does not
   connect to the database.
3. Commit the new files in `Migrations/`. They are applied automatically the
   next time the service starts.

## How to add the next service

Example: Member Service (this is how it was added).

1. Copy `services/BookService` to `services/MemberService`. Rename the
   `.csproj`, the namespace, the DbContext and the `BookService.dll` line in
   the Dockerfile. Delete the `Migrations` folder and create a new
   `InitialCreate` migration for the new model.
   Never reference another service's code; services only talk over HTTP.
2. Add a database to `db/init/01-create-databases.sh`:
   ```bash
   create_db member_db member_user "$MEMBER_DB_PASSWORD"
   ```
3. Add `MEMBER_DB_PASSWORD` to `.env.example` (and your `.env`).
4. In `docker-compose.yml`, pass the password to Postgres and add the service:
   ```yaml
   postgres:
     environment:
       MEMBER_DB_PASSWORD: ${MEMBER_DB_PASSWORD:-member_password}

   member-service:
     build: ./services/MemberService
     environment:
       ConnectionStrings__MemberDb: Host=postgres;Port=5432;Database=member_db;Username=member_user;Password=${MEMBER_DB_PASSWORD:-member_password};GSS Encryption Mode=Disable
     depends_on:
       postgres:
         condition: service_healthy
     healthcheck: *service-healthcheck
   ```
   Do the same in `docker-compose.prod.yml`, and add the service to the
   gateway's `depends_on`. No `ports:` here: only the gateway is reachable from outside. Add the
   port to `docker-compose.debug.yml` instead (`"5002:8080"`).
5. Make it reachable through the gateway: add
   `Services__MemberService: http://member-service:8080` to the `api-gateway`
   environment in `docker-compose.yml` and `MapRoute("/api/members", "MemberService");`
   plus `"MemberService"` in the list of HttpClients in `services/ApiGateway/Program.cs`.
6. Run `docker compose down -v` so the init script runs again, then
   `docker compose up --build`.

## Branching strategy

```
main        stable, only receives merges from develop (CI/CD will build from here)
 └ develop  integration branch: features are merged and tested together here
    └ feature/<short-name>   one branch per feature, branched from develop
```

1. Start a feature from the latest `develop`:
   ```bash
   git switch develop
   git pull
   git switch -c feature/member-service
   ```
2. Commit on the feature branch, push it, and open a pull request into `develop`.
3. After merging, run everything together on `develop` (`docker compose up --build`).
4. When `develop` is stable, open a pull request from `develop` into `main`.

Never commit directly to `main`.
