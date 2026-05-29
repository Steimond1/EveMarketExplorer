using System.Text.Json;
using EveMarketExplorer.Models;
using EveMarketExplorer.Services;
using EveMarketExplorer.ViewModels;

var tests = new TestSuite();

tests.Add("trade loop row renders stop-aligned path, items, and quantities", () =>
{
    var loop = SampleLoop();

    var row = TradeLoopRow.FromTradeLoop(loop, 7);

    Assert.Equal(7, row.Number);
    Assert.SequenceEqual(["Jita IV - Moon 4", "Amarr VIII", "Dodixie IX"], row.Path.Select(line => line.Text));
    Assert.SequenceEqual(["Tritanium", "Mexallon", "Pyerite"], row.Items.Select(line => line.Text));
    Assert.SequenceEqual(["10,000", "2,500", "900"], row.Quantities.Select(line => line.Text));
});

tests.Add("trade loop row exposes per-loop totals and peak cost", () =>
{
    var loop = SampleLoop();

    var row = TradeLoopRow.FromTradeLoop(loop, 1);

    Assert.Equal(18, row.Jumps);
    Assert.Equal(1_250_000m, row.PeakCost);
    Assert.Equal(12_000d, row.CargoVolume);
    Assert.Equal(450_000m, row.Profit);
    Assert.Equal(25_000m, row.ProfitPerJump);
    Assert.Equal(0.36, row.Margin, tolerance: 0.0000001);
});

tests.Add("trade loop row keeps sortable text for path and items", () =>
{
    var row = TradeLoopRow.FromTradeLoop(SampleLoop(), 1);

    Assert.Equal("Jita IV - Moon 4 -> Amarr VIII -> Dodixie IX", row.PathText);
    Assert.Equal("Tritanium | Mexallon | Pyerite", row.ItemsText);
});

tests.Add("trade loop route cache entry round-trips timestamp and blocked route", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "EveMarketExplorerTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);

    try
    {
        var cache = new EveCache(directory, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        });
        var cachedAt = DateTimeOffset.Parse("2026-05-29T12:00:00Z");
        var entries = new List<CachedTradeLoopRouteEntry>
        {
            new(30000142, 30002187, RouteMode.Safe, [30000142, 30002187], cachedAt),
            new(30000142, 30002659, RouteMode.Safe, null, cachedAt)
        };

        await cache.WriteAsync("trade-loop-route-cache.json", entries);
        var restored = await cache.TryReadAsync<List<CachedTradeLoopRouteEntry>>("trade-loop-route-cache.json");

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.SequenceEqual([30000142, 30002187], restored[0].Path!);
        Assert.Equal(cachedAt, restored[0].CachedAt);
        Assert.True(restored[1].Path is null);
    }
    finally
    {
        Directory.Delete(directory, recursive: true);
    }
});

tests.Add("gui search state round-trips trade loop rows", () =>
{
    var row = TradeLoopRow.FromTradeLoop(SampleLoop(), 1);
    var state = new GuiSearchState(
        "Jita",
        9_000_000m,
        12_000d,
        true,
        false,
        4,
        10d,
        1_000_000m,
        4,
        "Profit",
        true,
        "PeakCost",
        false,
        DateTimeOffset.Parse("2026-05-29T12:00:00Z"),
        [],
        [row]);

    var json = JsonSerializer.Serialize(state);
    var restored = JsonSerializer.Deserialize<GuiSearchState>(json);

    Assert.NotNull(restored);
    Assert.Equal(1, restored!.TradeLoops!.Count);
    Assert.Equal("PeakCost", restored.LoopSortMemberPath);
    Assert.SequenceEqual(row.Path.Select(line => line.Text), restored.TradeLoops[0].Path.Select(line => line.Text));
});

await tests.RunAsync();

static TradeLoop SampleLoop()
{
    return new TradeLoop(
        [
            new DisplayTradeLoopLeg(34, "Tritanium", "Jita", "Jita IV - Moon 4", "Amarr", "Amarr VIII", 5m, 6m, 5.8m, 10_000, 0.01, 100, 50_000m, 8_000m, 0.16),
            new DisplayTradeLoopLeg(36, "Mexallon", "Amarr", "Amarr VIII", "Dodixie", "Dodixie IX", 300m, 390m, 370m, 2_500, 0.01, 25, 750_000m, 175_000m, 0.23),
            new DisplayTradeLoopLeg(35, "Pyerite", "Dodixie", "Dodixie IX", "Jita", "Jita IV - Moon 4", 1_388.8889m, 1_700m, 1_685m, 900, 0.01, 9, 1_250_000m, 267_000m, 0.21)
        ],
        ["Jita", "Amarr", "Dodixie", "Jita"],
        18,
        1_250_000m,
        12_000d,
        450_000m,
        25_000m,
        0.36);
}

public sealed class TestSuite
{
    private readonly List<(string Name, Func<Task> Test)> tests = [];

    public void Add(string name, Action test)
    {
        tests.Add((name, () =>
        {
            test();
            return Task.CompletedTask;
        }));
    }

    public void Add(string name, Func<Task> test)
    {
        tests.Add((name, test));
    }

    public async Task RunAsync()
    {
        var failures = new List<string>();

        foreach (var (name, test) in tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                Console.WriteLine($"FAIL {name}");
                Console.WriteLine($"     {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{tests.Count - failures.Count}/{tests.Count} tests passed");

        if (failures.Count > 0)
        {
            Environment.ExitCode = 1;
        }
    }
}

public static class Assert
{
    public static void True(bool value, string? message = null)
    {
        if (!value)
        {
            throw new InvalidOperationException(message ?? "Expected true.");
        }
    }

    public static void NotNull<T>(T? value)
    {
        if (value is null)
        {
            throw new InvalidOperationException("Expected non-null value.");
        }
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void Equal(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();

        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidOperationException(
                $"Expected [{string.Join(", ", expectedArray)}], got [{string.Join(", ", actualArray)}].");
        }
    }
}
