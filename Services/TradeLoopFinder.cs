using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EveMarketExplorer.Services;

public sealed class TradeLoopFinder
{
    private const string RouteCacheFileName = "trade-loop-route-cache.json";
    private const string LegacyRouteCacheFileName = "route-cache.json";
    private static readonly TimeSpan RouteCacheRefreshInterval = TimeSpan.FromDays(1);
    private const int MaxBuySystemsPerType = 70;
    private const int MaxSellSystemsPerType = 70;
    private const int MaxOutgoingLegsPerSystem = 24;
    private const int MaxStartSystemsWhenOpen = 70;
    private const int MaxResults = 300;

    private readonly EveEsiClient esi;
    private readonly EveCache cache;
    private readonly Dictionary<(int Origin, int Destination, RouteMode Mode), CachedTradeLoopRouteEntry> routeCache = [];
    private readonly HashSet<(int Origin, int Destination, RouteMode Mode)> refreshingRouteKeys = [];
    private readonly object routeCacheLock = new();
    private bool routeCacheLoaded;

    public TradeLoopFinder(EveEsiClient esi, EveCache cache)
    {
        this.esi = esi;
        this.cache = cache;
    }

    public async Task<List<TradeLoop>> FindAsync(
        TradeLoopSearchRequest request,
        UniverseData universe,
        IReadOnlyList<MarketOrder> orders,
        IReadOnlySet<int> contrabandTypeIds,
        IProgress<TradeLoopSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureRouteCacheLoadedAsync();
        var salesTaxRate = TradeMath.GetSalesTaxRate(request.AccountingLevel);
        var minMargin = request.MinimumMarginPercent / 100d;

        var typeIds = orders
            .Where(order => order.VolumeRemain > 0)
            .Select(order => order.TypeId)
            .Distinct()
            .ToArray();
        var typeDetails = await LoadTypeDetailsAsync(typeIds, cancellationToken);

        progress?.Report(new TradeLoopSearchProgress("Подбираю прибыльные плечи...", 10, 0));
        var candidateLegs = BuildCandidateLegs(request, orders, typeDetails, contrabandTypeIds, salesTaxRate, minMargin);
        var adjacency = candidateLegs
            .GroupBy(leg => leg.OriginSystemId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(leg => leg.ProfitPerJumpEstimate)
                    .ThenByDescending(leg => leg.Profit)
                    .Take(MaxOutgoingLegsPerSystem)
                    .ToList());

        var startSystems = GetStartSystems(request, candidateLegs, adjacency);
        var loops = new List<CandidateTradeLoop>();
        var seenLoops = new HashSet<string>(StringComparer.Ordinal);
        var processedStarts = 0;

        foreach (var startSystemId in startSystems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedStarts++;
            progress?.Report(new TradeLoopSearchProgress(
                $"Проверяю кольца: {processedStarts:N0}/{startSystems.Count:N0}",
                10 + processedStarts * 80d / Math.Max(1, startSystems.Count),
                loops.Count));

            if (!adjacency.ContainsKey(startSystemId))
            {
                continue;
            }

            await SearchFromAsync(
                startSystemId,
                startSystemId,
                request,
                universe,
                adjacency,
                new HashSet<int> { startSystemId },
                [],
                loops,
                seenLoops,
                cancellationToken);
        }

        progress?.Report(new TradeLoopSearchProgress("Загружаю названия станций...", 95, loops.Count));
        var locationNames = await LoadLocationNamesAsync(orders, loops.SelectMany(loop => loop.Legs).Select(leg => leg.TypeId).ToHashSet());

        return loops
            .OrderByDescending(loop => loop.ProfitPerJump)
            .ThenByDescending(loop => loop.Profit)
            .Take(MaxResults)
            .Select(loop => loop.ToTradeLoop(universe, locationNames))
            .ToList();
    }

    private static List<TradeLoopLeg> BuildCandidateLegs(
        TradeLoopSearchRequest request,
        IReadOnlyList<MarketOrder> orders,
        IReadOnlyDictionary<int, TypeDetails> typeDetails,
        IReadOnlySet<int> contrabandTypeIds,
        double salesTaxRate,
        double minMargin)
    {
        var sellOrders = orders
            .Where(order => !order.IsBuyOrder && order.VolumeRemain > 0)
            .GroupBy(order => order.TypeId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(order => order.SystemId)
                    .Select(systemGroup => systemGroup.OrderBy(order => order.Price).First())
                    .OrderBy(order => order.Price)
                    .Take(MaxSellSystemsPerType)
                    .ToList());

        var buyOrders = orders
            .Where(order => order.IsBuyOrder && order.VolumeRemain > 0)
            .GroupBy(order => order.TypeId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(order => order.SystemId)
                    .Select(systemGroup => systemGroup.OrderByDescending(order => order.Price).First())
                    .OrderByDescending(order => order.Price)
                    .Take(MaxBuySystemsPerType)
                    .ToList());

        var legs = new List<TradeLoopLeg>();
        foreach (var (typeId, sellsBySystem) in sellOrders)
        {
            if (!buyOrders.TryGetValue(typeId, out var buysBySystem) ||
                !typeDetails.TryGetValue(typeId, out var type) ||
                !type.IsCargoCompatible ||
                !type.IsTransportable ||
                type.Volume <= 0 ||
                type.Volume > request.CargoVolume ||
                (!request.IncludeContraband && contrabandTypeIds.Contains(typeId)))
            {
                continue;
            }

            foreach (var sellOrder in sellsBySystem)
            {
                if (sellOrder.Price > request.Budget)
                {
                    continue;
                }

                var maxUnitsByMoney = ClampUnits(request.Budget / sellOrder.Price);
                var maxUnitsByCargo = ClampUnits(request.CargoVolume / type.Volume);
                var baseQuantity = Math.Min(maxUnitsByMoney, Math.Min(maxUnitsByCargo, sellOrder.VolumeRemain));
                if (baseQuantity <= 0)
                {
                    continue;
                }

                foreach (var buyOrder in buysBySystem)
                {
                    if (buyOrder.SystemId == sellOrder.SystemId)
                    {
                        continue;
                    }

                    var netSellPrice = TradeMath.GetNetSellPrice(buyOrder.Price, salesTaxRate);
                    if (netSellPrice < sellOrder.Price * (decimal)(1 + minMargin))
                    {
                        break;
                    }

                    var quantity = Math.Min(baseQuantity, buyOrder.VolumeRemain);
                    if (quantity <= 0)
                    {
                        continue;
                    }

                    var profit = (netSellPrice - sellOrder.Price) * quantity;
                    var cost = sellOrder.Price * quantity;
                    legs.Add(new TradeLoopLeg(
                        typeId,
                        type.Name,
                        sellOrder.SystemId,
                        buyOrder.SystemId,
                        sellOrder.LocationId,
                        buyOrder.LocationId,
                        sellOrder.Price,
                        buyOrder.Price,
                        netSellPrice,
                        quantity,
                        type.Volume,
                        type.Volume * quantity,
                        cost,
                        profit,
                        (double)((netSellPrice - sellOrder.Price) / sellOrder.Price)));
                }
            }
        }

        return legs;
    }

    private static List<int> GetStartSystems(
        TradeLoopSearchRequest request,
        IReadOnlyList<TradeLoopLeg> legs,
        IReadOnlyDictionary<int, List<TradeLoopLeg>> adjacency)
    {
        if (request.StartSystem is not null)
        {
            return [request.StartSystem.Id];
        }

        return legs
            .Where(leg => adjacency.ContainsKey(leg.DestinationSystemId))
            .GroupBy(leg => leg.OriginSystemId)
            .OrderByDescending(group => group.Max(leg => leg.Profit))
            .Take(MaxStartSystemsWhenOpen)
            .Select(group => group.Key)
            .ToList();
    }

    private async Task SearchFromAsync(
        int startSystemId,
        int currentSystemId,
        TradeLoopSearchRequest request,
        UniverseData universe,
        IReadOnlyDictionary<int, List<TradeLoopLeg>> adjacency,
        HashSet<int> visitedSystems,
        List<TradeLoopLeg> path,
        List<CandidateTradeLoop> loops,
        HashSet<string> seenLoops,
        CancellationToken cancellationToken)
    {
        if (!adjacency.TryGetValue(currentSystemId, out var outgoing))
        {
            return;
        }

        if (path.Count >= 1)
        {
            foreach (var closingLeg in outgoing.Where(leg => leg.DestinationSystemId == startSystemId))
            {
                var loopLegs = path.Concat([closingLeg]).ToList();
                if (loopLegs.Count < 2 || loopLegs.Count > request.MaxStops)
                {
                    continue;
                }

                var key = GetLoopKey(startSystemId, loopLegs);
                if (!seenLoops.Add(key))
                {
                    continue;
                }

                var loop = await TryCreateLoopAsync(loopLegs, request, universe, cancellationToken);
                if (loop is not null)
                {
                    loops.Add(loop);
                }
            }
        }

        if (path.Count >= request.MaxStops - 1)
        {
            return;
        }

        foreach (var leg in outgoing)
        {
            if (leg.DestinationSystemId == startSystemId ||
                visitedSystems.Contains(leg.DestinationSystemId))
            {
                continue;
            }

            visitedSystems.Add(leg.DestinationSystemId);
            path.Add(leg);
            await SearchFromAsync(
                startSystemId,
                leg.DestinationSystemId,
                request,
                universe,
                adjacency,
                visitedSystems,
                path,
                loops,
                seenLoops,
                cancellationToken);
            path.RemoveAt(path.Count - 1);
            visitedSystems.Remove(leg.DestinationSystemId);
        }
    }

    private async Task<CandidateTradeLoop?> TryCreateLoopAsync(
        IReadOnlyList<TradeLoopLeg> legs,
        TradeLoopSearchRequest request,
        UniverseData universe,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var routeNames = new List<string>();
        var routeSystemIds = new List<int>();
        var jumps = 0;

        foreach (var leg in legs)
        {
            var route = await GetRouteAsync(leg.OriginSystemId, leg.DestinationSystemId, request.RouteMode, universe);
            if (route is null || route.Jumps <= 0)
            {
                return null;
            }

            jumps += route.Jumps;
            if (routeSystemIds.Count == 0)
            {
                routeSystemIds.AddRange(route.Path);
            }
            else
            {
                routeSystemIds.AddRange(route.Path.Skip(1));
            }
        }

        var profit = legs.Sum(leg => leg.Profit);
        var requiredIsk = legs.Max(leg => leg.Cost);
        if (profit < request.MinimumProfit || requiredIsk > request.Budget)
        {
            return null;
        }

        foreach (var systemId in routeSystemIds)
        {
            routeNames.Add(universe.SystemsById.TryGetValue(systemId, out var system)
                ? system.Name
                : systemId.ToString(CultureInfo.InvariantCulture));
        }

        var profitPerJump = jumps <= 0 ? profit : profit / jumps;
        var margin = requiredIsk <= 0 ? 0 : (double)(profit / requiredIsk);
        if (margin < request.MinimumMarginPercent / 100d)
        {
            return null;
        }

        return new CandidateTradeLoop(
            legs.ToList(),
            routeNames,
            jumps,
            requiredIsk,
            legs.Max(leg => leg.TotalVolume),
            profit,
            profitPerJump,
            margin);
    }

    private async Task<Dictionary<int, TypeDetails>> LoadTypeDetailsAsync(IEnumerable<int> typeIds, CancellationToken cancellationToken)
    {
        var cached = await cache.TryReadAsync<Dictionary<int, TypeDetails>>("types.json") ?? new Dictionary<int, TypeDetails>();
        RepairOldTypeCache(cached);
        var missing = typeIds.Where(typeId => !cached.ContainsKey(typeId)).Distinct().ToArray();

        if (missing.Length > 0)
        {
            var loaded = new ConcurrentBag<TypeDetails>();
            await Parallel.ForEachAsync(
                missing,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 8,
                    CancellationToken = cancellationToken
                },
                async (typeId, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var type = await esi.GetTypeAsync(typeId);
                    var volume = GetCargoVolume(type);
                    loaded.Add(new TypeDetails(
                        typeId,
                        type.Name,
                        volume,
                        IsCargoCompatible(volume),
                        !NonTransportableTypeRules.IsBlocked(type.Name)));
                });

            foreach (var type in loaded)
            {
                cached[type.TypeId] = type;
            }

            await cache.WriteAsync("types.json", cached);
        }

        return cached;
    }

    private async Task<Dictionary<long, string>> LoadLocationNamesAsync(
        IReadOnlyList<MarketOrder> orders,
        IReadOnlySet<int> typeIds)
    {
        var locationIds = orders
            .Where(order => typeIds.Contains(order.TypeId) && IsNpcStationId(order.LocationId))
            .Select(order => order.LocationId)
            .Distinct()
            .ToArray();

        var cached = await cache.TryReadAsync<Dictionary<long, string>>("location-names.json") ?? new Dictionary<long, string>();
        var missing = locationIds.Where(locationId => !cached.ContainsKey(locationId)).ToArray();

        foreach (var chunk in missing.Chunk(1000))
        {
            var names = await esi.TryGetNamesAsync(chunk);
            foreach (var name in names)
            {
                cached[name.Id] = name.Name;
            }
        }

        if (missing.Length > 0)
        {
            await cache.WriteAsync("location-names.json", cached);
        }

        return cached;
    }

    private async Task<RouteInfo?> GetRouteAsync(
        int origin,
        int destination,
        RouteMode mode,
        UniverseData universe)
    {
        var key = (origin, destination, mode);
        CachedTradeLoopRouteEntry? cached;
        lock (routeCacheLock)
        {
            routeCache.TryGetValue(key, out cached);
        }

        if (cached is not null)
        {
            if (DateTimeOffset.UtcNow - cached.CachedAt > RouteCacheRefreshInterval)
            {
                QueueRouteRefresh(origin, destination, mode, universe);
            }

            return cached.Path is null
                ? null
                : new RouteInfo(cached.Path.Count - 1, cached.Path);
        }

        return await RefreshRouteCacheEntryAsync(origin, destination, mode, universe);
    }

    private void QueueRouteRefresh(
        int origin,
        int destination,
        RouteMode mode,
        UniverseData universe)
    {
        var key = (origin, destination, mode);
        lock (routeCacheLock)
        {
            if (!refreshingRouteKeys.Add(key))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshRouteCacheEntryAsync(origin, destination, mode, universe);
            }
            catch
            {
            }
            finally
            {
                lock (routeCacheLock)
                {
                    refreshingRouteKeys.Remove(key);
                }
            }
        });
    }

    private async Task<RouteInfo?> RefreshRouteCacheEntryAsync(
        int origin,
        int destination,
        RouteMode mode,
        UniverseData universe)
    {
        var key = (origin, destination, mode);
        var flag = mode == RouteMode.Safe ? "secure" : "shortest";
        var path = await esi.GetRouteAsync(origin, destination, flag);
        if (path is null)
        {
            SetCachedRoute(key, new CachedTradeLoopRouteEntry(origin, destination, mode, null, DateTimeOffset.UtcNow));
            await SaveRouteCacheAsync();
            return null;
        }

        if (mode == RouteMode.Safe)
        {
            foreach (var systemId in path)
            {
                var system = await EnsureSolarSystemDetailsAsync(systemId, universe);
                if (system.SecurityStatus <= 0.5)
                {
                    SetCachedRoute(key, new CachedTradeLoopRouteEntry(origin, destination, mode, null, DateTimeOffset.UtcNow));
                    await SaveRouteCacheAsync();
                    return null;
                }
            }
        }

        var route = new RouteInfo(path.Count - 1, path);
        SetCachedRoute(key, new CachedTradeLoopRouteEntry(origin, destination, mode, path, DateTimeOffset.UtcNow));
        await SaveRouteCacheAsync();
        return route;
    }

    private async Task EnsureRouteCacheLoadedAsync()
    {
        if (routeCacheLoaded)
        {
            return;
        }

        var cached = await cache.TryReadAsync<List<CachedTradeLoopRouteEntry>>(RouteCacheFileName) ?? [];
        lock (routeCacheLock)
        {
            foreach (var route in cached)
            {
                routeCache[(route.Origin, route.Destination, route.Mode)] = route;
            }
        }

        var legacyCached = await cache.TryReadAsync<List<CachedRoute>>(LegacyRouteCacheFileName) ?? [];
        lock (routeCacheLock)
        {
            foreach (var route in legacyCached)
            {
                routeCache.TryAdd(
                    (route.Origin, route.Destination, route.Mode),
                    new CachedTradeLoopRouteEntry(
                        route.Origin,
                        route.Destination,
                        route.Mode,
                        route.Path,
                        DateTimeOffset.UtcNow - RouteCacheRefreshInterval - TimeSpan.FromMinutes(1)));
            }
        }

        routeCacheLoaded = true;
    }

    private async Task SaveRouteCacheAsync()
    {
        List<CachedTradeLoopRouteEntry> routes;
        lock (routeCacheLock)
        {
            routes = routeCache.Values.ToList();
        }

        await cache.WriteAsync(RouteCacheFileName, routes);
    }

    private void SetCachedRoute(
        (int Origin, int Destination, RouteMode Mode) key,
        CachedTradeLoopRouteEntry route)
    {
        lock (routeCacheLock)
        {
            routeCache[key] = route;
        }
    }

    private async Task<SolarSystem> EnsureSolarSystemDetailsAsync(int systemId, UniverseData universe)
    {
        if (universe.SystemsById.TryGetValue(systemId, out var cached) &&
            cached.SecurityStatus != AppDefaults.UnknownSecurityStatus)
        {
            return cached;
        }

        var esiSystem = await esi.GetSolarSystemAsync(systemId);
        var updated = new SolarSystem(esiSystem.SystemId, esiSystem.Name, esiSystem.SecurityStatus);

        universe.SystemsById[systemId] = updated;
        universe.SystemsByName[updated.Name] = updated;

        var index = universe.Systems.FindIndex(system => system.Id == systemId);
        if (index >= 0)
        {
            universe.Systems[index] = updated;
        }
        else
        {
            universe.Systems.Add(updated);
        }

        return updated;
    }

    private static string GetLoopKey(int startSystemId, IReadOnlyList<TradeLoopLeg> legs)
    {
        var typePath = string.Join(">", legs.Select(leg => $"{leg.OriginSystemId}:{leg.DestinationSystemId}:{leg.TypeId}"));
        return $"{startSystemId}|{typePath}";
    }

    private static string FormatLocation(long locationId, string systemName, IReadOnlyDictionary<long, string> locationNames)
    {
        if (locationId <= 0)
        {
            return systemName;
        }

        return locationNames.TryGetValue(locationId, out var locationName)
            ? locationName
            : $"{systemName} / Location {locationId}";
    }

    private static bool IsNpcStationId(long locationId)
    {
        return locationId is >= 60_000_000 and < 70_000_000;
    }

    private static void RepairOldTypeCache(Dictionary<int, TypeDetails> cached)
    {
        foreach (var (typeId, type) in cached.ToArray())
        {
            if (!type.IsCargoCompatible && IsCargoCompatible(type.Volume))
            {
                cached[typeId] = type with { IsCargoCompatible = true };
            }

            if (!type.IsTransportable && !NonTransportableTypeRules.IsBlocked(type.Name))
            {
                cached[typeId] = cached[typeId] with { IsTransportable = true };
            }

            if (type.IsTransportable && NonTransportableTypeRules.IsBlocked(type.Name))
            {
                cached[typeId] = cached[typeId] with { IsTransportable = false };
            }
        }
    }

    private static double GetCargoVolume(EsiType type)
    {
        return type.PackagedVolume is > 0
            ? type.PackagedVolume.Value
            : type.Volume;
    }

    private static bool IsCargoCompatible(double volume)
    {
        return volume > 0 && !double.IsNaN(volume) && !double.IsInfinity(volume);
    }

    private static int ClampUnits(decimal units)
    {
        if (units <= 0)
        {
            return 0;
        }

        return units >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Floor(units);
    }

    private static int ClampUnits(double units)
    {
        if (units <= 0 || double.IsNaN(units) || double.IsInfinity(units))
        {
            return 0;
        }

        return units >= int.MaxValue
            ? int.MaxValue
            : (int)Math.Floor(units);
    }

    private sealed record TradeLoopLeg(
        int TypeId,
        string TypeName,
        int OriginSystemId,
        int DestinationSystemId,
        long BuyLocationId,
        long SellLocationId,
        decimal BuyPrice,
        decimal SellPrice,
        decimal NetSellPrice,
        int Quantity,
        double UnitVolume,
        double TotalVolume,
        decimal Cost,
        decimal Profit,
        double Margin)
    {
        public decimal ProfitPerJumpEstimate => Profit;

        public DisplayTradeLoopLeg WithDisplayNames(
            UniverseData universe,
            IReadOnlyDictionary<long, string> locationNames)
        {
            var origin = universe.SystemsById.TryGetValue(OriginSystemId, out var originSystem)
                ? originSystem.Name
                : OriginSystemId.ToString(CultureInfo.InvariantCulture);
            var destination = universe.SystemsById.TryGetValue(DestinationSystemId, out var destinationSystem)
                ? destinationSystem.Name
                : DestinationSystemId.ToString(CultureInfo.InvariantCulture);

            return new DisplayTradeLoopLeg(
                TypeId,
                TypeName,
                origin,
                FormatLocation(BuyLocationId, origin, locationNames),
                destination,
                FormatLocation(SellLocationId, destination, locationNames),
                BuyPrice,
                SellPrice,
                NetSellPrice,
                Quantity,
                UnitVolume,
                TotalVolume,
                Cost,
                Profit,
                Margin);
        }
    }

    private sealed record CandidateTradeLoop(
        List<TradeLoopLeg> Legs,
        List<string> Route,
        int Jumps,
        decimal RequiredIsk,
        double CargoVolume,
        decimal Profit,
        decimal ProfitPerJump,
        double Margin)
    {
        public TradeLoop ToTradeLoop(UniverseData universe, IReadOnlyDictionary<long, string> locationNames)
        {
            return new TradeLoop(
                Legs.Select(leg => leg.WithDisplayNames(universe, locationNames)).ToList(),
                Route,
                Jumps,
                RequiredIsk,
                CargoVolume,
                Profit,
                ProfitPerJump,
                Margin);
        }
    }
}

public sealed record TradeLoopSearchRequest(
    SolarSystem? StartSystem,
    decimal Budget,
    double CargoVolume,
    RouteMode RouteMode,
    bool IncludeContraband,
    int AccountingLevel,
    double MinimumMarginPercent,
    decimal MinimumProfit,
    int MaxStops);

public sealed record TradeLoopSearchProgress(string Stage, double Percent, int FoundLoops);

public sealed record CachedTradeLoopRouteEntry(
    int Origin,
    int Destination,
    RouteMode Mode,
    List<int>? Path,
    DateTimeOffset CachedAt);

public sealed record DisplayTradeLoopLeg(
    int TypeId,
    string TypeName,
    string BuySystem,
    string BuyLocation,
    string SellSystem,
    string SellLocation,
    decimal BuyPrice,
    decimal SellPrice,
    decimal NetSellPrice,
    int Quantity,
    double UnitVolume,
    double TotalVolume,
    decimal Cost,
    decimal Profit,
    double Margin);

public sealed record TradeLoop(
    List<DisplayTradeLoopLeg> DisplayLegs,
    List<string> Route,
    int Jumps,
    decimal RequiredIsk,
    double CargoVolume,
    decimal Profit,
    decimal ProfitPerJump,
    double Margin);
