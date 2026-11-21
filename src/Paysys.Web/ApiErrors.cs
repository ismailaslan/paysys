using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Paysys.Web;

// Turns a failed API response into one plain sentence for the screen: never raw JSON, status
// codes or parameter names.
public static class ApiErrors
{
    public static async Task<string> ReadAsync(HttpResponseMessage response)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync();
        }
        catch (Exception)
        {
            body = "";
        }

        return FromBody(body) ?? ForStatus(response.StatusCode);
    }

    private static string? FromBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                var messages = errors.EnumerateObject()
                    .SelectMany(p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray() : [])
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => Clean(e.GetString()!))
                    .Where(m => m.Length > 0)
                    .Distinct()
                    .ToList();

                if (messages.Count > 0)
                    return string.Join(" ", messages);
            }

            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                var cleaned = Clean(message.GetString()!);
                if (cleaned.Length > 0)
                    return cleaned;
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the status text.
        }

        return null;
    }

    // Drops the "(Parameter 'amount')" suffix the server's argument checks add.
    private static string Clean(string message) =>
        Regex.Replace(message, @"\s*\(Parameter '[^']*'\)", "").Trim();

    private static string ForStatus(HttpStatusCode status) => (int)status switch
    {
        401 => "Your session has expired. Please sign in again.",
        403 => "You don't have access to that.",
        404 => "We couldn't find that.",
        409 => "That already exists.",
        429 => "Too many attempts. Wait a moment and try again.",
        >= 500 => "Something went wrong on our side. Please try again in a moment.",
        _ => "Something went wrong. Please check what you entered and try again.",
    };
}
