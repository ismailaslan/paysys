using Paysys.Api.Endpoints;
using Paysys.Application.DependencyInjection;
using Paysys.Infrastructure.DependencyInjection;
using Paysys.Tokenization.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddTokenization();
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

app.MapAccountEndpoints();
app.MapTransactionEndpoints();
app.MapExchangeRateEndpoints();
app.MapTokenizationEndpoints();

app.Run();
