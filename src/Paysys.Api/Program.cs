using Paysys.Api.Endpoints;
using Paysys.BLL.DependencyInjection;
using Paysys.DAL.DependencyInjection;
using Paysys.Tokenization.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddDataAccess(builder.Configuration);
builder.Services.AddBusinessLogic();
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
app.MapBankEndpoints();
app.MapTransactionEndpoints();
app.MapExchangeRateEndpoints();
app.MapTokenizationEndpoints();
app.MapBankStubEndpoints();

app.Run();
