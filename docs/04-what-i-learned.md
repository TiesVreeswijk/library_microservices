# What I learned

Ties — November 2026

This is my reflection on the hard parts of the project. Building endpoints and routing turned
out to be the easy part. The interesting lessons all came from the same place: once every
service has its own database and the services talk over a network, things can fail halfway,
and you have to decide what "correct" means when they do.

## 1. There is no transaction across two databases

This was the biggest shift in how I think.

In a monolith, "delete a member and anonymize their loans" would be one database transaction:
either everything happens or nothing does. In my system the member lives in member_db and the
loans in loan_db, owned by two different services. No transaction covers both. I knew that in
theory (it's the price of Database per Service), but I only really felt it when I had to build
something that changes data in both places.

The answer was a **saga**: split the work into local steps, each one a normal transaction in
one database, and decide up front what happens when a step fails.

| # | Step | Service | Type |
|---|------|---------|------|
| 1 | Member → `PendingDeletion` | Member | compensatable |
| 2 | Any active loans? | Loan | pivot |
| 3 | Anonymize loan history | Loan | retryable |
| 4 | Anonymize member → `Deleted` | Member | retryable |

What I learned from building it:

- **Compensation is not a rollback.** Step 1 is already saved in member_db when step 2 says
  "no". There is nothing to roll back. So the saga does a new action that undoes the effect:
  it sets the member back to `Active`. A compensation is just another step that goes forward.
- **The pivot decides everything.** The steps before the pivot can be undone, the steps after
  it can't, so the saga must make sure they *eventually* succeed. That's why my saga gives up
  and compensates when the Loan Service stays unreachable *before* the pivot (after 10
  attempts), but retries forever *after* it. I didn't understand why the pivot mattered until I
  had to write that rule.
- **Step 1 is really a lock.** At first step 1 seemed pointless: why mark the member before
  checking their loans? But without it, the member could borrow a book between the check in
  step 2 and the anonymizing in step 3, and the check would be wrong by the time it mattered.
  `PendingDeletion` makes the Loan Service refuse new loans, so the answer of step 2 stays true.
  This is called a *semantic lock*: a lock that lives in the data, not in the database.
- **Eventual consistency is a real state, not a bug.** While the saga runs, the data is
  "inconsistent": the member is `PendingDeletion` and the loans still point to them. That's
  allowed, as long as the system always ends up consistent. The client gets `202 Accepted` and
  can ask for the status, instead of waiting for a result that may take a minute.
- **The saga's state has to be saved.** If the state only lived in memory, a crash halfway
  would leave a member stuck in `PendingDeletion` forever. So the saga is a table in member_db,
  saved after every step, and a background worker picks up anything that's unfinished. That's
  what made scenario 6 work: I stopped the Loan Service, deleted a member, and when the Loan
  Service came back the saga finished by itself.
- **Orchestration over HTTP was enough.** I always thought a saga needed a message broker,
  which is why it was out of scope at first. An *orchestrated* saga (one service runs the steps
  and remembers where it is) works fine over plain HTTP. A *choreographed* saga (services react
  to each other's events) is the one that really needs a broker.

I also learned where the saga does **not** go. Borrowing a book also touches two services (the
Book Service takes a copy, the Loan Service saves the loan), and if saving the loan fails, the
Loan Service gives the copy back. But if that "give back" call also fails, the copy stays taken.
I accepted that on purpose. Not every multi-step flow needs a full saga, as long as I can
explain where the gap is.

## 2. Retrying is only safe when the operation is idempotent

Retries sound like a free win: if a call fails, just try again. They're not free.

When a call times out, the gateway only knows that **it didn't get an answer**, not that the
call failed. The Loan Service might have created the loan and then been too slow to reply. If
the gateway retried `POST /loans`, the member could end up borrowing the same book twice. So my
gateway only retries `GET`.

The saga does retry a `POST` (`/loans/anonymize`), and that's only safe because I made it
**idempotent**: it sets `MemberId` to null on all loans of that member, so the second call finds
nothing to change and gives the same result. The rule I took away: *before retrying something,
ask what happens if the first attempt actually worked.*

## 3. A slow service is worse than a dead one

A stopped container fails fast: the connection is refused (or, as I found out, DNS times out).
A service that is **running but slow** is worse, because every caller waits for it, and the
callers' callers wait for them. My health checks didn't help here at all: with fault injection
on, the Book Service answered `/health` instantly and Docker reported it as `healthy`, while
every real request took 3 seconds. "Running" is not the same as "working", and "healthy" is not
the same as "fast".

The tools against it, and what each one actually does:

- **Timeout**: puts a limit on how long one call may wait. Without it, HttpClient waits 100
  seconds.
- **Retry**: hides short hiccups. Against a slow service it makes things *worse*: three
  attempts of 2 seconds is 6 seconds of waiting.
- **Circuit breaker**: notices that a service keeps failing and stops calling it for a while,
  so callers get an answer in milliseconds instead of waiting for the timeout. In my demo the
  third request against the slow Book Service took 3 ms instead of 6.5 seconds.

The **order** matters too. In my pipeline the retry is on the outside, then the circuit breaker,
then the timeout on the inside. That way every retry attempt goes through the breaker and
counts as a failure there, and the timeout is per attempt instead of for all attempts together.

## 4. Deciding what counts as a failure is a design decision

I first thought "anything that isn't a 200 is a failure". That's wrong, and it took me a while
to see why.

- **4xx answers are the client's fault**, not the service's. A 404 says the service works fine.
- **A 503 can be someone else's failure.** The Loan Service answers 503 when the *Book* Service
  is down. If the gateway counted that 503 for the Loan Service's circuit, a broken Book Service
  would also cut off the perfectly healthy Loan Service. So my breaker counts no connection,
  timeouts and 500/502/504, but not 503.
- **Timeouts have to fit inside each other.** The Loan Service waits up to 5 seconds for each of
  two other services. A 2 second gateway timeout would cut off normal `POST /loans` requests, so
  the Loan Service gets 12 seconds. A caller's timeout has to be longer than the work it waits for.

## 5. Circuit breaker settings are a trade-off, not a fact

I set the thresholds very low for the demo (open after 50% failures out of only 4 calls), so you
can see it trip right away. Testing the demo taught me what that costs:

- With 30% of the Book Service's requests failing at random, the retries saved the first few
  requests, but then the circuit opened anyway. Two unlucky failures close together are already
  50% of 4 calls. A sensitive breaker protects quickly, but it also gives up on a service that
  mostly works.
- Older calls still count. The breaker looks at the last 10 seconds, so successful calls from
  the previous demo step influenced when it opened. The same steps don't always give the same
  timing.
- **Half-open is lazy.** I expected the circuit to switch to Half-open by itself after 15
  seconds. In Polly it only switches when the next call comes in, and that call is the trial.
  My diagnostics endpoint kept saying `Open` until I sent a request.

Each service gets its **own** circuit. That's what keeps the failure contained: when the Book
Service is down, the Member and Loan Service are still called normally.

## 6. Partial answers are better than no answer, but you have to choose

The member overview combines three services. When one is down, I had to decide per service
whether the overview can do without it:

- No Member Service → 503. Without the member there is nothing to show.
- No Loan Service → the member with `"loans": null`.
- No Book Service → the loans without titles, `"degraded": ["book-service"]`.

That's not a technical decision, it's a product decision: what is still useful to the user? The
technical part is just making sure the client can *see* that the answer is incomplete.

## 7. Database per Service makes simple things awkward

Some things that are trivial with one database became surprisingly fiddly:

- **Seed data across services.** A demo loan lives in loan_db, but the number of available
  copies lives in book_db. No service may touch another's database, so each one seeds its own
  part and I had to keep the numbers in step **by hand**. There's no foreign key to warn me when
  they don't match. The ids only line up because each table starts empty and the rows go in
  in the same order.
- **Anonymizing instead of deleting.** I don't really delete the member, because the loan
  history is still useful (how often was a book borrowed?). But "clear the card number" broke
  the unique index on card numbers, so the field had to become nullable. And the loans keep
  their history with `MemberId = null`.
- **Migrations aren't always right.** EF Core generated `defaultValue: ""` for the new
  `Status` column, which isn't a valid status, so every existing member would have broken. I had
  to fix the migration by hand. Generated code still needs reading.

## 8. Following one action through several services

When one deletion runs through two services, the logs of each service on their own make no
sense. I learned to pass a **correlation id** along: the gateway gives every request an
`X-Correlation-Id`, and the saga sends its own id to the Loan Service. Then
`docker compose logs member-service loan-service | grep <saga id>` shows one deletion from
start to finish.

The flip side: the framework logs so much by default (every request, every SQL query, every
Polly attempt) that the important lines disappear. Turning log levels down through environment
variables (Externalized Configuration again) was what made the logs usable in a demo.

## 9. Testing failure is its own skill

- **Stopping a container is not enough** to test failure. It only shows "dead". To show "slow"
  and "flaky" I added fault injection to the Book Service: a delay or a percentage of 500s,
  turned on with an environment variable.
- **Test on fresh data, in isolation.** The demo has to work right after
  `docker compose up` on an empty database. To check that without wiping my own database, I ran
  everything in a separate Compose project (`docker compose -p ...`) with its own volume.
- **Write down what really happens, not what should happen.** Running the demo script for real
  showed two places where my description was wrong (the circuit opened one request earlier than
  I wrote, and the flaky scenario tripped the breaker). I fixed the text, not the system,
  because what really happened was the more interesting lesson.

## What I would still improve

- The correlation id only goes through forwarded requests and the saga. The overview's own calls
  and the Loan Service's calls to the other services don't carry it yet (sprint 6).
- The saga worker assumes there's only one Member Service. With two instances, both workers could
  pick up the same saga. That would need a lock in the database (`SELECT ... FOR UPDATE SKIP LOCKED`).
- If the saga ever had to publish events instead of calling HTTP endpoints, saving the state
  and sending the event would again be two things that can fail separately. That's the problem
  the Transactional Outbox pattern solves, and now I understand why it exists.
- There are no automated tests. Everything above was checked by hand with the demo script.

## The short version

Splitting a system into services doesn't remove complexity, it moves it. Inside one service,
the database protects you. Between services, you have to decide for yourself what happens when
a call fails, times out, is retried or arrives twice. The patterns (saga, circuit breaker,
idempotency, correlation ids) are mostly names for the decisions you have to make anyway.
