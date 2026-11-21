using Paysys.Api.Auth;
using Paysys.Api.Endpoints;
using Paysys.Api.Diagnostics;
using Paysys.BLL.DependencyInjection;
using Paysys.DAL.DependencyInjection;
using Paysys.Tokenization.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<Paysys.Api.OpenApi.BearerSecuritySchemeTransformer>());
builder.Services.AddDataAccess(builder.Configuration);
builder.Services.AddBusinessLogic(builder.Environment.IsDevelopment());
builder.Services.AddTokenization();
builder.Services.AddPaysysAuth(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddSingleton<AuditChainStatus>();
builder.Services.AddHostedService<AuditChainVerificationService>();
// CORS only tells *browsers* which origins may read responses. curl, scripts and
// any non-browser client ignore it entirely, so it is not a security boundary -
// authentication and authorization below are what protect the API.
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorWeb", policy =>
    {
        // Origins come from Cors:AllowedOrigins (env var Cors__AllowedOrigins__0, __1, ...).
        // When none are configured the two local development origins apply, as before.
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
        policy.WithOrigins(allowedOrigins is { Length: > 0 }
                ? allowedOrigins
                : ["http://localhost:5273", "https://localhost:7075"])
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// First in the pipeline so it sees everything. An exception nothing else handled is logged at
// Error by this middleware; the client gets a small JSON body carrying the trace id (so the
// log line can be found from a bug report) instead of a stack trace or an empty 500.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { message = "An unexpected error occurred.", traceId = context.TraceIdentifier });
}));

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("BlazorWeb");
app.UseAuthentication();
app.UseRequestOutcomeLogging();     // after authentication so the user is known; before authorization so it sees the final status
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapAuditEndpoints();
app.MapAccountEndpoints();
app.MapPayeeEndpoints();
app.MapBankEndpoints();
app.MapTransactionEndpoints();
app.MapExchangeRateEndpoints();
app.MapTokenizationEndpoints();
app.MapBankStubEndpoints();
app.MapHealthEndpoints();

// Opt-in: `dotnet run --project src/Paysys.Api -- --seed-demo` loads sample data into an
// empty database and exits without starting the server.
if (args.Contains("--seed-demo"))
{
    await using var seedScope = app.Services.CreateAsyncScope();
    Environment.ExitCode = await Paysys.Api.Seeding.DemoSeeder.RunAsync(seedScope.ServiceProvider);
    return;
}

// Opt-in: `dotnet run --project src/Paysys.Api -- --import-users` copies the Auth:Users
// configuration entries into the Users table (see ConfigUserImporter) and exits without
// starting the server. Login reads only the Users table. Adding `--update-existing` also
// updates the Role and PasswordHash of rows that match by Id and username; it refuses to
// leave the table without an admin.
if (args.Contains("--import-users"))
{
    await using var importScope = app.Services.CreateAsyncScope();
    var importer = importScope.ServiceProvider.GetRequiredService<ConfigUserImporter>();
    Environment.ExitCode = await importer.RunAsync(Console.Out, Console.Error, args.Contains("--update-existing"));
    return;
}

app.Run();
