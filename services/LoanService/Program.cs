using LoanService;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Comes from the environment variable ConnectionStrings__LoanDb.
var connectionString = builder.Configuration.GetConnectionString("LoanDb")
    ?? throw new InvalidOperationException("Set the environment variable ConnectionStrings__LoanDb.");

// Where the other services live, from Services__MemberService and Services__BookService.
// In Docker Compose the service name is the host name, e.g. http://book-service:8080.
var memberServiceUrl = builder.Configuration["Services:MemberService"]
    ?? throw new InvalidOperationException("Set the environment variable Services__MemberService.");
var bookServiceUrl = builder.Configuration["Services:BookService"]
    ?? throw new InvalidOperationException("Set the environment variable Services__BookService.");

builder.Services.AddDbContext<LoanDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddOpenApi();

// A short timeout, so a hanging service makes POST /loans fail after 5 seconds
// instead of the HttpClient default of 100 seconds.
builder.Services.AddHttpClient<MemberServiceClient>(c =>
{
    c.BaseAddress = new Uri(memberServiceUrl);
    c.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient<BookServiceClient>(c =>
{
    c.BaseAddress = new Uri(bookServiceUrl);
    c.Timeout = TimeSpan.FromSeconds(5);
});

// Health Check API: /health reports Unhealthy (503) when the database cannot be reached.
// It deliberately does not check the other services: the Loan Service itself still works
// (it can list loans) when the Book Service is down, and the gateway checks each service separately.
builder.Services.AddHealthChecks().AddDbContextCheck<LoanDbContext>("database");

var app = builder.Build();

// Create/update the database schema and insert demo loans on startup (see SeedData.cs for how
// they stay in step with the Book Service's copy counts).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LoanDbContext>();
    db.Database.Migrate();
    SeedData.Seed(db);
}

// API description at /openapi/v1.json, browsable at /swagger.
// Turned on in every environment so it can be demoed from Docker; a real production app would usually hide it.
app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Loan Service"));

app.MapHealthChecks("/health");

var loans = app.MapGroup("/loans").WithTags("Loans");

// The Results<...> return types list every possible response,
// so the OpenAPI description (and Swagger) shows all status codes per endpoint.

// GET /loans or GET /loans?memberId=1
loans.MapGet("/", async (int? memberId, LoanDbContext db) =>
    await db.Loans
        .Where(l => memberId == null || l.MemberId == memberId)
        .OrderBy(l => l.Id)
        .ToListAsync());

loans.MapGet("/{id:int}", async Task<Results<Ok<Loan>, NotFound>> (int id, LoanDbContext db) =>
    await db.Loans.FindAsync(id) is Loan loan
        ? TypedResults.Ok(loan)
        : TypedResults.NotFound());

// Borrowing a book touches three services. In a monolith this would be one database transaction;
// here it is three steps that can each fail on their own:
//   1. ask the Member Service whether the member exists and is not blocked
//   2. ask the Book Service to take one copy (checkout)
//   3. save the loan in our own database
loans.MapPost("/", async Task<Results<Created<Loan>, ValidationProblem, Conflict<ErrorResponse>, ProblemHttpResult>> (
    LoanRequest request, LoanDbContext db, MemberServiceClient members, BookServiceClient books) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
        return TypedResults.ValidationProblem(errors);

    var memberId = request.MemberId!.Value;
    var bookId = request.BookId!.Value;

    // Step 1
    MemberInfo? member;
    try
    {
        member = await members.GetMemberAsync(memberId);
    }
    catch (Exception e) when (IsUnreachable(e))
    {
        return ServiceUnavailable("Member Service");
    }

    if (member is null)
        return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["memberId"] = [$"Member {memberId} does not exist."] });
    if (member.IsBlocked)
        return TypedResults.Conflict(new ErrorResponse($"Member {memberId} is blocked and cannot borrow books."));
    // PendingDeletion: a deletion saga is running for this member (see the Member Service).
    // Refusing loans now is what makes the saga's "no active loans" check stay true until the end.
    if (member.Status != "Active")
        return TypedResults.Conflict(new ErrorResponse($"Member {memberId} is {member.Status} and cannot borrow books."));

    // Step 2
    CheckoutResult checkout;
    try
    {
        checkout = await books.CheckoutAsync(bookId);
    }
    catch (Exception e) when (IsUnreachable(e))
    {
        return ServiceUnavailable("Book Service");
    }

    if (checkout == CheckoutResult.BookNotFound)
        return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["bookId"] = [$"Book {bookId} does not exist."] });
    if (checkout == CheckoutResult.NoCopiesAvailable)
        return TypedResults.Conflict(new ErrorResponse($"No copies of book {bookId} are available."));

    // Step 3
    var now = DateTime.UtcNow;
    var loan = new Loan { MemberId = memberId, BookId = bookId, LoanedAt = now, DueDate = now.AddDays(14) };
    db.Loans.Add(loan);

    try
    {
        await db.SaveChangesAsync();
    }
    catch
    {
        // The copy is already taken in the Book Service, but the loan was not saved.
        // Give the copy back so the two services agree again (a simple compensating action).
        // If this call fails too, the copy stays taken: solving that properly is what the
        // Saga pattern is for, which is out of scope for this project.
        await books.ReturnAsync(bookId);
        throw;
    }

    return TypedResults.Created($"/loans/{loan.Id}", loan);
});

loans.MapPost("/{id:int}/return", async Task<Results<Ok<Loan>, NotFound, Conflict<ErrorResponse>, ProblemHttpResult>> (
    int id, LoanDbContext db, BookServiceClient books) =>
{
    var loan = await db.Loans.FindAsync(id);
    if (loan is null)
        return TypedResults.NotFound();
    if (loan.ReturnedAt is not null)
        return TypedResults.Conflict(new ErrorResponse($"Loan {id} was already returned."));

    // Give the copy back first. If the Book Service is down, the loan stays open
    // and the return can simply be tried again later.
    try
    {
        await books.ReturnAsync(loan.BookId);
    }
    catch (Exception e) when (IsUnreachable(e))
    {
        return ServiceUnavailable("Book Service");
    }

    loan.ReturnedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return TypedResults.Ok(loan);
});

// The two endpoints below are used by the "delete member" saga in the Member Service.
// They are internal: the gateway does not expose them.

// Step 2 of the saga: the loans of this member that are not returned yet.
loans.MapGet("/active", async (int memberId, HttpRequest http, LoanDbContext db, ILogger<Program> logger) =>
{
    var active = await db.Loans
        .Where(l => l.MemberId == memberId && l.ReturnedAt == null)
        .OrderBy(l => l.Id)
        .ToListAsync();

    logger.LogInformation("Correlation {CorrelationId}: member {MemberId} has {Count} active loans",
        http.Headers["X-Correlation-Id"].ToString(), memberId, active.Count);
    return active;
});

// Step 3 of the saga: remove the member id from all their loans.
// Idempotent: the second call finds no loans with this member id any more, changes nothing and
// still answers 200. That matters, because the saga retries this call when it gets no answer,
// even if the first call did work.
loans.MapPost("/anonymize", async (AnonymizeRequest request, HttpRequest http, LoanDbContext db, ILogger<Program> logger) =>
{
    var anonymized = await db.Loans
        .Where(l => l.MemberId == request.MemberId)
        .ExecuteUpdateAsync(s => s.SetProperty(l => l.MemberId, (int?)null));

    logger.LogInformation("Correlation {CorrelationId}: anonymized {Count} loans of member {MemberId}",
        http.Headers["X-Correlation-Id"].ToString(), anonymized, request.MemberId);
    return new { request.MemberId, Anonymized = anonymized };
});

app.Run();

// No connection, an error status (5xx) or the 5 second timeout.
static bool IsUnreachable(Exception e) => e is HttpRequestException or TaskCanceledException;

static ProblemHttpResult ServiceUnavailable(string service) =>
    TypedResults.Problem($"The {service} cannot be reached. Try again later.", statusCode: StatusCodes.Status503ServiceUnavailable);

// Body of a 409 response: {"error": "..."}
record ErrorResponse(string Error);

// Body of POST /loans/anonymize: {"memberId": 2}
record AnonymizeRequest(int MemberId);
