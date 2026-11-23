using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paysys.Shared.Auth;
using Paysys.Shared.Tokenization;
using Paysys.TestSupport;

namespace Paysys.Api.Tests.Http;

// Evidence for the tokenization benchmark: the full card number is never stored and never logged.
// Real Postgres, the real endpoints, a published test card number. Each test also proves that
// its own search works (it finds the token, and the "rejected" log line), so an empty result
// means the number is really absent and not that the search looked in the wrong place.
public class TokenizationEvidenceTests : PostgresTestBase
{
    private const string Password = "a-long-enough-password";
    private const string Ip = "203.0.113.160";

    // A published, Luhn-valid test number; the first 12 digits are searched as well, so a
    // truncated or masked copy would also be caught (the last four are allowed to be stored).
    private const string TestNumber = "4012888888881881";
    private const string TestNumberFirstTwelve = "401288888888";
    private const string BadChecksumNumber = "4012888888881882";

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        request.Headers.Add(PaysysApiFactory.TestRemoteIpHeader, Ip);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private async Task<(string Token, Guid AccountId)> ArrangeAsync(HttpClient client)
    {
        var bank = await Seed.BankAsync(Db);
        var register = await client.PostJsonAsync("/api/auth/register", new { username = "payer", password = Password, accountKind = "individual" }, Ip);
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var login = await client.PostJsonAsync("/api/auth/login", new LoginRequest("payer", Password, "individual"), Ip);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;

        await using var db = NewContext();
        var userId = await db.Users.Where(u => u.Username == "payer").Select(u => u.Id).SingleAsync();
        var account = await Seed.AccountAsync(Db, bank, userId, 100m);
        return (token, account.Id);
    }

    private static object CardBody(Guid accountId, string number, int month, int year) =>
        new { accountId, cardNumber = number, expiryMonth = month, expiryYear = year };

    private async Task<List<string>> FindInAnyColumnAsync(params string[] needles)
    {
        var connection = Db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync();

        var columns = new List<(string Table, string Column)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = 'public'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add((reader.GetString(0), reader.GetString(1)));
        }

        var hits = new List<string>();
        foreach (var (table, column) in columns)
        {
            foreach (var needle in needles)
            {
                await using var command = connection.CreateCommand();
                // The needles are digits or token characters chosen by the test, never user input.
                command.CommandText = $"SELECT count(*) FROM \"{table}\" WHERE \"{column}\"::text LIKE '%{needle}%'";
                var count = Convert.ToInt64(await command.ExecuteScalarAsync());
                if (count > 0)
                    hits.Add($"{table}.{column} contains {needle}");
            }
        }

        return hits;
    }

    [PostgresFact]
    public async Task CardNumbers_AreNeverStored_InAnyColumnOfAnyTable()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var (token, accountId) = await ArrangeAsync(client);
        var year = DateTime.UtcNow.Year + 2;

        var added = await SendAsync(client, HttpMethod.Post, "/api/tokenize", token, CardBody(accountId, TestNumber, 12, year));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var addedCard = (await added.Content.ReadFromJsonAsync<TokenizeCardResponse>())!;

        var issued = await SendAsync(client, HttpMethod.Post, "/api/cards/issue", token, new { accountId });
        Assert.Equal(HttpStatusCode.Created, issued.StatusCode);
        var issuedCard = (await issued.Content.ReadFromJsonAsync<IssueCardResponse>())!;

        // A rejected card leaves an audit entry; its reason must not carry the number either.
        var rejected = await SendAsync(client, HttpMethod.Post, "/api/tokenize", token, CardBody(accountId, BadChecksumNumber, 12, year));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // The search works: the token that was handed back is found where it is stored.
        var control = await FindInAnyColumnAsync(addedCard.Token);
        Assert.Contains(control, hit => hit.StartsWith("CardTokens."));

        var hits = await FindInAnyColumnAsync(
            TestNumber, TestNumberFirstTwelve, BadChecksumNumber,
            issuedCard.CardNumber, issuedCard.CardNumber[..12]);
        Assert.Empty(hits);
    }

    [PostgresFact]
    public async Task NoTable_HasAColumnThatCouldHoldACardNumberOrCvv()
    {
        var connection = Db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT table_name || '.' || column_name FROM information_schema.columns WHERE table_schema = 'public'";
        var names = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                names.Add(reader.GetString(0));
        }

        Assert.Contains("CardTokens.LastFourDigits", names);
        var suspicious = names.Where(n =>
        {
            var column = n[(n.IndexOf('.') + 1)..].ToLowerInvariant();
            return column.Contains("cardnumber") || column.Contains("cvv") || column.Contains("cvc") || column is "number" or "pan";
        }).ToList();
        Assert.Empty(suspicious);
    }

    [PostgresFact]
    public async Task CardNumbers_AreNeverLogged_AndNeverEchoedInAnError()
    {
        using var factory = new PaysysApiFactory(Db.Database.GetConnectionString());
        using var client = factory.CreateVerifiedClient();
        var (token, accountId) = await ArrangeAsync(client);
        var year = DateTime.UtcNow.Year + 2;

        var added = await SendAsync(client, HttpMethod.Post, "/api/tokenize", token, CardBody(accountId, TestNumber, 12, year));
        var addedCard = (await added.Content.ReadFromJsonAsync<TokenizeCardResponse>())!;
        var issued = await SendAsync(client, HttpMethod.Post, "/api/cards/issue", token, new { accountId });
        var issuedCard = (await issued.Content.ReadFromJsonAsync<IssueCardResponse>())!;

        var badChecksum = await SendAsync(client, HttpMethod.Post, "/api/tokenize", token, CardBody(accountId, BadChecksumNumber, 12, year));
        var expired = await SendAsync(client, HttpMethod.Post, "/api/tokenize", token, CardBody(accountId, TestNumber, 1, 2020));
        var tooShort = await SendAsync(client, HttpMethod.Post, "/api/tokenize", token, CardBody(accountId, "40128888", 12, year));
        Assert.Equal(HttpStatusCode.BadRequest, badChecksum.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        // What the client is told about a rejected card never repeats the number.
        foreach (var response in new[] { badChecksum, expired, tooShort })
        {
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(TestNumber, body);
            Assert.DoesNotContain(TestNumberFirstTwelve, body);
            Assert.DoesNotContain(BadChecksumNumber, body);
        }

        // The log search works: the rejection line the endpoint writes is in what was captured.
        var lines = factory.Logs.Lines;
        Assert.Contains(lines, line => line.Contains("Tokenize rejected"));

        var forbidden = new[]
        {
            TestNumber, TestNumberFirstTwelve, BadChecksumNumber,
            issuedCard.CardNumber, issuedCard.CardNumber[..12], addedCard.Token, issuedCard.Token,
        };
        var leaks = lines.Where(line => forbidden.Any(line.Contains)).ToList();
        Assert.Empty(leaks);
    }
}
