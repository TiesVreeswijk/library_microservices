# Demo script

This is how I demo my library system. It has six scenarios. Each one says what I'm about to
show, which pattern it shows, the exact steps, what you should see, and why it matters.

All requests are also in [`demo.http`](demo.http) (Rider / VS Code) and in the Postman
collection in [`postman/`](postman/), in the same order. The diagrams I point at are in
[`diagrams/`](diagrams/).

## Before the demo

1. Start with fresh demo data (see [Reset the demo data](#reset-the-demo-data)):

   ```bash
   docker compose down -v
   docker compose up -d --build
   docker compose ps          # wait until everything says (healthy)
   ```

2. Open a second terminal with the logs of the three parts I talk about most:

   ```bash
   docker compose logs -f api-gateway member-service loan-service
   ```

   Add `book-service` at the end when I show fault injection (scenario 3).

3. Open `docs/demo.http` in Rider (or import the Postman collection).

**The demo data** (seeded by each service into its own database):

| Member | Who | Loans | Used in |
|---|---|---|---|
| 1 | Anna de Vries | 2 books out (The Pragmatic Programmer, Domain-Driven Design), 1 returned | 1, 2, 4 |
| 2 | Bram Jansen | only returned books (2) | 5 |
| 3 | Carla Bakker | blocked, no loans | – |
| 4 | Daan Visser | no loans | 6 |
| 5 | Eva Smit | no loans, spare for repeating 5 or 6 | – |

There are 7 books. Book 5 (Domain-Driven Design) has its only copy out with Anna.

**About "logging in":** my gateway doesn't have a login. Clients send an API key in the
`X-Api-Key` header (`dev-api-key`, set with `GATEWAY_API_KEY`). The gateway checks it once for
all services, which is the authentication part of the API Gateway pattern. So scenario 1 shows
the key check instead of a login with a token.

---

## Scenario 1: normal flow

**What I show:** one entry point, the key check, and the overview that combines three services.

**Patterns:** API Gateway (routing + authentication), API Composition, Database per Service.

**Steps:**

1. `GET /health`, without a key. Gives `Healthy`: health checks don't need a key.
2. `GET /api/books` without a key. Gives **401**: the gateway stops it before any service sees it.
3. `GET /api/books` with the key. Gives the 7 books. The gateway removed `/api` and forwarded the
   request to the Book Service.
4. `GET /api/loans?memberId=1`. The Loan Service only knows ids (`bookId: 1`), no titles,
   because the titles live in another database.
5. `GET /api/members/1/overview`. Anna, her 3 loans, and the title of every book.
   `"degraded": []`.

**What you should see:** the overview has data from three databases in one answer. Every
response has an `X-Correlation-Id` header.

**Why it matters:** in a monolith the overview is one SQL query with two JOINs. Here
member_db, loan_db and book_db are separate, so a JOIN is impossible. The gateway asks the
Member and Loan Service at the same time, then the Book Service for all titles in one call
(`/books?ids=1&ids=5&ids=2`), and joins it in memory. The client doesn't know any of that.

---

## Scenario 2: the Book Service goes down

**What I show:** a service is gone, but the rest keeps working, and the circuit breaker stops
the gateway from waiting for it every time.

**Patterns:** Circuit Breaker, API Composition with a fallback, Health Check API.

**Steps:**

1. `GET /api/diagnostics/circuits`. All three are `Closed`.
2. In the terminal: `docker compose stop book-service`
3. `GET /api/members/1/overview`. Takes **a few seconds** (up to about 6): every attempt waits
   for the 2 second timeout, then retries. In the logs: `book-service: no answer within 2s` and
   `attempt 1 failed (TimeoutRejectedException), retrying in ... ms`.
   The answer is still **200**, with Anna and her loans, but `"book": null` and
   `"degraded": ["book-service"]`.
4. Somewhere in the first or second request the logs show
   `Circuit for book-service: Closed/HalfOpen -> OPEN for 15s`. (When exactly depends on how many
   Book Service calls just succeeded: the breaker looks at the last 10 seconds, so the calls from
   scenario 1 can still count.)
5. Send it once more. Now it's back in a **few milliseconds**: the gateway doesn't even try.
6. `GET /api/books/1`. **503** straight away:
   `"error": "book-service unavailable", "circuit": "Open"`.
7. `GET /api/diagnostics/circuits`. `book-service` is `Open`, the other two are still `Closed`.
8. In the terminal: `docker compose start book-service`, then `docker compose ps` until it's
   `(healthy)`, and wait until the 15 seconds are over.
9. `GET /api/books/1`. **200**. The logs show
   `Open -> HALF-OPEN, letting one trial call through` and then `HalfOpen -> CLOSED`.
10. `GET /api/diagnostics/circuits`. Everything `Closed` again.

**What you should see:** a slow first answer, then fast answers once the circuit is open, a
partial overview instead of an error, and the state going Closed → Open → Half-open → Closed
(diagram: [`diagrams/circuit-breaker.md`](diagrams/circuit-breaker.md)).

**Why it matters:** without a breaker every request would wait for the timeout, the
gateway's threads would pile up, and one broken service would make the whole system slow. The
breaker fails fast, so the client gets an answer right away. Each service has its own circuit,
so the Member and Loan Service keep working. The overview chooses to give a partial answer,
because a member without book titles is still useful.

One thing that surprised me: the diagnostics endpoint keeps saying `Open` after the 15
seconds. Polly only switches to Half-open when the next call comes in, and that call is the trial.

---

## Scenario 3: the Book Service is slow or flaky

**What I show:** a service that is running (and healthy!) but slow or broken. Stopping the
container doesn't show that case, so I use fault injection.

**Patterns:** Circuit Breaker, timeouts and retries (Polly), Externalized Configuration.

**Steps (slow):**

1. Turn on a 3 second delay in the Book Service (recreates only that container):

   ```bash
   # bash
   BOOK_FAULT_DELAY_MS=3000 docker compose up -d book-service
   ```
   ```powershell
   # PowerShell
   $env:BOOK_FAULT_DELAY_MS=3000; docker compose up -d book-service
   ```

   The Book Service logs `Fault injection is ON: 3000 ms delay`.
2. `docker compose ps`. The Book Service is still `(healthy)`: `/health` isn't delayed. The
   service is "up" but useless, which is exactly the case a health check alone doesn't catch.
3. `GET /api/books/1`. After about 6 seconds: **503**. The logs show 3 timeouts with retries in between.
4. `GET /api/books/1` again. The circuit opens during this request; 503 after about 2 seconds.
5. `GET /api/books/1` again. 503 in milliseconds.

**Steps (flaky):**

1. Switch to "30% of the requests fail with a 500":

   ```bash
   # bash
   BOOK_FAULT_FAILURE_RATE=0.3 docker compose up -d book-service
   ```
   ```powershell
   # PowerShell
   Remove-Item Env:BOOK_FAULT_DELAY_MS; $env:BOOK_FAULT_FAILURE_RATE=0.3; docker compose up -d book-service
   ```

2. Wait 15 seconds, then send `GET /api/books/1` a few times. The first answers are **200**,
   even though some of the Book Service's answers were 500s: the logs show
   `attempt 1 failed (HTTP 500), retrying`, and the retry saved the request. After a few requests
   the circuit opens anyway. My demo thresholds are very low (4 calls, 50%), so two unlucky 500s
   close together are enough. That's the trade-off: a sensitive breaker protects quickly, but it
   also gives up on a service that mostly works. To show only the retries, make the breaker less
   sensitive first, e.g. `CIRCUIT_MINIMUM_THROUGHPUT=20` and `docker compose up -d api-gateway`.
3. Turn it off again:

   ```bash
   # bash (leave the variables out)
   docker compose up -d book-service
   ```
   ```powershell
   # PowerShell
   Remove-Item Env:BOOK_FAULT_FAILURE_RATE; docker compose up -d book-service
   ```

**What you should see:** timeouts and retries in the logs, the breaker tripping on slowness,
and retries rescuing requests when the failures are random, until the breaker decides there are
too many.

**Why it matters:** a slow service is worse than a dead one, because every caller waits. The
timeout (2s) puts a limit on waiting, the retry hides short hiccups, and the breaker stops the
retries from making things worse. Only `GET` is retried: retrying a `POST /loans` that timed out
could borrow the same book twice, because the first attempt may have worked. All values (timeout,
retries, ratio, break time) come from environment variables (`CIRCUIT_*` in `.env.example`), so I
can tune them without rebuilding.

---

## Scenario 4: delete a member who still has books

**What I show:** a saga that stops at its decision step and undoes what it already did.

**Patterns:** Saga (orchestration, compensation).

**Steps:**

1. `GET /api/members/1/overview`. Anna has 2 books that are not returned (`returnedAt: null`).
2. `DELETE /api/members/1`. **202 Accepted**, a `Location: /api/members/1/deletion` header, and
   the saga: `"state": "Running"`, `"currentStep": "CheckActiveLoans"`.
3. `GET /api/members/1/deletion`. `"state": "Failed"`,
   `"failureReason": "The member still has 2 book(s) on loan."`
4. `GET /api/members/1`. `"status": "Active"` again.
5. In the logs, the same saga id in both services:

   ```
   member-service | Saga 4d44... member 1: STARTED (request 21b0...), step 1 MarkPendingDeletion done
   loan-service   | Correlation 4d44...: member 1 has 2 active loans
   member-service | Saga 4d44... member 1: step 2 CheckActiveLoans says NO (2 active loans), compensating
   member-service | Saga 4d44... member 1: compensated step 1, member is Active again, saga FAILED
   ```

**What you should see:** step 1 happened (status `PendingDeletion`), step 2 said no, and step 1
was compensated (status back to `Active`).

**Why it matters:** the saga has four steps in two services
(diagram: [`diagrams/delete-member-saga.md`](diagrams/delete-member-saga.md)):

| # | Step | Service | Type |
|---|------|---------|------|
| 1 | Status → `PendingDeletion` | Member | compensatable |
| 2 | Any active loans? | Loan | pivot (go / no-go) |
| 3 | Anonymize loan history | Loan | retryable |
| 4 | Anonymize member, status `Deleted` | Member | retryable |

Steps before the pivot can be undone; after the pivot the saga only goes forward. You can't
"roll back" step 1 like a database transaction, because it is already saved in member_db. So the
saga does the opposite instead: a compensating action.

Why step 1 exists at all: it works as a lock. While Anna is `PendingDeletion` the Loan Service
refuses to lend her a book, so she can't borrow something between the check in step 2 and the
anonymizing in steps 3 and 4.

---

## Scenario 5: delete a member without active loans

**What I show:** the happy path of the saga, and that the loan history survives without saying
who it belonged to.

**Patterns:** Saga, Database per Service.

**Steps:**

1. `GET /api/loans?memberId=2`. Bram has 2 loans, both returned.
2. `DELETE /api/members/2`. **202 Accepted**.
3. `GET /api/members/2/deletion`. `"state": "Completed"`, `"currentStep": "Done"`.
4. `GET /api/members/2`. `"name": ""`, `"cardNumber": null`, `"status": "Deleted"`.
5. `GET /api/loans?memberId=2`. `[]`: no loan points to Bram any more.
6. `GET /api/loans`. Bram's two loans are still there, with `"memberId": null`.
7. `POST /api/loans` with `{"memberId": 2, "bookId": 3}`. **409**: `Member 2 is Deleted and cannot borrow books.`
8. `DELETE /api/members/2` again. **409**: `Member 2 is already deleted.`

**What you should see:** in the logs, steps 1 → 2 (pivot passed) → 3 (2 loans anonymized) → 4 →
`COMPLETED`, all with the same saga id in the Member and Loan Service.

**Why it matters:** the member is anonymized, not deleted, so the library still knows how often
a book was borrowed. Step 3 is **idempotent**: calling `POST /loans/anonymize` twice gives the same
result (the second time 0 loans change). That matters because the saga retries it when it gets
no answer, and maybe the first call did work and only the answer got lost.

---

## Scenario 6: delete a member while the Loan Service is down

**What I show:** the most important one. The deletion touches two databases and one of them
is unreachable. There is no transaction that covers both, so how does the system stay correct?

**Patterns:** Saga (saved state, retries, eventual consistency), Circuit Breaker.

**Steps:**

1. In the terminal: `docker compose stop loan-service`
2. `DELETE /api/members/4`. Still **202 Accepted**. Step 1 only needs member_db, so the Member
   Service can start the deletion on its own.
3. `GET /api/members/4/deletion` a few times, a few seconds apart. `"state": "Running"`,
   `"currentStep": "CheckActiveLoans"`, `"retryCount"` going up, and
   `"failureReason": "Waiting for the Loan Service (...), attempt 3."`.
   In the logs: `could not reach the Loan Service ... retry 3 in 8s`, and the Member Service's
   own circuit to the Loan Service opening.
4. `GET /api/members/4`. `"status": "PendingDeletion"`.
5. In the terminal: `docker compose start loan-service`
6. If I'm quick (before the next retry): `POST /api/loans` with `{"memberId": 4, "bookId": 3}`.
   **409**: `Member 4 is PendingDeletion and cannot borrow books.` That's the lock from step 1.
7. Wait (up to 30 seconds, the longest wait between retries) and `GET /api/members/4/deletion`.
   `"state": "Completed"`. Nobody had to do anything: the background worker finished it.
8. `GET /api/members/4`. `"status": "Deleted"`.

**What you should see:** the saga waits instead of failing, retries with growing waits (2, 4, 8,
16, then every 30 seconds), and finishes by itself once the Loan Service is back.

**Why it matters:** in a monolith "delete member and anonymize their loans" is one transaction:
all or nothing. Here member_db and loan_db are separate databases in separate services, and a
transaction across both (two-phase commit) is something I deliberately don't use: it would lock
both services together, and if one is down nothing works. The saga instead:

- splits the work into **local transactions**, one per step, each in one database;
- **saves its state after every step** (table `DeletionSagas` in member_db). The
  `BackgroundService` finds unfinished sagas and continues them, even after a restart of the
  Member Service;
- accepts that for a while the data is **not consistent yet** (member `PendingDeletion`, loans
  not anonymized yet), but makes sure it **becomes** consistent (eventual consistency);
- has a rule per step for failure: before the pivot it may give up and compensate (after 10
  attempts, `Saga__MaxAttemptsBeforePivot`), after the pivot it keeps retrying until it works.

Here the saga waits at **step 2**, the pivot, because the Loan Service was already down when it
started. If the Loan Service had gone down right after step 2, the saga would wait at step 3 in
exactly the same way, with one difference: there it never gives up, because the decision to
delete has already been made.

---

## Reset the demo data

Every scenario changes data (deleted members, anonymized loans). To start over:

```bash
docker compose down -v        # -v removes the Postgres volume, so ALL data is gone
docker compose up -d --build  # migrations run again and every service seeds its demo data
```

Also make sure fault injection is off: no `BOOK_FAULT_*` variables in your shell or `.env`.
In PowerShell: `Remove-Item Env:BOOK_FAULT_DELAY_MS, Env:BOOK_FAULT_FAILURE_RATE -ErrorAction SilentlyContinue`.

To repeat only scenario 5 or 6 without a reset, use member 5 (Eva Smit) instead.

## Logs

```bash
docker compose logs -f api-gateway member-service loan-service   # during the demo
docker compose logs -f api-gateway book-service                  # scenario 3
docker compose logs member-service loan-service | grep <saga id> # one deletion, both services
```

In PowerShell, use `Select-String <saga id>` instead of `grep`.
