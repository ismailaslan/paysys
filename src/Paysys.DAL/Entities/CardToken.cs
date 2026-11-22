namespace Paysys.DAL.Entities;

public class CardToken
{
    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public string Token { get; private set; }
    public string LastFourDigits { get; private set; }
    public CardBrand CardBrand { get; private set; }
    public int ExpiryMonth { get; private set; }
    public int ExpiryYear { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public CardStatus Status { get; private set; }

    private CardToken()
    {
        // Required by EF Core for materialization.
        Token = string.Empty;
        LastFourDigits = string.Empty;
    }

    public CardToken(
        Guid id,
        Guid accountId,
        string token,
        string lastFourDigits,
        CardBrand cardBrand,
        int expiryMonth,
        int expiryYear)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token is required.", nameof(token));

        if (lastFourDigits is null || lastFourDigits.Length != 4 || !lastFourDigits.All(char.IsDigit))
            throw new ArgumentException("LastFourDigits must be exactly 4 digits.", nameof(lastFourDigits));

        if (expiryMonth < 1 || expiryMonth > 12)
            throw new ArgumentOutOfRangeException(nameof(expiryMonth), "ExpiryMonth must be between 1 and 12.");

        if (expiryYear < 1)
            throw new ArgumentOutOfRangeException(nameof(expiryYear), "ExpiryYear must be positive.");

        Id = id;
        AccountId = accountId;
        Token = token;
        LastFourDigits = lastFourDigits;
        CardBrand = cardBrand;
        ExpiryMonth = expiryMonth;
        ExpiryYear = expiryYear;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    // The allowed moves between statuses. The endpoints check first so they can give a plain message;
    // these throw if anything else calls them out of order.
    public void Pause()
    {
        if (Status != CardStatus.Active)
            throw new InvalidOperationException("Only an active card can be paused.");
        Status = CardStatus.Paused;
    }

    public void Resume()
    {
        if (Status != CardStatus.Paused)
            throw new InvalidOperationException("Only a paused card can be resumed.");
        Status = CardStatus.Active;
    }

    public void Block()
    {
        if (Status is not (CardStatus.Active or CardStatus.Paused))
            throw new InvalidOperationException("Only an active or paused card can be blocked.");
        Status = CardStatus.Blocked;
    }

    public void Unblock()
    {
        if (Status != CardStatus.Blocked)
            throw new InvalidOperationException("Only a blocked card can be unblocked.");
        Status = CardStatus.Active;
    }

    public void Close()
    {
        if (Status is CardStatus.Blocked or CardStatus.Closed)
            throw new InvalidOperationException("A blocked or closed card cannot be closed.");
        Status = CardStatus.Closed;
    }
}
