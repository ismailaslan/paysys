namespace Paysys.Shared.Tokenization;

// How a stored card brand is written for a person. Unknown means a card from a network Paysys does not
// recognise (an older card Paysys issued before it had its own brand is stored this way too).
public static class CardBrandNames
{
    public static string Display(string brand) => brand switch
    {
        "Paysys" => "Paysys",
        "Unknown" => "Card",
        _ => brand,
    };
}
