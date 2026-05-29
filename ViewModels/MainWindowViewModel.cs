using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EveParserAvalonia.Models;

namespace EveParserAvalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private const string CompatibilityDate = "2026-05-28";
    private const string LastSearchFileName = "avalonia-last-search.json";

    private readonly JsonSerializerOptions json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpClient httpClient;
    private readonly EveCache cache;
    private readonly MarketDataService marketData;
    private readonly TradeOpportunityFinder opportunityFinder;
    private readonly ContrabandDataSource contraband;

    private UniverseData? universe;
    private CachedOrders? marketOrders;
    private Task<CachedOrders>? cacheRefreshTask;
    private string currentSortMemberPath = "Profit";
    private bool currentSortDescending = true;
    private bool restoredLastSearch;

    [ObservableProperty]
    private string systemName = "Jita";

    [ObservableProperty]
    private decimal budget = 9_000_000m;

    [ObservableProperty]
    private double cargoVolume = 12_000;

    [ObservableProperty]
    private bool safeRoutes = true;

    [ObservableProperty]
    private bool includeContraband;

    [ObservableProperty]
    private int accountingLevel;

    [ObservableProperty]
    private double minimumMargin = 10;

    [ObservableProperty]
    private decimal minimumProfit = 1_000_000m;

    [ObservableProperty]
    private string status = "Готово к расчету.";

    [ObservableProperty]
    private string toastMessage = "";

    [ObservableProperty]
    private bool isToastVisible;

    [ObservableProperty]
    private string cacheStatusText = "Кэш: проверяю...";

    [ObservableProperty]
    private IBrush cacheStatusBrush = Brushes.Goldenrod;

    [ObservableProperty]
    private bool isCacheRefreshing;

    [ObservableProperty]
    private string cacheRefreshProgress = "Проверяю состояние кэша.";

    [ObservableProperty]
    private bool isSearchRunning;

    [ObservableProperty]
    private double searchProgressValue;

    [ObservableProperty]
    private string searchProgressText = "Расчет не запущен.";

    [ObservableProperty]
    private bool isLastResultWarningVisible;

    [ObservableProperty]
    private string lastResultWarning = "";

    public MainWindowViewModel()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        Directory.CreateDirectory(GetCacheDirectory());

        httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://esi.evetech.net/latest/")
        };
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EveParserAvalonia", "1.0"));
        httpClient.DefaultRequestHeaders.Add("X-Compatibility-Date", CompatibilityDate);

        cache = new EveCache(GetCacheDirectory(), json);
        var esi = new EveEsiClient(httpClient, json);
        marketData = new MarketDataService(esi, cache);
        opportunityFinder = new TradeOpportunityFinder(esi, cache);
        contraband = new ContrabandDataSource(httpClient, cache);

        SystemNames = LoadSystemNames();
        Opportunities = [];
        RestoreLastSearchState();
        _ = InitializeAsync();
    }

    public IReadOnlyList<string> SystemNames { get; }

    public ObservableCollection<TradeOpportunityRow> Opportunities { get; }

    public TableSortState CurrentSortState => new(currentSortMemberPath, currentSortDescending);

    public TableSortState SortBy(string sortMemberPath)
    {
        var sortState = RememberSortBy(sortMemberPath);
        ApplyCurrentSort();
        SaveLastSearchState();
        return sortState;
    }

    public TableSortState RememberSortBy(string sortMemberPath)
    {
        if (string.Equals(currentSortMemberPath, sortMemberPath, StringComparison.Ordinal))
        {
            currentSortDescending = !currentSortDescending;
        }
        else
        {
            currentSortMemberPath = sortMemberPath;
            currentSortDescending = false;
        }

        SaveLastSearchState();
        return CurrentSortState;
    }

    [RelayCommand]
    private async Task FindOpportunities()
    {
        try
        {
            IsSearchRunning = true;
            IsLastResultWarningVisible = false;
            LastResultWarning = "";
            SearchProgressValue = 0;
            SearchProgressText = "Готовлю данные для расчета...";
            Status = "Готовлю данные для расчета...";

            universe ??= await marketData.LoadUniverseAsync();
            marketOrders ??= cacheRefreshTask is { IsCompletedSuccessfully: true }
                ? cacheRefreshTask.Result
                : await marketData.LoadMarketOrdersAsync(universe.Regions);

            if (!universe.SystemsByName.TryGetValue(SystemName.Trim(), out var startSystem))
            {
                Status = $"Система не найдена: {SystemName}";
                SearchProgressText = Status;
                return;
            }

            var routeMode = SafeRoutes ? RouteMode.Safe : RouteMode.Risky;
            var contrabandTypeIds = IncludeContraband
                ? new HashSet<int>()
                : await contraband.GetContrabandTypeIdsAsync();

            Status = $"Считаю сделки... Налог продажи: {TradeMath.GetSalesTaxRate(AccountingLevel):P2}";
            SearchProgressText = "Подбираю товары и маршруты...";
            var request = new TradeSearchRequest(
                startSystem,
                Budget,
                CargoVolume,
                routeMode,
                IncludeContraband,
                AccountingLevel,
                MinimumMargin,
                MinimumProfit);

            var progress = new Progress<TradeSearchProgress>(value =>
            {
                SearchProgressValue = value.TotalTypes <= 0
                    ? 0
                    : value.ProcessedTypes * 100d / value.TotalTypes;
                SearchProgressText =
                    $"Обработано товаров: {value.ProcessedTypes:N0}/{value.TotalTypes:N0}, найдено: {value.FoundOpportunities:N0}";
            });

            var opportunities = await opportunityFinder.FindAsync(
                request,
                universe,
                marketOrders.Orders,
                contrabandTypeIds,
                progress);

            Opportunities.Clear();
            foreach (var row in opportunities.Select(ToRow))
            {
                Opportunities.Add(row);
            }

            ApplyCurrentSort();

            Status = Opportunities.Count == 0
                ? "Подходящих сделок не найдено."
                : $"Найдено сделок: {Opportunities.Count:N0}";
            SearchProgressText = Status;
            SaveLastSearchState();
        }
        catch (Exception ex)
        {
            Status = GetSearchErrorText(ex);
            SearchProgressText = Status;
        }
        finally
        {
            IsSearchRunning = false;
        }
    }

    [RelayCommand]
    private async Task RefreshCache()
    {
        await StartCacheRefreshAsync("Обновляю кэш...");
    }

    private async Task InitializeAsync()
    {
        var cacheStatus = await UpdateCacheStatusAsync();
        if (restoredLastSearch && Opportunities.Count > 0 && !cacheStatus.IsFresh)
        {
            ShowStaleLastResultWarning();
        }

        if (cacheStatus.CreatedAt is null || !cacheStatus.IsFresh)
        {
            _ = StartCacheRefreshAsync("Кэш устарел, обновляю в фоне...");
        }
    }

    private async Task StartCacheRefreshAsync(string statusText)
    {
        if (cacheRefreshTask is { IsCompleted: false })
        {
            Status = "Обновление кэша уже идет.";
            return;
        }

        try
        {
            IsCacheRefreshing = true;
            CacheStatusText = "Кэш обновляется";
            CacheStatusBrush = Brushes.Goldenrod;
            CacheRefreshProgress = "Обновление кэша: загрузка карты...";
            Status = statusText;

            universe ??= await marketData.LoadUniverseAsync();
            var progress = new Progress<MarketRefreshProgress>(value =>
            {
                CacheRefreshProgress =
                    $"Регионов: {value.CompletedRegions}/{value.TotalRegions}, ордеров: {value.OrdersCount:N0}";
            });

            cacheRefreshTask = marketData.RefreshMarketOrdersAsync(universe.Regions, progress);
            marketOrders = await cacheRefreshTask;
            Status = $"Кэш обновлен: {marketOrders.Orders.Count:N0} ордеров.";

            if (restoredLastSearch && Opportunities.Count > 0)
            {
                ShowStaleLastResultWarning();
            }
        }
        catch (Exception ex)
        {
            Status = $"Не удалось обновить кэш: {ex.Message}";
            CacheRefreshProgress = Status;
        }
        finally
        {
            IsCacheRefreshing = false;
            cacheRefreshTask = null;
            await UpdateCacheStatusAsync();
        }
    }

    private async Task<CacheStatus> UpdateCacheStatusAsync()
    {
        var cacheStatus = await marketData.GetMarketCacheStatusAsync();
        if (cacheStatus.CreatedAt is null)
        {
            CacheStatusText = "Кэш: нет";
            CacheStatusBrush = Brushes.IndianRed;
            CacheRefreshProgress = "Кэш рынка еще не создан. Нажмите обновить кэш.";
            return cacheStatus;
        }

        CacheStatusText = $"Кэш: {cacheStatus.CreatedAt.Value.LocalDateTime:dd.MM.yyyy HH:mm}";
        CacheStatusBrush = cacheStatus.IsFresh ? Brushes.LimeGreen : Brushes.IndianRed;
        CacheRefreshProgress = cacheStatus.IsFresh
            ? $"Кэш свежий: {cacheStatus.OrdersCount:N0} ордеров."
            : $"Кэш устарел: {cacheStatus.OrdersCount:N0} ордеров.";
        return cacheStatus;
    }

    private void ShowStaleLastResultWarning()
    {
        LastResultWarning = "Показан результат прошлого поиска. Кэш был устаревшим или обновился после него, рекомендуется выполнить поиск заново.";
        IsLastResultWarningVisible = true;
    }

    private void RestoreLastSearchState()
    {
        var path = GetLastSearchPath();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<GuiSearchState>(stream, json);
            if (state is null)
            {
                return;
            }

            SystemName = state.SystemName;
            Budget = state.Budget;
            CargoVolume = state.CargoVolume;
            SafeRoutes = state.SafeRoutes;
            IncludeContraband = state.IncludeContraband;
            AccountingLevel = state.AccountingLevel;
            MinimumMargin = state.MinimumMargin;
            MinimumProfit = state.MinimumProfit;
            currentSortMemberPath = string.IsNullOrWhiteSpace(state.SortMemberPath) ? "Profit" : state.SortMemberPath;
            currentSortDescending = state.SortDescending;

            Opportunities.Clear();
            foreach (var row in state.Opportunities)
            {
                Opportunities.Add(row);
            }

            ApplyCurrentSort();
            restoredLastSearch = Opportunities.Count > 0;
            if (restoredLastSearch)
            {
                Status = $"Восстановлен прошлый результат: {Opportunities.Count:N0} сделок.";
            }
        }
        catch
        {
            Status = "Не удалось восстановить прошлый результат поиска.";
        }
    }

    private void SaveLastSearchState()
    {
        try
        {
            var state = new GuiSearchState(
                SystemName,
                Budget,
                CargoVolume,
                SafeRoutes,
                IncludeContraband,
                AccountingLevel,
                MinimumMargin,
                MinimumProfit,
                currentSortMemberPath,
                currentSortDescending,
                DateTimeOffset.UtcNow,
                Opportunities.ToList());

            using var stream = File.Create(GetLastSearchPath());
            JsonSerializer.Serialize(stream, state, json);
        }
        catch
        {
            Status = "Не удалось сохранить последний результат поиска.";
        }
    }

    private static TradeOpportunityRow ToRow(TradeOpportunity opportunity, int index)
    {
        return new TradeOpportunityRow
        {
            Number = index + 1,
            Name = opportunity.Name,
            BuyLocation = opportunity.BuyLocation,
            SellLocation = opportunity.SellLocation,
            Jumps = opportunity.Jumps,
            BuyPrice = opportunity.BuyPrice,
            SellPrice = opportunity.SellPrice,
            Quantity = opportunity.Quantity,
            ProfitPerJump = opportunity.ProfitPerJump,
            Profit = opportunity.Profit,
            Margin = opportunity.Margin,
            UnitVolume = opportunity.UnitVolume,
            TotalVolume = opportunity.TotalVolume
        };
    }

    private void ApplyCurrentSort()
    {
        var sorted = currentSortDescending
            ? Opportunities.OrderByDescending(GetSortValue).ToList()
            : Opportunities.OrderBy(GetSortValue).ToList();

        Opportunities.Clear();
        foreach (var (row, index) in sorted.Select((row, index) => (row, index + 1)))
        {
            Opportunities.Add(CopyWithNumber(row, index));
        }
    }

    private object GetSortValue(TradeOpportunityRow row)
    {
        return currentSortMemberPath switch
        {
            nameof(TradeOpportunityRow.Name) => row.Name,
            nameof(TradeOpportunityRow.BuyLocation) => row.BuyLocation,
            nameof(TradeOpportunityRow.SellLocation) => row.SellLocation,
            nameof(TradeOpportunityRow.Jumps) => row.Jumps,
            nameof(TradeOpportunityRow.BuyPrice) => row.BuyPrice,
            nameof(TradeOpportunityRow.SellPrice) => row.SellPrice,
            nameof(TradeOpportunityRow.Quantity) => row.Quantity,
            nameof(TradeOpportunityRow.ProfitPerJump) => row.ProfitPerJump,
            nameof(TradeOpportunityRow.Profit) => row.Profit,
            nameof(TradeOpportunityRow.Margin) => row.Margin,
            nameof(TradeOpportunityRow.TotalVolume) => row.TotalVolume,
            _ => row.Number
        };
    }

    private static TradeOpportunityRow CopyWithNumber(TradeOpportunityRow row, int number)
    {
        return new TradeOpportunityRow
        {
            Number = number,
            Name = row.Name,
            BuyLocation = row.BuyLocation,
            SellLocation = row.SellLocation,
            Jumps = row.Jumps,
            BuyPrice = row.BuyPrice,
            SellPrice = row.SellPrice,
            Quantity = row.Quantity,
            ProfitPerJump = row.ProfitPerJump,
            Profit = row.Profit,
            Margin = row.Margin,
            UnitVolume = row.UnitVolume,
            TotalVolume = row.TotalVolume
        };
    }

    private static IReadOnlyList<string> LoadSystemNames()
    {
        var cachePath = Path.Combine(GetCacheDirectory(), "universe.json");

        if (!File.Exists(cachePath))
        {
            return ["Amarr", "Dodixie", "Hek", "Jita", "Rens"];
        }

        try
        {
            using var stream = File.OpenRead(cachePath);
            var universe = JsonSerializer.Deserialize<UniverseData>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return universe?.Systems?
                .Select(system => system.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? ["Amarr", "Dodixie", "Hek", "Jita", "Rens"];
        }
        catch
        {
            return ["Amarr", "Dodixie", "Hek", "Jita", "Rens"];
        }
    }

    private static string GetCacheDirectory()
    {
        return Path.Combine("C:", "Projects", "EveParser", "EveParser", "cache");
    }

    private static string GetLastSearchPath()
    {
        return Path.Combine(GetCacheDirectory(), LastSearchFileName);
    }

    private static string GetSearchErrorText(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
        {
            return "Ошибка расчета: ESI временно ограничил запросы (429 Too Many Requests). Подождите минуту и повторите поиск; часть маршрутов уже сохранена в кэш.";
        }

        return $"Ошибка расчета: {ex.Message}";
    }
}

public sealed record TableSortState(string SortMemberPath, bool Descending);

public sealed record GuiSearchState(
    string SystemName,
    decimal Budget,
    double CargoVolume,
    bool SafeRoutes,
    bool IncludeContraband,
    int AccountingLevel,
    double MinimumMargin,
    decimal MinimumProfit,
    string SortMemberPath,
    bool SortDescending,
    DateTimeOffset SavedAt,
    List<TradeOpportunityRow> Opportunities);
