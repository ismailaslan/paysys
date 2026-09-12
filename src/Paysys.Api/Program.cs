using Microsoft.EntityFrameworkCore;
using Paysys.Application.DependencyInjection;
using Paysys.Application.Transactions;
using Paysys.Application.Transactions.Exceptions;
using Paysys.Domain.Entities;
using Paysys.Infrastructure.DependencyInjection;
using Paysys.Infrastructure.Persistence;
using Paysys.Shared.Accounts;
using Paysys.Shared.Transactions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("BlazorWeb");

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapGet("/api/transactions/count", async (PaysysDbContext db) => await db.Transactions.CountAsync());

app.MapGet("/api/accounts", async (PaysysDbContext db) =>
    await db.Accounts
        .Select(a => new AccountResponse(a.Id, a.Name, a.Balance, a.Currency, a.CreatedAt))
        .ToListAsync());

app.MapPost("/api/accounts", async (CreateAccountRequest request, PaysysDbContext db) =>
{
    Account account;
    try
    {
        account = new Account(Guid.NewGuid(), request.Name, request.Balance, request.Currency);
    }
    catch (ArgumentException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
    }

    db.Accounts.Add(account);
    await db.SaveChangesAsync();

    return Results.Created($"/api/accounts/{account.Id}",
        new AccountResponse(account.Id, account.Name, account.Balance, account.Currency, account.CreatedAt));
});

app.MapPost("/api/transactions", async (CreateTransactionRequest request, TransactionProcessingService service) =>
{
    Transaction transaction;
    try
    {
        transaction = await service.ProcessAsync(
            request.SourceAccountId,
            request.DestinationAccountId,
            request.Amount,
            request.Currency,
            request.IdempotencyKey);
    }
    catch (ArgumentException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
    }
    catch (AccountNotFoundException ex)
    {
        return Results.NotFound(new { message = ex.Message });
    }
    catch (CurrencyMismatchException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["currency"] = [ex.Message] });
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Conflict(new { message = "One of the accounts was modified concurrently. Please retry." });
    }

    var response = new TransactionResponse(
        transaction.Id,
        transaction.Amount,
        transaction.Currency,
        transaction.Status.ToString(),
        transaction.SourceAccountId,
        transaction.DestinationAccountId,
        transaction.IdempotencyKey,
        transaction.FailureReason,
        transaction.CreatedAt,
        transaction.UpdatedAt);

    return Results.Created($"/api/transactions/{transaction.Id}", response);
});

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
