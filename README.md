# Library microservices

A library lending system built as microservices. It has three services
(Book, Member and Loan Service) and one Postgres container. Every service gets its own database.

```
services/BookService   Book Service (.NET 10, Minimal APIs, EF Core + Npgsql)
services/MemberService Member Service (same setup, its own database)
services/LoanService   Loan Service (calls the other two over HTTP)
db/init                Postgres init script that creates one database + user per service
docs                   Project documentation
docker-compose.yml     Starts Postgres and all services
.env.example           Passwords; copy to .env to change them
```

## Start it

You only need Docker.

```bash
docker compose up --build
```

The Book Service runs on http://localhost:5001, the Member Service on
http://localhost:5002 and the Loan Service on http://localhost:5003. On startup
each service applies its EF Core migrations. Books and members get seed data
if their table is empty (5 books, 3 members); loans start empty.

Passwords: without a `.env` file, Compose uses the defaults from `.env.example`.
To change them, `cp .env.example .env` and edit `.env`.

The init script only runs when the Postgres volume is empty. After changing it
(or a password), reset the database with:

```bash
docker compose down -v
```

## Try it in the browser

Open http://localhost:5001/swagger (books), http://localhost:5002/swagger
(members) or http://localhost:5003/swagger (loans). Click an endpoint, then **Try it out** and
**Execute** to send a real request and see the response and status code.
The raw API description is at http://localhost:5001/openapi/v1.json.

## Book Service endpoints

| Method | Path          | Success | Errors   |
|--------|---------------|---------|----------|
| GET    | /books        | 200     |          |
| GET    | /books/{id}   | 200     | 404      |
| POST   | /books        | 201     | 400, 409 |
| PUT    | /books/{id}   | 200     | 400, 404, 409 |
| DELETE | /books/{id}   | 204     | 404      |
| POST   | /books/{id}/checkout | 200 | 404, 409 |
| POST   | /books/{id}/return   | 200 | 404, 409 |
| GET    | /health       | 200     | 503      |

`checkout` lowers `availableCopies` by 1 and `return` raises it by 1; they are
called by the Loan Service. 409 means no copy is available (checkout) or all
copies are already in (return). The check and the change are one SQL `UPDATE`,
so two checkouts at the same moment can never both take the last copy.

`/health` returns `Healthy` when the service can reach its database and
`Unhealthy` (503) when it cannot.

409 means another book already has that ISBN. ISBNs must be 10 or 13 digits;
hyphens are allowed and are removed before saving.

```bash
curl localhost:5001/books

curl localhost:5001/books/1

curl -X POST localhost:5001/books \
  -H "Content-Type: application/json" \
  -d '{"title":"Refactoring","author":"Martin Fowler","isbn":"978-0-13-475759-9","totalCopies":2,"availableCopies":2}'

curl -X PUT localhost:5001/books/1 \
  -H "Content-Type: application/json" \
  -d '{"title":"The Pragmatic Programmer","author":"David Thomas, Andrew Hunt","isbn":"9780135957059","totalCopies":5,"availableCopies":4}'

curl -X DELETE localhost:5001/books/1
```

(On Windows PowerShell, use `curl.exe` instead of `curl`.)

## Member Service endpoints

| Method | Path          | Success | Errors        |
|--------|---------------|---------|---------------|
| GET    | /members      | 200     |               |
| GET    | /members/{id} | 200     | 404           |
| POST   | /members      | 201     | 400, 409      |
| PUT    | /members/{id} | 200     | 400, 404, 409 |
| GET    | /health       | 200     | 503           |

409 means another member already has that card number. Card numbers are
stored without spaces and in capitals, so `lib 0004` becomes `LIB0004`.
Member 3 (Carla Bakker) is seeded as blocked, for the Loan Service later.

```bash
curl localhost:5002/members

curl -X POST localhost:5002/members   -H "Content-Type: application/json"   -d '{"name":"Daan Visser","cardNumber":"LIB0004","isBlocked":false}'

curl -X PUT localhost:5002/members/4   -H "Content-Type: application/json"   -d '{"name":"Daan Visser","cardNumber":"LIB0004","isBlocked":true}'
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
   the member is blocked.
2. Ask the Book Service to check out one copy: 400 if the book does not
   exist, 409 if no copy is available.
3. Save the loan, due in 14 days. If saving fails, the copy is given back.

503 means the Member or Book Service could not be reached within 5 seconds.
The Loan Service keeps working for everything that only needs its own
database, such as listing loans. A loan only stores `memberId` and `bookId`,
not titles or names: those belong to the other services.

The addresses of the other services come from `Services__MemberService` and
`Services__BookService` (see `docker-compose.yml`).

```bash
curl -X POST localhost:5003/loans   -H "Content-Type: application/json"   -d '{"memberId":1,"bookId":2}'

curl "localhost:5003/loans?memberId=1"

curl -X POST localhost:5003/loans/1/return
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
     ports:
       - "5002:8080"
     depends_on:
       postgres:
         condition: service_healthy
   ```
5. Run `docker compose down -v` so the init script runs again, then
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
