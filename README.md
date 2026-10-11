# Library microservices

A library lending system built as microservices. Right now it has one service
(Book Service) and one Postgres container. Every service gets its own database.

```
services/BookService   Book Service (.NET 10, Minimal APIs, EF Core + Npgsql)
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

The Book Service runs on http://localhost:5001. On startup it applies EF Core
migrations and adds 5 books if the table is empty.

Passwords: without a `.env` file, Compose uses the defaults from `.env.example`.
To change them, `cp .env.example .env` and edit `.env`.

The init script only runs when the Postgres volume is empty. After changing it
(or a password), reset the database with:

```bash
docker compose down -v
```

## Try it in the browser

Open http://localhost:5001/swagger. Click an endpoint, then **Try it out** and
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

## Add an EF Core migration

Needs the .NET 10 SDK and the EF tool (`dotnet tool install --global dotnet-ef`).

1. Change `Book.cs` or `BookDbContext.cs`.
2. From `services/BookService`, run:

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

Example: Member Service.

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
