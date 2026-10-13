# Microservices Semester Project: Planning

Ties — 4 October 2026

## About this document

This is the planning for my microservices project. The theory, my pattern choice and the design of the system are in the other document, *Learning Microservices: Semester Project Plan*.

I have two scenarios. My base planning is half a day per week (4 hours) for 12 weeks, so about 48 hours. If my group project turns out to be less work than expected, I can spend 1 or even 2 days a week on this instead. The base planning comes first, then what I'd do with the extra hours.

## Sprint planning (base scenario)

6 sprints of 2 weeks, 4 hours per week, so 8 hours per sprint and 48 hours in total. I want to have something working to show at the end of every sprint.

**Sprint overview (timeline graphic in the Word version):** Sprint 1 (weeks 1–2) theory + design ·
Sprint 2 (weeks 3–4) setup + Book Service · Sprint 3 (weeks 5–6) Loan + Member Service ·
Sprint 4 (weeks 7–8) API Gateway + auth · Sprint 5 (weeks 9–10) composition + resilience ·
Sprint 6 (weeks 11–12) config, logging, reflection. 8 hours per sprint, 48 hours total.

| Sprint | Weeks | Goal | Tasks (hours) | What I have at the end |
| --- | --- | --- | --- | --- |
| 1 | 1–2 | Learn the theory and design the system | Read the microservices.io pages on Microservice Architecture, Monolithic Architecture, API Gateway, Database per Service, API Composition, Health Check API and Circuit Breaker (4h) · Write down in my own words the differences between a monolith and microservices (2h) · Decide on my three services, their endpoints and what data each one owns, and make the architecture diagram (2h) | Notes on the theory + design document with diagram |
| 2 | 3–4 | Set up the project and build the first service | Git repo, folder structure, Docker Compose with Postgres (3h) · Book Service with CRUD endpoints and its own database (4h) · Test it with Postman (1h) | Book Service running in Docker |
| 3 | 5–6 | Two more services, each with their own database | Member Service (3h) · Loan Service (4h) · Write down the problem: the Loan Service only has book IDs, not titles (1h) | Three services, three separate databases |
| 4 | 7–8 | Put the API gateway in front | Gateway with routing to all three services (3h) · Make sure only the gateway is reachable from outside (1h) · JWT or API key check in the gateway (3h) · Test the whole flow (1h) | All traffic goes through one entry point, with authentication |
| 5 | 9–10 | Composition and failure handling | Build the `/members/:id/overview` endpoint that combines three services (3h) · Add timeouts and a circuit breaker with a fallback (3h) · Add a `/health` endpoint to every service and use it in Docker Compose (2h) | A combined endpoint that keeps working when a service goes down |
| 6 | 11–12 | Config, logging, reflection and presentation | Move all config to environment variables (2h) · Logging in the gateway with a request ID (1h) · Write my reflection: what was hard, the pros and cons, what I'd add next (3h) · Prepare demo and presentation (2h) | Finished system, reflection and demo |

After every 4-hour session I'll write a few lines in a learning log: what I did, what I learned and where I got stuck. It only takes a few minutes and it'll help a lot when I write my reflection in sprint 6.

## What I do if I get more hours

If I get more time, there are two ways to use it: go **deeper** into patterns I already have, or **add** new patterns. More patterns is not automatically more learning, so my first choice is depth. I'd rather be able to explain two patterns really well, with measurements to back it up, than say I touched ten.

### Option 1: go deeper (my preference)

I pick one or two patterns that are already in the system and take them much further than the base version. Each deep dive ends with a short write-up with numbers in it, which is much better proof that I understand something than another feature.

| Deep dive | What "deeper" means | Hours |
| --- | --- | --- |
| **API Gateway** | Replace my own minimal-API gateway with YARP, or an off-the-shelf one like Kong, and compare them · Add rate limiting and proper JWT validation with key rotation · Add a second gateway for a mobile client ([Backends for Frontends](https://microservices.io/patterns/apigateway.html)) · Measure how many milliseconds the extra hop costs | ~24 |
| **Resilience** | Add retries with exponential backoff and a bulkhead next to the circuit breaker, all through Polly · Tune the thresholds instead of using the defaults · Use a tool like Toxiproxy to inject fake latency and failures · Document what happens in each circuit breaker state (closed, open, half-open) with graphs | ~24 |
| **Data per service** | Build a second version of the overview endpoint as a [CQRS](https://microservices.io/patterns/data/cqrs.html) read model that is updated through events · Keep both versions side by side and compare response time, how stale the data gets and how much code each one costs | ~32 |

The data deep dive is the most interesting one for my report, because I end up with two working solutions to the same problem and can explain the trade-off with my own measurements. It's also the most work, because it needs events.

### Option 2: add a new pattern

If going deeper feels too narrow, asynchronous communication is the biggest step up in complexity. Services stop waiting for each other and the data is only correct eventually, which is a completely different way of thinking.

| Addition | What I build | Hours |
| --- | --- | --- |
| Messaging | Add RabbitMQ (with MassTransit) and let the Loan Service publish a `LoanCreated` event. The Book Service listens and updates the number of copies itself | ~14 |
| [Saga](https://microservices.io/patterns/data/saga.html) | Make borrowing a book a saga: check member → create loan → update stock, with compensating actions when a step fails | ~16 |
| [Transactional Outbox](https://microservices.io/patterns/data/transactional-outbox.html) | Make sure an event is never lost when a service crashes halfway, by writing it in the same transaction as the data | ~10 |
| [Distributed tracing](https://microservices.io/patterns/observability/distributed-tracing.html) | Zipkin or Jaeger, so I can see in a timeline where the time goes in one request | ~16 |

Other things I could add, but which say more about infrastructure than about microservice patterns: running on Kubernetes instead of Docker Compose, a React frontend, or [service component tests](https://microservices.io/patterns/testing/service-component-test.html).

### What each scenario buys me

| Scenario | Hours per week | Extra hours | What I'd do |
| --- | --- | --- | --- |
| Base | 4 (0.5 day) | – | The 6 sprints above |
| More time | 8 (1 day) | ~48 | One deep dive, or messaging + saga |
| Most time | 16 (2 days) | ~96 | The data deep dive (CQRS vs API Composition), which needs messaging and the saga anyway |

### How I keep this realistic

- **The base plan stays the base plan.** I only start an extra when the base system works end to end. If the hours never come, I still have a complete project.
- **One thing at a time.** Every extra gets its own mini-sprint with its own demo moment, so a half-finished extra never blocks the rest.
- **Measurements over features.** Each extra ends with numbers and a short write-up, not just working code.
- **I'll check with my teacher first.** I want to know whether depth or breadth counts for more in my assessment before I pick.

## Risks and how I keep it manageable

With 48 hours my biggest risk is taking on too much, especially now that the base project is bigger than my first idea. This is how I keep it under control:

- **No frontend.** Postman is enough for the demo. A UI would take a lot of time and doesn't teach me anything about microservices.
- **No Kubernetes or message broker in the base plan.** Docker Compose is enough for 48 hours. Those only come in if I actually get more hours.
- **Problems with Docker.** If the setup takes way longer than planned, I'll first run the services locally with `npm run dev` and move them into Docker later.
- **Falling behind.** If I run out of time, I'll drop things in this order: logging, externalized config, circuit breaker, then the Member Service (then the overview endpoint combines two services instead of three). Three services with their own databases behind a working gateway is the minimum I need to show the pattern.
- **Extra time left?** Then I start with a deep dive from the section above, instead of thinking up something new.
