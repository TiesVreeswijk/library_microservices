using ApiGateway;

var builder = WebApplication.CreateBuilder(args);

// The services behind the gateway: the name used in configuration, and the name used in
// logs, errors and /api/diagnostics/circuits.
var services = new Dictionary<string, string>
{
    ["BookService"] = "book-service",
    ["MemberService"] = "member-service",
    ["LoanService"] = "loan-service",
};

builder.Services.AddSingleton(new CircuitStates(services.Values));

// Polly logs every attempt of every call by itself, which buries the interesting lines.
// Resilience.cs logs the retries, timeouts and circuit state changes itself, so Polly's own logs are off.
builder.Logging.AddFilter("Polly", LogLevel.None);

// Where the services live, from Services__BookService, Services__MemberService and Services__LoanService.
// In Docker Compose the service name is the host name, e.g. http://book-service:8080.
// Each one gets its own HttpClient with a timeout, retry and circuit breaker (see Resilience.cs).
foreach (var (name, displayName) in services)
{
    var url = builder.Configuration[$"Services:{name}"]
        ?? throw new InvalidOperationException($"Set the environment variable Services__{name}.");
    builder.Services.AddServiceClient(builder.Configuration, name, displayName, url);
}

// The key clients must send in the X-Api-Key header, from the environment variable Gateway__ApiKey.
var apiKey = builder.Configuration["Gateway:ApiKey"]
    ?? throw new InvalidOperationException("Set the environment variable Gateway__ApiKey.");

// GET /api/diagnostics/circuits is only for demos, so it is off unless Gateway__EnableDiagnostics=true.
var diagnosticsEnabled = builder.Configuration.GetValue("Gateway:EnableDiagnostics", false);

// The gateway has no database, so its health check only says "the gateway process is up".
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");

// Authentication happens here, once, for every service. The services themselves do not check
// anything: they trust whatever reaches them inside the Docker network.
// /health stays open so Docker and monitoring can call it without a key.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !ApiKey.IsValid(context.Request.Headers["X-Api-Key"], apiKey))
    {
        await Results.Problem("Send a valid API key in the X-Api-Key header.",
            statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
        return;
    }

    await next();
});

// Routing: /api/<resource>/... goes to the service that owns <resource>, without the /api prefix.
//   /api/books/1          -> book-service   /books/1
//   /api/members/1        -> member-service /members/1
//   /api/loans?memberId=1 -> loan-service   /loans?memberId=1
// {**rest} matches everything after the prefix (also nothing), for every HTTP method.
MapRoute("/api/books", "BookService");
MapRoute("/api/members", "MemberService");
MapRoute("/api/loans", "LoanService");

// API Composition: one endpoint that combines the Member, Loan and Book Service (see MemberOverview.cs).
// A route with a fixed segment like /overview wins over {**rest}, so this is not forwarded.
app.MapGet("/api/members/{id:int}/overview", MemberOverview.GetAsync);

// The Book Service has endpoints that only the Loan Service may call. Clients must borrow through
// POST /api/loans, so the gateway does not expose these. More specific routes win over {**rest}.
app.Map("/api/books/{id}/checkout", () => Results.NotFound());
app.Map("/api/books/{id}/return", () => Results.NotFound());

// Demo only: the current state of each circuit, e.g. { "book-service": "Open", "loan-service": "Closed", ... }
if (diagnosticsEnabled)
    app.MapGet("/api/diagnostics/circuits", (CircuitStates circuits) => circuits.All());

app.Run();

void MapRoute(string prefix, string serviceName) =>
    app.Map(prefix + "/{**rest}", (HttpContext context, IHttpClientFactory clients, CircuitStates circuits) =>
        Forwarder.ForwardAsync(context, clients.CreateClient(serviceName), context.Request.Path.Value!["/api".Length..],
            services[serviceName], circuits));
