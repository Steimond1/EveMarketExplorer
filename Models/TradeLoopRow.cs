using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EveMarketExplorer.Services;

namespace EveMarketExplorer.Models;

public sealed class TradeLoopRow
{
    public int Number { get; init; }
    public IReadOnlyList<TradeLoopCellLine> Path { get; init; } = [];
    public IReadOnlyList<TradeLoopCellLine> Items { get; init; } = [];
    public IReadOnlyList<TradeLoopCellLine> Quantities { get; init; } = [];
    public int AvailableRuns { get; init; }
    public int Jumps { get; init; }
    public decimal PeakCost { get; init; }
    public double CargoVolume { get; init; }
    public decimal Profit { get; init; }
    public decimal ProfitPerJump { get; init; }
    public double Margin { get; init; }
    public string PathText { get; init; } = "";
    public string ItemsText { get; init; } = "";

    public static TradeLoopRow FromTradeLoop(TradeLoop loop, int number)
    {
        var path = loop.DisplayLegs
            .Select(leg => new TradeLoopCellLine { Text = leg.BuyLocation })
            .ToList();
        var items = loop.DisplayLegs
            .Select(leg => new TradeLoopCellLine { Text = leg.TypeName })
            .ToList();
        var quantities = loop.DisplayLegs
            .Select(leg => new TradeLoopCellLine { Text = leg.Quantity.ToString("N0", CultureInfo.InvariantCulture) })
            .ToList();

        return new TradeLoopRow
        {
            Number = number,
            Path = path,
            Items = items,
            Quantities = quantities,
            AvailableRuns = loop.AvailableRuns,
            Jumps = loop.Jumps,
            PeakCost = loop.RequiredIsk,
            CargoVolume = loop.CargoVolume,
            Profit = loop.Profit,
            ProfitPerJump = loop.ProfitPerJump,
            Margin = loop.Margin,
            PathText = string.Join(" -> ", path.Select(line => line.Text)),
            ItemsText = string.Join(" | ", items.Select(line => line.Text))
        };
    }
}

public sealed class TradeLoopCellLine
{
    public string Text { get; init; } = "";
}
