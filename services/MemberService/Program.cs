using System.Text.Json.Serialization;
using MemberService;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Comes from the environment variable ConnectionStrings__MemberDb.
var connectionString = builder.Configuration.GetConnectionString("MemberDb")
    ?? throw new InvalidOperationException("Set the environment variable ConnectionStrings__MemberDb.");

// Where the Loan Service lives (needed by the deletion saga), from Services__LoanService.
var loanServiceUrl = builder.Configuration["Services:LoanService"]
    ?? throw new InvalidOperationException("Set the environment variable Services__LoanService.");

builder.Services.AddDbContext<MemberDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddOpenApi();

// Enums go out as text in JSON: "status": "PendingDeletion" instead of "status": 1.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// The deletion saga: an HttpClient to the Loan Service (with retry, circuit breaker and timeout),
// the runner that does the steps, and the background worker that keeps unfinished sagas going.
builder.Services.AddLoanServiceClient(builder.Configuration, loanServiceUrl);
builder.Services.AddScoped<DeletionSagaRunner>();
builder.Services.AddHostedService<DeletionSagaWorker>();

// Polly logs every attempt by itself; LoanServiceClient.cs and the saga log what matters.
builder.Logging.AddFilter("Polly", LogLevel.None);

// Health Check API: /health reports Unhealthy (503) when the database cannot be reached,
// so Docker and later the gateway can tell "running" apart from "working".
builder.Services.AddHealthChecks().AddDbContextCheck<MemberDbContext>("database");

var app = builder.Build();

// Create/update the database schema and insert seed data on startup.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MemberDbContext>();
    db.Database.Migrate();
    SeedData.Seed(db);
}

// API description at /openapi/v1.json, browsable at /swagger.
// Turned on in every environment so it can be demoed from Docker; a real production app would usually hide it.
app.MapOpenApi();
app.UseSwaggerUI(options => options.SwaggerEndpoint("/openapi/v1.json", "Member Service"));

app.MapHealthChecks("/health");

var members = app.MapGroup("/members").WithTags("Members");

// The Results<...> return types list every possible response,
// so the OpenAPI description (and Swagger) shows all status codes per endpoint.

members.MapGet("/", async (MemberDbContext db) =>
    await db.Members.OrderBy(m => m.Id).ToListAsync());

members.MapGet("/{id:int}", async Task<Results<Ok<Member>, NotFound>> (int id, MemberDbContext db) =>
    await db.Members.FindAsync(id) is Member member
        ? TypedResults.Ok(member)
        : TypedResults.NotFound());

members.MapPost("/", async Task<Results<Created<Member>, ValidationProblem, Conflict<ErrorResponse>>> (MemberRequest request, MemberDbContext db) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
        return TypedResults.ValidationProblem(errors);

    var member = new Member();
    request.CopyTo(member);
    db.Members.Add(member);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException e) when (IsDuplicateCardNumber(e))
    {
        return TypedResults.Conflict(new ErrorResponse($"A member with card number {member.CardNumber} already exists."));
    }

    return TypedResults.Created($"/members/{member.Id}", member);
});

members.MapPut("/{id:int}", async Task<Results<Ok<Member>, ValidationProblem, NotFound, Conflict<ErrorResponse>>> (int id, MemberRequest request, MemberDbContext db) =>
{
    var errors = request.Validate();
    if (errors.Count > 0)
        return TypedResults.ValidationProblem(errors);

    var member = await db.Members.FindAsync(id);
    if (member is null)
        return TypedResults.NotFound();
    if (member.Status != MemberStatus.Active)
        return TypedResults.Conflict(new ErrorResponse($"Member {id} is {member.Status} and cannot be changed."));

    request.CopyTo(member);

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException e) when (IsDuplicateCardNumber(e))
    {
        return TypedResults.Conflict(new ErrorResponse($"A member with card number {member.CardNumber} already exists."));
    }

    return TypedResults.Ok(member);
});

// Delete a member: starts the "delete member" saga (see DeletionSagaRunner.cs) and answers at once
// with 202 Accepted. The deletion itself happens in the background, because it needs the Loan
// Service, which may be down right now. GET /members/{id}/deletion shows how far it is.
members.MapDelete("/{id:int}", async Task<Results<Accepted<DeletionSaga>, NotFound, Conflict<ErrorResponse>>> (
    int id, HttpRequest http, MemberDbContext db, ILogger<Program> logger) =>
{
    // Step 1 of the saga and saving the saga itself happen in one local transaction: both are in
    // member_db, so either the member is PendingDeletion and the saga exists, or neither.
    await using var transaction = await db.Database.BeginTransactionAsync();

    // Active -> PendingDeletion in one UPDATE ... WHERE, so two DELETEs at the same moment
    // cannot both start a saga (same idea as the checkout in the Book Service).
    var marked = await db.Members
        .Where(m => m.Id == id && m.Status == MemberStatus.Active)
        .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MemberStatus.PendingDeletion));

    if (marked == 0)
    {
        var member = await db.Members.FindAsync(id);
        if (member is null)
            return TypedResults.NotFound();
        return TypedResults.Conflict(new ErrorResponse(member.Status == MemberStatus.Deleted
            ? $"Member {id} is already deleted."
            : $"Member {id} is already being deleted."));
    }

    var now = DateTime.UtcNow;
    var saga = new DeletionSaga
    {
        Id = Guid.NewGuid(),
        MemberId = id,
        CurrentStep = SagaStep.CheckActiveLoans, // step 1 is done, the worker continues with step 2
        State = SagaState.Running,
        NextAttemptAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };
    db.DeletionSagas.Add(saga);
    await db.SaveChangesAsync();
    await transaction.CommitAsync();

    logger.LogInformation("Saga {SagaId} member {MemberId}: STARTED (request {CorrelationId}), step 1 MarkPendingDeletion done",
        saga.Id, id, http.Headers["X-Correlation-Id"].ToString());

    return TypedResults.Accepted($"/members/{id}/deletion", saga);
});

// The state of the latest deletion saga of this member: state, current step, failure reason.
members.MapGet("/{id:int}/deletion", async Task<Results<Ok<DeletionSaga>, NotFound>> (int id, MemberDbContext db) =>
    await db.DeletionSagas
        .Where(s => s.MemberId == id)
        .OrderByDescending(s => s.CreatedAt)
        .FirstOrDefaultAsync() is DeletionSaga saga
        ? TypedResults.Ok(saga)
        : TypedResults.NotFound());

app.Run();

// The unique index on CardNumber makes Postgres reject duplicates with error code 23505.
static bool IsDuplicateCardNumber(DbUpdateException e) =>
    e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

// Body of a 409 response: {"error": "..."}
record ErrorResponse(string Error);
