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
        int expiryYear,
        CardStatus initialStatus = CardStatus.Active)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token is required.", nameof(token));

        if (lastFourDigits is null || lastFourDigits.Length != 4 || !lastFourDigits.All(char.IsDigit))
            throw new ArgumentException("LastFourDigits must be exactly 4 digits.", nameof(lastFourDigits));

        if (expiryMonth < 1 || expiryMonth > 12)
            throw new ArgumentOutOfRangeException(nameof(expiryMonth), "ExpiryMonth must be between 1 and 12.");

        if (expiryYear < 1)
            throw new ArgumentOutOfRangeException(nameof(expiryYear), "ExpiryYear must be positive.");

        if (initialStatus is not (CardStatus.Active or CardStatus.Pending))
            throw new ArgumentException("A new card starts Active or Pending.", nameof(initialStatus));

        Id = id;
        AccountId = accountId;
        Token = token;
        LastFourDigits = lastFourDigits;
        CardBrand = cardBrand;
        ExpiryMonth = expiryMonth;
        ExpiryYear = expiryYear;
        CreatedAt = DateTimeOffset.UtcNow;
        Status = initialStatus;
    }

    // The allowed moves between statuses. The endpoints check first so they can give a plain message;
    // these throw if anything else calls them out of order.
    // The card's bank confirms a card the owner added, or rejects it (which closes it for good).
    public void Confirm()
    {
        if (Status != CardStatus.Pending)
            throw new InvalidOperationException("Only a card waiting for the bank can be confirmed.");
        Status = CardStatus.Active;
    }

    public void Reject()
    {
        if (Status != CardStatus.Pending)
            throw new InvalidOperationException("Only a card waiting for the bank can be rejected.");
        Status = CardStatus.Closed;
    }

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

        // Paused, not Active: the card may have been paused by its owner before the block (a lost card,
        // say), and the bank's unblock must not make it usable again. The owner resumes it.
        Status = CardStatus.Paused;
    }

    public void Close()
    {
        if (Status is CardStatus.Blocked or CardStatus.Closed)
            throw new InvalidOperationException("A blocked or closed card cannot be closed.");
        Status = CardStatus.Closed;
    }
}
