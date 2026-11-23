using Gateway.Api;
using Gateway.Api.Data;
using Gateway.Api.Endpoints;
using Gateway.Api.Hubs;
using Gateway.Api.Security;
using Gateway.Api.Services;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Every request here is a small JSON message; refuse big bodies before they are read.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 64 * 1024);

// The gateway uses the same Neon database as Paysys, in its own "gateway" schema. Set
// ConnectionStrings__Gateway, or reuse ConnectionStrings__Paysys.
var connectionString = builder.Configuration.GetConnectionString("Gateway")
    ?? builder.Configuration.GetConnectionString("Paysys")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Gateway (or ConnectionStrings__Paysys).");

builder.Services.AddDbContext<GatewayDb>(options => options.UseNpgsql(connectionString));
builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TicketStore>();
builder.Services.AddHttpClient(BankClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(8))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddScoped<BankClient>();
builder.Services.AddSignalR();
builder.Services.AddHostedService<RetentionService>();

// How often a terminal can call (see TerminalRateLimit). The answer is 429 with Retry-After.
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = TerminalRateLimit.Create();
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();

        await context.HttpContext.Response.WriteAsJsonAsync(new { message = "Too many requests. Try again in a minute." }, token);
    };
});

// CORS only matters to browsers: it lets the Paysys web site open the live connection. The hub also
// needs a ticket, so CORS is not what protects it.
builder.Services.AddCors(options => options.AddPolicy("web", policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
    policy.WithOrigins(origins is { Length: > 0 } ? origins : ["http://localhost:5273", "https://localhost:7075"])
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials();
}));

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
    await GatewaySchema.EnsureAsync(scope.ServiceProvider.GetRequiredService<GatewayDb>());

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { message = "An unexpected error occurred.", traceId = context.TraceIdentifier });
}));

app.UseRateLimiter();
app.UseCors("web");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapTerminalEndpoints();
app.MapBankEndpoints();
app.MapHub<MerchantHub>("/hub/merchant");

app.Run();

// Lets the tests start the gateway.
public partial class Program;
