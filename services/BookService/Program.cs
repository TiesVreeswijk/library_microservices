using BookService;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Comes from the environment variable ConnectionStrings__BookDb.
var connectionString = builder.Configuration.GetConnectionString("BookDb")
    ?? throw new InvalidOperationException("Set the environment variable ConnectionStrings__BookDb.");

builder.Services.AddDbContext<BookDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddOpenApi();

// Health Check API: /health reports Unhealthy (503) when the database cannot be reached,
// so Docker and later the gateway can tell "running" apart from "working".
builder.Services.AddHealthChecks().AddDbContextCheck<BookDbContext>("database");

var app = builder.Build();

// Create/update the database schema and insert seed data on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BookDbContext>();
    db.Database.Migrate();
    SeedData.Seed(db);
}

// API description at /openapi/v1.json, browsable at /swagger.
// Turned on in every environment so it can be demoed from Docker; a real production app would usually hide it.
app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Book Service"));

app.MapHealthChecks("/health");

var books = app.MapGroup("/books").WithTags("Books");

// The Results<...> return types list every possible response,
// so the OpenAPI description (and Swagger) shows all status codes per endpoint.

books.MapGet("/", async (BookDbContext db) =>
    await db.Books.OrderBy(b => b.Id).ToListAsync());

books.MapGet("/{id:int}", async Task<Results<Ok<Book>, NotFound>> (int id, BookDbContext db) =>
    await db.Books.FindAsync(id) is Book book
        ? TypedResults.Ok(book)
        : TypedResults.NotFound());

books.MapPost("/", async Task<Results<Created<Book>, ValidationProblem, Conflict<ErrorResponse>>> (BookRequest request, BookDbContext db) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
        return TypedResults.ValidationProblem(errors);

    var book = new Book();
    request.CopyTo(book);
    db.Books.Add(book);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException e) when (IsDuplicateIsbn(e))
    {
        return TypedResults.Conflict(new ErrorResponse($"A book with ISBN {book.Isbn} already exists."));
    }

    return TypedResults.Created($"/books/{book.Id}", book);
});

books.MapPut("/{id:int}", async Task<Results<Ok<Book>, ValidationProblem, NotFound, Conflict<ErrorResponse>>> (int id, BookRequest request, BookDbContext db) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
        return TypedResults.ValidationProblem(errors);

    var book = await db.Books.FindAsync(id);
    if (book is null)
        return TypedResults.NotFound();

    request.CopyTo(book);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException e) when (IsDuplicateIsbn(e))
    {
        return TypedResults.Conflict(new ErrorResponse($"A book with ISBN {book.Isbn} already exists."));
    }

    return TypedResults.Ok(book);
});

books.MapDelete("/{id:int}", async Task<Results<NoContent, NotFound>> (int id, BookDbContext db) =>
{
    var book = await db.Books.FindAsync(id);
    if (book is null)
        return TypedResults.NotFound();

    db.Books.Remove(book);
    await db.SaveChangesAsync();
    return TypedResults.NoContent();
});

// Called by the Loan Service when a book is borrowed or returned.
// The check and the change happen in one SQL UPDATE ... WHERE, so two loans at the
// same moment can never both take the last copy (no read-then-write race).

books.MapPost("/{id:int}/checkout", async Task<Results<Ok<Book>, NotFound, Conflict<ErrorResponse>>> (int id, BookDbContext db) =>
{
    var updated = await db.Books
        .Where(b => b.Id == id && b.AvailableCopies > 0)
        .ExecuteUpdateAsync(s => s.SetProperty(b => b.AvailableCopies, b => b.AvailableCopies - 1));

    var book = await db.Books.FindAsync(id);
    if (book is null)
        return TypedResults.NotFound();

    return updated == 1
        ? TypedResults.Ok(book)
        : TypedResults.Conflict(new ErrorResponse($"No copies of book {id} are available."));
});

books.MapPost("/{id:int}/return", async Task<Results<Ok<Book>, NotFound, Conflict<ErrorResponse>>> (int id, BookDbContext db) =>
{
    var updated = await db.Books
        .Where(b => b.Id == id && b.AvailableCopies < b.TotalCopies)
        .ExecuteUpdateAsync(s => s.SetProperty(b => b.AvailableCopies, b => b.AvailableCopies + 1));

    var book = await db.Books.FindAsync(id);
    if (book is null)
        return TypedResults.NotFound();

    return updated == 1
        ? TypedResults.Ok(book)
        : TypedResults.Conflict(new ErrorResponse($"All copies of book {id} are already in."));
});

app.Run();

// The unique index on Isbn makes Postgres reject duplicates with error code 23505.
static bool IsDuplicateIsbn(DbUpdateException e) =>
    e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

// Body of a 409 response: {"error": "..."}
record ErrorResponse(string Error);
