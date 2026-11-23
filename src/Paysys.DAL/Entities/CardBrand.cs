namespace Paysys.DAL.Entities;

public enum CardBrand
{
    Unknown,
    Visa,
    Mastercard,
    Amex,

    // Cards Paysys itself issues (numbers starting 99, which no card network uses). Appended after the
    // others: the brand is stored as a number, so existing rows keep their meaning.
    Paysys
}
