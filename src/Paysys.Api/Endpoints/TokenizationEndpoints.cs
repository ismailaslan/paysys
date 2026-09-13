using Microsoft.EntityFrameworkCore;
using Paysys.Domain.Entities;
using Paysys.Infrastructure.Persistence;
using Paysys.Shared.Tokenization;
using Paysys.Tokenization;

namespace Paysys.Api.Endpoints;

public static class TokenizationEndpoints
{
    public static WebApplication MapTokenizationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tokenize", async (PaysysDbContext db) =>
            await db.CardTokens
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new CardTokenSummaryResponse(c.Id, c.LastFourDigits, c.CardBrand.ToString()))
                .ToListAsync());

        app.MapPost("/api/tokenize", async (TokenizeCardRequest request, CardTokenizationService service) =>
        {
            CardToken token;
            try
            {
                token = await service.TokenizeAsync(request.CardNumber, request.ExpiryMonth, request.ExpiryYear);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }

            var response = new TokenizeCardResponse(
                token.Id,
                token.Token,
                token.LastFourDigits,
                token.CardBrand.ToString(),
                token.ExpiryMonth,
                token.ExpiryYear,
                token.CreatedAt);

            // No Location header: there is deliberately no GET-by-token endpoint,
            // since nothing about a token is ever looked up or reversed in this demo.
            return Results.Created((string?)null, response);
        });

        return app;
    }
}
