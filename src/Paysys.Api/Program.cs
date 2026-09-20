using Paysys.Api.Auth;
using Paysys.Api.Endpoints;
using Paysys.Api.Diagnostics;
using Paysys.BLL.DependencyInjection;
using Paysys.DAL.DependencyInjection;
using Paysys.Tokenization.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddDataAccess(builder.Configuration);
builder.Services.AddBusinessLogic(builder.Environment.IsDevelopment());
builder.Services.AddTokenization();
builder.Services.AddPaysysAuth(builder.Configuration);
builder.Services.AddSingleton<AuditChainStatus>();
builder.Services.AddHostedService<AuditChainVerificationService>();
// CORS only tells *browsers* which origins may read responses. curl, scripts and
// any non-browser client ignore it entirely, so it is not a security boundary -
// authentication and authorization below are what protect the API.
builder.Services.AddCors(options =>
{
    options.AddPolicy("BlazorWeb", policy =>
    {
        policy.WithOrigins("http://localhost:5273", "https://localhost:7075")
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

app.Run();
