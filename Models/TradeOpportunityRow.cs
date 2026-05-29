namespace EveParserAvalonia.Models;

public sealed class TradeOpportunityRow
{
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string BuyLocation { get; init; } = "";
    public string SellLocation { get; init; } = "";
    public int Jumps { get; init; }
    public decimal BuyPrice { get; init; }
    public decimal SellPrice { get; init; }
    public int Quantity { get; init; }
    public decimal ProfitPerJump { get; init; }
    public decimal Profit { get; init; }
    public double Margin { get; init; }
    public double TotalVolume { get; init; }
}
