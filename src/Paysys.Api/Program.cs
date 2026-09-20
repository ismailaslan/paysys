using Paysys.Api.Auth;
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
builder.Services.AddPaysysAuth(builder.Configuration);
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseCors("BlazorWeb");
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapBankEndpoints();
app.MapTransactionEndpoints();
app.MapExchangeRateEndpoints();
app.MapTokenizationEndpoints();
app.MapBankStubEndpoints();

app.Run();
