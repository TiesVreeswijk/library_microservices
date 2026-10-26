using MemberService;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Comes from the environment variable ConnectionStrings__MemberDb.
var connectionString = builder.Configuration.GetConnectionString("MemberDb")
    ?? throw new InvalidOperationException("Set the environment variable ConnectionStrings__MemberDb.");

builder.Services.AddDbContext<MemberDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddOpenApi();

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

app.Run();

// The unique index on CardNumber makes Postgres reject duplicates with error code 23505.
static bool IsDuplicateCardNumber(DbUpdateException e) =>
    e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

// Body of a 409 response: {"error": "..."}
record ErrorResponse(string Error);
