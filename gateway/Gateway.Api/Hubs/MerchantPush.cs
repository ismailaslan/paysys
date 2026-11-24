using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Gateway.Api.Hubs;

// Sends a payment's outcome to the merchant's browsers.
//
// A browser is connected to one gateway instance, but the bank's message can reach any instance. So the
// instance that receives the message sends it to its own browsers at once, and, when the relay is turned on
// (Gateway:PushRelay:Enabled = true), also tells the other instances through the database (Postgres NOTIFY);
// each of them sends it to its own browsers. An instance ignores what it sent itself, so no browser hears it twice.
//
// The relay needs a database connection that holds a session (LISTEN), which a pooled connection string such as
// Neon's "-pooler" host does not, and a permanent connection may keep a serverless database awake. So it is off by
// default and a single instance behaves exactly as before. If the database does not answer, the local send has
// already happened and the failure is only logged; the merchant page's status check catches up.
//
// Postgres refuses a notification of 8000 bytes or more, so a larger message is cut into pieces (base64, about 6000
// characters each) that the receiver puts back together; pieces that do not all arrive within 30 seconds are dropped.
// A message over 1 MB is not relayed.
public interface IMerchantPush
{
    Task SendAsync(string group, string method, object payload, CancellationToken cancellationToken = default);
}

public sealed class MerchantPush : BackgroundService, IMerchantPush
{
    public const string Channel = "gateway_push";
    private const int MaxNotifyBytes = 7000;   // Postgres refuses a notification payload of 8000 bytes or more.
    private const int PieceBytes = 4500;       // raw bytes per piece; about 6000 characters once base64 and the envelope are added
    private const int MaxMessageBytes = 1_000_000;
    private static readonly TimeSpan PiecesKeptFor = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, Pending> _pending = new();

    private readonly IHubContext<MerchantHub> _hub;
    private readonly ILogger<MerchantPush> _log;
    private readonly bool _enabled;
    private readonly string? _connectionString;
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MerchantPush(IHubContext<MerchantHub> hub, IConfiguration configuration, ILogger<MerchantPush> log)
    {
        _hub = hub;
        _log = log;
        _enabled = configuration.GetValue("Gateway:PushRelay:Enabled", false);
        _connectionString = configuration.GetConnectionString("Gateway") ?? configuration.GetConnectionString("Paysys");
    }

    // Completes when this instance is listening for the others (at once when the relay is off).
    public Task Ready => _ready.Task;

    public async Task SendAsync(string group, string method, object payload, CancellationToken cancellationToken = default)
    {
        await _hub.Clients.Group(group).SendAsync(method, payload, cancellationToken);

        if (!_enabled || _connectionString is null)
            return;

        try
        {
            var message = JsonSerializer.Serialize(new Relayed(_instance, group, method, JsonSerializer.SerializeToElement(payload)));
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            if (bytes.Length > MaxMessageBytes)
            {
                _log.LogWarning("A live message was too large to pass to the other gateway instances");
                return;
            }

            var notifications = new List<string>();
            if (bytes.Length <= MaxNotifyBytes)
            {
                notifications.Add(message);
            }
            else
            {
                var id = Guid.NewGuid().ToString("N");
                var count = (bytes.Length + PieceBytes - 1) / PieceBytes;
                for (var k = 0; k < count; k++)
                {
                    var part = bytes.AsSpan(k * PieceBytes, Math.Min(PieceBytes, bytes.Length - k * PieceBytes));
                    notifications.Add(JsonSerializer.Serialize(new Piece(_instance, id, count, k, Convert.ToBase64String(part))));
                }
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            foreach (var notification in notifications)
            {
                await using var command = new NpgsqlCommand("SELECT pg_notify(@channel, @payload)", connection);
                command.Parameters.AddWithValue("channel", Channel);
                command.Parameters.AddWithValue("payload", notification);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("A live message could not be passed to the other gateway instances: {Reason}", ex.GetType().Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled || _connectionString is null)
        {
            _ready.TrySetResult();
            return;
        }

        var listener = new NpgsqlConnectionStringBuilder(_connectionString) { Pooling = false, KeepAlive = 30 }.ConnectionString;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(listener);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, e) => _ = RelayAsync(e.Payload);
                await using (var command = new NpgsqlCommand($"LISTEN {Channel}", connection))
                    await command.ExecuteNonQueryAsync(stoppingToken);

                _ready.TrySetResult();
                while (!stoppingToken.IsCancellationRequested)
                    await connection.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _log.LogWarning("The gateway's live relay lost its database connection: {Reason}", ex.GetType().Name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
            }
        }
    }

    private async Task RelayAsync(string payload)
    {
        try
        {
            // A piece of a larger message: keep it until the rest has arrived.
            using (var document = JsonDocument.Parse(payload))
            {
                if (document.RootElement.TryGetProperty("k", out _))
                {
                    var piece = JsonSerializer.Deserialize<Piece>(payload);
                    if (piece is null || piece.Instance == _instance)
                        return;

                    var whole = AddPiece(piece);
                    if (whole is null)
                        return;

                    payload = whole;
                }
            }

            var message = JsonSerializer.Deserialize<Relayed>(payload);
            if (message is null || message.Instance == _instance)
                return;

            await _hub.Clients.Group(message.Group).SendAsync(message.Method, message.Payload);
        }
        catch (Exception ex)
        {
            _log.LogWarning("A live message from another gateway instance could not be passed on: {Reason}", ex.GetType().Name);
        }
    }

    // Returns the whole message when this was its last missing piece, otherwise null.
    private string? AddPiece(Piece piece)
    {
        if (piece.Count < 1 || piece.Count > MaxMessageBytes / PieceBytes + 2 || piece.Index < 0 || piece.Index >= piece.Count)
            return null;

        lock (_pending)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var stale in _pending.Where(p => now - p.Value.Started > PiecesKeptFor).Select(p => p.Key).ToList())
                _pending.Remove(stale);

            var key = piece.Instance + ":" + piece.Id;
            if (!_pending.TryGetValue(key, out var pending) || pending.Parts.Length != piece.Count)
                _pending[key] = pending = new Pending(now, new byte[piece.Count][]);

            pending.Parts[piece.Index] = Convert.FromBase64String(piece.Data);
            if (pending.Parts.Any(part => part is null))
                return null;

            _pending.Remove(key);
            return System.Text.Encoding.UTF8.GetString(pending.Parts.SelectMany(part => part).ToArray());
        }
    }

    private sealed record Pending(DateTimeOffset Started, byte[][] Parts);

    private sealed record Piece(
        [property: System.Text.Json.Serialization.JsonPropertyName("i")] string Instance,
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("n")] int Count,
        [property: System.Text.Json.Serialization.JsonPropertyName("k")] int Index,
        [property: System.Text.Json.Serialization.JsonPropertyName("d")] string Data);

    private sealed record Relayed(
        [property: System.Text.Json.Serialization.JsonPropertyName("i")] string Instance,
        [property: System.Text.Json.Serialization.JsonPropertyName("g")] string Group,
        [property: System.Text.Json.Serialization.JsonPropertyName("m")] string Method,
        [property: System.Text.Json.Serialization.JsonPropertyName("p")] JsonElement Payload);
}
