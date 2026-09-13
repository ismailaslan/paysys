using Paysys.Domain.Entities;

namespace Paysys.Application.Cards;

public record CardResolution(CardToken CardToken, Account Account, Bank Bank);
