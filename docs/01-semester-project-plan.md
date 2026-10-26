# Learning Microservices: Semester Project Plan

Ties — 19 September 2026

## Introduction

For this semester I want to learn something complex, and I picked microservices. I'm going to learn it by building a system myself around the **API Gateway** pattern plus a few supporting patterns. My base planning is half a day per week (4 hours) for 12 weeks, so about 48 hours. If my group project turns out to be less work than expected, I can spend 1 or even 2 days per week on this instead. The planning, and what I'd add with those extra hours, is in a separate document: *Microservices Semester Project: Planning*.

My main source is [microservices.io](https://microservices.io/), a site made by Chris Richardson, who also wrote the book *Microservices Patterns*. According to the site, microservices is a way of building an app as two or more services. These are usually split up by business capability, and each one is often owned by a small team ([source](https://microservices.io/)).

The site has a whole [pattern language](https://microservices.io/patterns/) with patterns for things like splitting up services, handling data, communication and external APIs. The starting point for all of them is the [Microservice Architecture pattern](https://microservices.io/patterns/microservices.html): an app made of loosely coupled services that you can deploy separately.

My goal isn't to build something production-ready. I want to actually understand why you would split an app into services, and what problems you run into when you do.

## My pattern choice: API Gateway + supporting patterns

My main pattern is the [API Gateway](https://microservices.io/patterns/apigateway.html). My teacher said my first idea was a bit too simple, so I added four supporting patterns around it. They make the system more realistic, but they're all still synchronous, so I can keep following what happens.

**What is it?** An API gateway is one entry point that sits between the client (like a website) and all the services behind it. The frontend doesn't talk to each service directly. It only talks to the gateway, and the gateway sends each request to the right service. It can also combine data from multiple services into one response, and check if the client is allowed to do something. The site also mentions a variant called Backends for Frontends, where you have a separate gateway for each type of client (web, mobile, etc.).

**Why I think this is the best pattern for a beginner:**

- **Every microservice app needs it.** microservices.io describes it as the pattern for how clients reach your services. You can't really have a working multi-service app without solving this, so it makes sense to start here.
- **It's easy to follow what happens.** A request comes in, gets routed and a response goes out. I can test that with Postman and see it in the logs. Patterns like Saga or CQRS use async messaging and "eventual consistency", which sounds way harder to debug with only 4 hours a week.
- **I still learn the basics along the way.** To have something to route to, I first have to split the app into services with their own API and database and run them separately. That's pretty much what microservices is about.
- **I can add to it step by step.** Once the gateway works I can add login checks, combined responses or error handling later. Each step is small and I can see the result.
- **It fits the stack I'm building in.** Each service is a small ASP.NET Core project, so most of my time goes into the architecture instead of into the framework.

**The supporting patterns I added:**

| Pattern | What I do with it | Why I added it |
| --- | --- | --- |
| [Database per Service](https://microservices.io/patterns/data/database-per-service.html) | Every service gets its own database and the others can only reach that data through its API | Without this my services would share one database and it would still be a monolith |
| [API Composition](https://microservices.io/patterns/data/api-composition.html) | The gateway asks multiple services and joins the data in memory into one response | This is the moment you really feel the downside of separate databases: you can't just write a JOIN anymore |
| [Health Check API](https://microservices.io/patterns/observability/health-check-api.html) | Every service gets a `/health` endpoint through the built-in ASP.NET Core health checks, which also checks its database connection | A service can be running but still broken. Now the gateway and Docker can see that |
| [Circuit Breaker](https://microservices.io/patterns/reliability/circuit-breaker.html) | If a service keeps failing, the gateway (using Polly) stops calling it for a while and returns a fallback | Shows what happens when one service goes down, and how you stop that from breaking everything |
| [Externalized Configuration](https://microservices.io/patterns/externalized-configuration.html) | Connection strings, ports and secrets come from environment variables, not from the code | Needed anyway once you run the same service in multiple environments |

This also means I now have three services instead of two (Book, Loan and Member), so the composition endpoint actually has to combine data from more than one place.

**What I'm still skipping in the base plan:** Saga, CQRS, Event Sourcing and Transactional Outbox. They solve real problems, but they all need a message broker and asynchronous thinking. With 4 hours a week that's too much. If I get more hours they're an option — I describe that in my planning document.

## What I'm going to build

I'm going to build a small **library lending system** with four pieces: an API gateway, a Book Service, a Loan Service and a Member Service, each in its own Docker container.

I kept the business logic simple on purpose, so I can spend my time on the architecture instead of complicated rules. The complexity is in the architecture: three services that each own their own data, a gateway in front of them, and what happens when one of them is slow or down.

**Architecture (diagram in the Word version):** the Client (browser / Postman) talks only to the
API Gateway, which handles routing, authentication, API Composition and the circuit breaker. The
gateway routes to three services — Book Service, Loan Service and Member Service — and each service
talks only to its own database (books DB, loans DB, members DB).

The client only knows about the gateway, and each service only uses its own database.

| Part | What it does | Example endpoints |
| --- | --- | --- |
| API Gateway | The one entry point: sends requests to the right service, checks the token, combines responses, and trips the circuit breaker when a service keeps failing | `GET /api/books`, `GET /api/members/:id/overview` |
| Book Service | Keeps track of books (title, author, copies available) | `GET /books`, `GET /books/:id`, `POST /books`, `GET /health` |
| Loan Service | Keeps track of loans (who borrowed which book, due date) | `POST /loans`, `GET /loans?memberId=`, `GET /health` |
| Member Service | Keeps track of members (name, card number, active or blocked) | `GET /members/:id`, `POST /members`, `GET /health` |

The `/members/:id/overview` endpoint is where I practice API Composition: the gateway asks the Member Service who the member is, the Loan Service which books they have out, and the Book Service for the titles of those books, and then puts it together into one response. In a monolith this would be one query with a few joins. Here it's three calls that can each fail on their own, which is exactly the point.

**Tech stack:** C# on .NET 10 for all four parts, built with ASP.NET Core minimal APIs and EF Core + Npgsql, PostgreSQL with a separate database per service, and Docker Compose so I can start everything with one command. For the circuit breaker I'll use Polly instead of writing one myself. The gateway is a minimal-API project that calls the services with HttpClient, so I write the routing and the composition myself — that's the part I actually want to learn.

## What I want to learn

At the end of week 12 I want to be able to explain and show these six things using my own working system:

1. **Monolith vs microservices:** what microservices are, and when they're worth the extra complexity (and when they're not).
2. **Splitting up services:** why books and loans are separate services, based on what they do.
3. **The API Gateway pattern:** routing, authentication and API Composition, plus the downsides (an extra step for every request, and one point that can break everything).
4. **Database per Service:** why each service keeps its data private, and what problems that gives when you need data from multiple services.
5. **Running multiple services:** putting services in containers, running them with Docker Compose, and keeping the config out of the code.
6. **Failure in a distributed system:** why one slow or broken service can drag the rest down, and how a health check and a circuit breaker help against that.

## Sources

- [What are microservices?](https://microservices.io/) — microservices.io
- [A pattern language for microservices](https://microservices.io/patterns/) — microservices.io
- [Pattern: Microservice Architecture](https://microservices.io/patterns/microservices.html) — microservices.io
- [Pattern: API Gateway / Backends for Frontends](https://microservices.io/patterns/apigateway.html) — microservices.io
- [Pattern: Database per service](https://microservices.io/patterns/data/database-per-service.html) — microservices.io
- [Pattern: API Composition](https://microservices.io/patterns/data/api-composition.html) — microservices.io
- [Pattern: Health Check API](https://microservices.io/patterns/observability/health-check-api.html) — microservices.io
- [Pattern: Circuit Breaker](https://microservices.io/patterns/reliability/circuit-breaker.html) — microservices.io
- [Pattern: Externalized configuration](https://microservices.io/patterns/externalized-configuration.html) — microservices.io
- [Pattern: Saga](https://microservices.io/patterns/data/saga.html) — microservices.io
- [Pattern: Transactional outbox](https://microservices.io/patterns/data/transactional-outbox.html) — microservices.io
- [Pattern: CQRS](https://microservices.io/patterns/data/cqrs.html) — microservices.io
- [Pattern: Distributed tracing](https://microservices.io/patterns/observability/distributed-tracing.html) — microservices.io
- [Pattern: Service registry](https://microservices.io/patterns/service-registry.html) — microservices.io
- [Pattern: Service component test](https://microservices.io/patterns/testing/service-component-test.html) — microservices.io
