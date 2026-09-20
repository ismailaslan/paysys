using Microsoft.EntityFrameworkCore;
using Paysys.DAL.Entities;
using Paysys.DAL.Persistence;
using Paysys.Shared.Tokenization;
using Paysys.Tokenization;
using Paysys.Tokenization.Exceptions;

namespace Paysys.Api.Endpoints;

public static class TokenizationEndpoints
{
    public static WebApplication MapTokenizationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/tokenize", async (Guid? accountId, PaysysDbContext db) =>
        {
            var cards = await db.CardTokens
                .Where(c => !accountId.HasValue || c.AccountId == accountId.Value)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            var accountNamesById = await db.Accounts.ToDictionaryAsync(a => a.Id, a => a.Name);

            return cards.Select(c => new CardTokenSummaryResponse(
                c.Id, c.LastFourDigits, c.CardBrand.ToString(), accountNamesById.GetValueOrDefault(c.AccountId, "(unknown)")));
        });

        app.MapPost("/api/tokenize", async (TokenizeCardRequest request, CardTokenizationService service) =>
        {
            CardToken token;
            try
            {
                token = await service.TokenizeAsync(request.AccountId, request.CardNumber, request.ExpiryMonth, request.ExpiryYear);
            }
            catch (ArgumentException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [ex.ParamName ?? "request"] = [ex.Message] });
            }
            catch (AccountNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
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
