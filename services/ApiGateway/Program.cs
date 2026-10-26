using ApiGateway;

var builder = WebApplication.CreateBuilder(args);

// Where the services live, from Services__BookService, Services__MemberService and Services__LoanService.
// In Docker Compose the service name is the host name, e.g. http://book-service:8080.
string ServiceUrl(string name) => builder.Configuration[$"Services:{name}"]
    ?? throw new InvalidOperationException($"Set the environment variable Services__{name}.");

// One named HttpClient per service. The timeout is longer than the Loan Service's own
// 5 second timeout, because POST /loans can call two services before it answers.
foreach (var name in new[] { "BookService", "MemberService", "LoanService" })
{
    var url = ServiceUrl(name);
    builder.Services.AddHttpClient(name, c =>
    {
        c.BaseAddress = new Uri(url);
        c.Timeout = TimeSpan.FromSeconds(15);
    });
}

// The key clients must send in the X-Api-Key header, from the environment variable Gateway__ApiKey.
var apiKey = builder.Configuration["Gateway:ApiKey"]
    ?? throw new InvalidOperationException("Set the environment variable Gateway__ApiKey.");

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

// The Book Service has endpoints that only the Loan Service may call. Clients must borrow through
// POST /api/loans, so the gateway does not expose these. More specific routes win over {**rest}.
app.Map("/api/books/{id}/checkout", () => Results.NotFound());
app.Map("/api/books/{id}/return", () => Results.NotFound());

app.Run();

void MapRoute(string prefix, string serviceName) =>
    app.Map(prefix + "/{**rest}", (HttpContext context, IHttpClientFactory clients) =>
        Forwarder.ForwardAsync(context, clients.CreateClient(serviceName), context.Request.Path.Value!["/api".Length..]));
