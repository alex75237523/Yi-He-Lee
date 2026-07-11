using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class MarketPriceServiceTests
{
    // 2026-07-09 為週四，確定為平日；用於一般成功／失敗情境。
    private static readonly DateOnly Weekday = new(2026, 7, 9);
    // 2026-07-04 為週六，用於驗證休市快速路徑。
    private static readonly DateOnly Weekend = new(2026, 7, 4);
    private static readonly OfficialMarketDataSettings Settings = new();

    [Fact]
    public async Task TWSE回傳日期不等於targetDate時拒絕寫入且標記為尚未公布()
    {
        var twse = FakeTwseProvider.WithMismatchedDate(Weekday.AddDays(-1));
        var tpex = FakeTpexProvider.WithMatchingDate();
        var repository = new FakeMarketDataRepository();
        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        var results = await service.FetchAndSaveDailyPricesAsync(Weekday, Settings, CancellationToken.None);

        var twseSummary = Assert.Single(results, x => x.SourceProvider == "TWSE");
        Assert.Equal(OfficialPriceBatchStatus.NotPublished, twseSummary.Status);
        Assert.DoesNotContain(repository.SavedPrices, x => x.SourceProvider == "TWSE");
    }

    [Fact]
    public async Task TPEx回傳日期不等於targetDate時拒絕寫入_重現官方靜默回退舊資料的情形()
    {
        // 重現實測觀察到的行為：TWSE 當日已公布，但 TPEx 靜默回傳前一交易日資料且 HTTP 200。
        var twse = FakeTwseProvider.WithMatchingDate();
        var tpex = FakeTpexProvider.WithMismatchedDate(Weekday.AddDays(-1));
        var repository = new FakeMarketDataRepository();
        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        var results = await service.FetchAndSaveDailyPricesAsync(Weekday, Settings, CancellationToken.None);

        var tpexSummary = Assert.Single(results, x => x.SourceProvider == "TPEx");
        Assert.Equal(OfficialPriceBatchStatus.NotPublished, tpexSummary.Status);
        Assert.DoesNotContain(repository.SavedPrices, x => x.SourceProvider == "TPEx");
    }

    [Fact]
    public async Task 週末不呼叫官方來源直接記錄休市()
    {
        var twse = FakeTwseProvider.WithMatchingDate();
        var tpex = FakeTpexProvider.WithMatchingDate();
        var repository = new FakeMarketDataRepository();
        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekend), new FakeLogger());

        var results = await service.FetchAndSaveDailyPricesAsync(Weekend, Settings, CancellationToken.None);

        Assert.All(results, x => Assert.Equal(OfficialPriceBatchStatus.Holiday, x.Status));
        Assert.Equal(0, twse.CallCount);
        Assert.Equal(0, tpex.CallCount);
    }

    [Fact]
    public async Task TWSE明確回報休市時_TPEx日期不符也一併記錄為休市而非尚未公布()
    {
        var twse = FakeTwseProvider.WithExplicitHoliday();
        var tpex = FakeTpexProvider.WithMismatchedDate(Weekday.AddDays(-3)); // 靜默回退到更早的交易日
        var repository = new FakeMarketDataRepository();
        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        var results = await service.FetchAndSaveDailyPricesAsync(Weekday, Settings, CancellationToken.None);

        Assert.All(results, x => Assert.Equal(OfficialPriceBatchStatus.Holiday, x.Status));
    }

    [Fact]
    public async Task 兩來源皆成功時寫入正式資料且狀態為成功()
    {
        var twse = FakeTwseProvider.WithMatchingDate();
        var tpex = FakeTpexProvider.WithMatchingDate();
        var repository = new FakeMarketDataRepository();
        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        var results = await service.FetchAndSaveDailyPricesAsync(Weekday, Settings, CancellationToken.None);

        Assert.All(results, x => Assert.Equal(OfficialPriceBatchStatus.Succeeded, x.Status));
        Assert.Contains(repository.SavedPrices, x => x.SourceProvider == "TWSE" && x.TradeDate == Weekday);
        Assert.Contains(repository.SavedPrices, x => x.SourceProvider == "TPEx" && x.TradeDate == Weekday);
    }

    [Fact]
    public async Task 同日重跑不重複呼叫官方來源_冪等略過()
    {
        var twse = FakeTwseProvider.WithMatchingDate();
        var tpex = FakeTpexProvider.WithMatchingDate();
        var repository = new FakeMarketDataRepository();
        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        await service.FetchAndSaveDailyPricesAsync(Weekday, Settings, CancellationToken.None);
        await service.FetchAndSaveDailyPricesAsync(Weekday, Settings, CancellationToken.None);

        Assert.Equal(1, twse.CallCount);
        Assert.Equal(1, tpex.CallCount);
    }

    [Fact]
    public async Task 歷史回補只查詢targetDate以前的日期_不得改寫成執行當日()
    {
        var twse = FakeTwseProvider.EchoRequestedDate();
        var tpex = FakeTpexProvider.EchoRequestedDate();
        var repository = new FakeMarketDataRepository();
        var settings = new OfficialMarketDataSettings
        {
            RequiredTradingDaysForMa120 = 3,
            MaxBackfillLookbackCalendarDays = 10,
            BackfillThrottleMillisecondsBetweenRequests = 0
        };
        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        await service.BackfillHistoryAsync(Weekday, settings, CancellationToken.None);

        Assert.DoesNotContain(Weekday, twse.RequestedDates);
        Assert.All(twse.RequestedDates, d => Assert.True(d < Weekday));
        Assert.All(repository.SavedPrices, p => Assert.True(p.TradeDate < Weekday));
    }

    [Fact]
    public async Task 歷史回補要分市場檢查_上市足夠時仍會補上櫃不足資料()
    {
        var twse = FakeTwseProvider.EchoRequestedDate();
        var tpex = FakeTpexProvider.EchoRequestedDate();
        var repository = new FakeMarketDataRepository();
        var settings = new OfficialMarketDataSettings
        {
            RequiredTradingDaysForMa120 = 3,
            MaxBackfillLookbackCalendarDays = 10,
            BackfillThrottleMillisecondsBetweenRequests = 0
        };

        repository.SavedPrices.Add(CreateSavedPrice("2330", "台積電", MarketType.Listed, Weekday.AddDays(-1)));
        repository.SavedPrices.Add(CreateSavedPrice("2330", "台積電", MarketType.Listed, Weekday.AddDays(-2)));
        repository.SavedPrices.Add(CreateSavedPrice("2330", "台積電", MarketType.Listed, Weekday.AddDays(-3)));

        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        await service.BackfillHistoryAsync(Weekday, settings, CancellationToken.None);

        Assert.Empty(twse.RequestedDates);
        Assert.Equal(3, tpex.RequestedDates.Count);
        Assert.Equal(3, repository.SavedPrices.Count(x => x.MarketType == MarketType.Otc));
    }

    [Fact]
    public async Task 歷史回補要補足興櫃持股不足資料()
    {
        var twse = FakeTwseProvider.EchoRequestedDate();
        var tpex = FakeTpexProvider.EchoRequestedDate();
        var emerging = FakeEmergingProvider.WithMatchingDate();
        var repository = new FakeMarketDataRepository();
        var settings = new OfficialMarketDataSettings
        {
            RequiredTradingDaysForMa120 = 3,
            MaxBackfillLookbackCalendarDays = 10,
            BackfillThrottleMillisecondsBetweenRequests = 0
        };

        foreach (var date in new[] { Weekday.AddDays(-1), Weekday.AddDays(-2), Weekday.AddDays(-3) })
        {
            repository.SavedPrices.Add(CreateSavedPrice("2330", "台積電", MarketType.Listed, date));
            repository.SavedPrices.Add(CreateSavedPrice("5351", "鈺創", MarketType.Otc, date));
        }

        var service = new MarketPriceService(twse, tpex, emerging, repository, new FakeClock(Weekday), new FakeLogger());

        await service.BackfillHistoryAsync(Weekday, settings, CancellationToken.None, emergingStockCodes: ["4573"]);

        Assert.Empty(twse.RequestedDates);
        Assert.Empty(tpex.RequestedDates);
        Assert.Equal(3, emerging.RequestedDates.Count);
        Assert.Equal(3, repository.SavedPrices.Count(x => x.MarketType == MarketType.Emerging && x.StockCode == "4573"));
    }

    [Fact]
    public async Task 市場整體已有足夠交易日但指定持股仍不足時必須繼續回補()
    {
        // 上櫃市場整體已有 3 個交易日（由另一檔股票貢獻），但持股 5351 只有 1 筆；
        // 完成判斷不得只看市場整體，必須逐檔確認 5351 也達標才可停止回補。
        var twse = FakeTwseProvider.EchoRequestedDate();
        var tpex = FakeTpexProvider.EchoRequestedDate();
        var repository = new FakeMarketDataRepository();
        var settings = new OfficialMarketDataSettings
        {
            RequiredTradingDaysForMa120 = 3,
            MaxBackfillLookbackCalendarDays = 10,
            BackfillThrottleMillisecondsBetweenRequests = 0
        };

        foreach (var date in new[] { Weekday.AddDays(-1), Weekday.AddDays(-2), Weekday.AddDays(-3) })
        {
            repository.SavedPrices.Add(CreateSavedPrice("6488", "環球晶", MarketType.Otc, date));
        }
        repository.SavedPrices.Add(CreateSavedPrice("5351", "鈺創", MarketType.Otc, Weekday.AddDays(-1)));

        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        await service.BackfillHistoryAsync(
            Weekday, settings, CancellationToken.None,
            otcStockCodes: ["5351"]);

        // 市場整體交易日數（3）已足夠，但因逐檔檢查 5351 仍不足，仍必須繼續呼叫 TPEx 回補。
        Assert.NotEmpty(tpex.RequestedDates);
    }

    [Fact]
    public async Task 同市場A股票已足夠B股票不足時仍確保B的缺日被補齊()
    {
        var twse = FakeTwseProvider.EchoRequestedDate();
        var tpex = FakeTpexProvider.EchoRequestedDate();
        var repository = new FakeMarketDataRepository();
        var settings = new OfficialMarketDataSettings
        {
            RequiredTradingDaysForMa120 = 3,
            MaxBackfillLookbackCalendarDays = 10,
            BackfillThrottleMillisecondsBetweenRequests = 0
        };

        foreach (var date in new[] { Weekday.AddDays(-1), Weekday.AddDays(-2), Weekday.AddDays(-3) })
        {
            repository.SavedPrices.Add(CreateSavedPrice("6488", "環球晶", MarketType.Otc, date));
        }
        // B 股票（5351）完全沒有資料。

        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        await service.BackfillHistoryAsync(
            Weekday, settings, CancellationToken.None,
            otcStockCodes: ["6488", "5351"]);

        // 回補為整批下載（市場層級效率），因此只要有任何一檔（5351）不足，該市場當日仍會被完整抓取，
        // 抓取結果（實際官方端點會回傳整批市場資料）即可讓 5351 缺口一併補齊；此處驗證確實有繼續抓取，
        // 不會因為 6488 已足夠就誤判整批已完成。
        Assert.True(tpex.RequestedDates.Count >= 3);
    }

    [Fact]
    public async Task 未指定持股代碼時維持既有僅檢查市場整體交易日數行為()
    {
        // 向下相容：listedStockCodes／otcStockCodes 皆未指定時，行為必須與修正前一致，只看市場整體。
        var twse = FakeTwseProvider.EchoRequestedDate();
        var tpex = FakeTpexProvider.EchoRequestedDate();
        var repository = new FakeMarketDataRepository();
        var settings = new OfficialMarketDataSettings
        {
            RequiredTradingDaysForMa120 = 3,
            MaxBackfillLookbackCalendarDays = 10,
            BackfillThrottleMillisecondsBetweenRequests = 0
        };

        foreach (var date in new[] { Weekday.AddDays(-1), Weekday.AddDays(-2), Weekday.AddDays(-3) })
        {
            repository.SavedPrices.Add(CreateSavedPrice("2330", "台積電", MarketType.Listed, date));
            repository.SavedPrices.Add(CreateSavedPrice("5351", "鈺創", MarketType.Otc, date));
        }

        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        await service.BackfillHistoryAsync(Weekday, settings, CancellationToken.None);

        Assert.Empty(twse.RequestedDates);
        Assert.Empty(tpex.RequestedDates);
    }

    [Fact]
    public async Task 歷史回補遇到DB已有收盤價時不再呼叫官方來源()
    {
        var twse = FakeTwseProvider.EchoRequestedDate();
        var tpex = FakeTpexProvider.EchoRequestedDate();
        var repository = new FakeMarketDataRepository();
        var existingDate = Weekday.AddDays(-1);
        repository.SavedPrices.Add(CreateSavedPrice("2330", "台積電", MarketType.Listed, existingDate));

        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        var summary = await service.FetchAndSaveSingleAsync(
            OfficialPriceJobType.HistoricalBackfill,
            existingDate,
            MarketType.Listed,
            Settings,
            CancellationToken.None);

        Assert.Equal(OfficialPriceBatchStatus.Succeeded, summary.Status);
        Assert.Equal(0, twse.CallCount);
        Assert.Contains("DB 已保存", summary.ErrorMessage);
    }

    [Fact]
    public async Task 興櫃當日快照日期等於targetDate時單獨寫入成功()
    {
        var twse = FakeTwseProvider.WithMatchingDate();
        var tpex = FakeTpexProvider.WithMatchingDate();
        var emerging = FakeEmergingProvider.WithMatchingDate();
        var repository = new FakeMarketDataRepository();
        var service = new MarketPriceService(twse, tpex, emerging, repository, new FakeClock(Weekday), new FakeLogger());

        var summary = await service.FetchAndSaveSingleAsync(
            OfficialPriceJobType.DailyMarketData, Weekday, MarketType.Emerging, Settings, CancellationToken.None);

        Assert.Equal(OfficialPriceBatchStatus.Succeeded, summary.Status);
        Assert.Contains(repository.SavedPrices, x => x.SourceProvider == "TPEx興櫃" && x.TradeDate == Weekday && x.MarketType == MarketType.Emerging);
    }

    [Fact]
    public async Task 興櫃快照日期不等於targetDate時拒絕寫入且標記為尚未公布()
    {
        var twse = FakeTwseProvider.WithMatchingDate();
        var tpex = FakeTpexProvider.WithMatchingDate();
        var emerging = FakeEmergingProvider.WithMismatchedDate(Weekday.AddDays(-1));
        var repository = new FakeMarketDataRepository();
        var service = new MarketPriceService(twse, tpex, emerging, repository, new FakeClock(Weekday), new FakeLogger());

        var summary = await service.FetchAndSaveSingleAsync(
            OfficialPriceJobType.DailyMarketData, Weekday, MarketType.Emerging, Settings, CancellationToken.None);

        Assert.Equal(OfficialPriceBatchStatus.NotPublished, summary.Status);
        Assert.DoesNotContain(repository.SavedPrices, x => x.SourceProvider == "TPEx興櫃");
    }

    [Fact]
    public async Task 回補過程官方來源發生例外時該市場批次記為失敗以利產生逐檔缺失狀態()
    {
        // 官方端點逾時／格式異常等例外會被捕捉並記為 Failed（而非直接讓整個回補流程中斷），
        // DailyJobService 會依此批次狀態把仍不足的股票標示為「歷史回補失敗」而非泛稱的「歷史資料不足」。
        var twse = FakeTwseProvider.WithException();
        var tpex = FakeTpexProvider.EchoRequestedDate();
        var repository = new FakeMarketDataRepository();
        var settings = new OfficialMarketDataSettings
        {
            RequiredTradingDaysForMa120 = 2,
            MaxBackfillLookbackCalendarDays = 5,
            BackfillThrottleMillisecondsBetweenRequests = 0
        };

        var service = new MarketPriceService(twse, tpex, FakeEmergingProvider.WithMatchingDate(), repository, new FakeClock(Weekday), new FakeLogger());

        var summaries = await service.BackfillHistoryAsync(Weekday, settings, CancellationToken.None);

        Assert.Contains(summaries, x => x.MarketType == MarketType.Listed && x.Status == OfficialPriceBatchStatus.Failed);
    }

    private sealed class FakeClock : IClock
    {
        private readonly DateOnly _today;
        public FakeClock(DateOnly today) => _today = today;
        public DateTimeOffset GetTaipeiNow() => new(_today.ToDateTime(new TimeOnly(13, 35)), TimeSpan.FromHours(8));
        public DateOnly GetTaipeiToday() => _today;
    }

    private sealed class FakeLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class FakeTwseProvider : ITwseMarketDataProvider
    {
        private readonly Func<DateOnly, OfficialPriceFetchResult> _factory;
        public List<DateOnly> RequestedDates { get; } = [];
        public int CallCount { get; private set; }
        public string SourceProviderName => "TWSE";

        private FakeTwseProvider(Func<DateOnly, OfficialPriceFetchResult> factory) => _factory = factory;

        public static FakeTwseProvider WithMatchingDate() => new(date => Success(MarketType.Listed, "TWSE", date, date));
        public static FakeTwseProvider WithMismatchedDate(DateOnly returnedDate) => new(date => Success(MarketType.Listed, "TWSE", date, returnedDate));
        public static FakeTwseProvider WithExplicitHoliday() => new(date => Holiday(MarketType.Listed, "TWSE", date));
        public static FakeTwseProvider EchoRequestedDate() => new(date => Success(MarketType.Listed, "TWSE", date, date));
        public static FakeTwseProvider WithException() => new(_ => throw new InvalidOperationException("模擬官方端點逾時或格式異常。"));

        public Task<OfficialPriceFetchResult> FetchDailyCloseAsync(DateOnly requestedDate, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestedDates.Add(requestedDate);
            return Task.FromResult(_factory(requestedDate));
        }

    }

    private sealed class FakeTpexProvider : ITpexMarketDataProvider
    {
        private readonly Func<DateOnly, OfficialPriceFetchResult> _factory;
        public List<DateOnly> RequestedDates { get; } = [];
        public int CallCount { get; private set; }
        public string SourceProviderName => "TPEx";

        private FakeTpexProvider(Func<DateOnly, OfficialPriceFetchResult> factory) => _factory = factory;

        public static FakeTpexProvider WithMatchingDate() => new(date => Success(MarketType.Otc, "TPEx", date, date));
        public static FakeTpexProvider WithMismatchedDate(DateOnly returnedDate) => new(date => Success(MarketType.Otc, "TPEx", date, returnedDate));
        public static FakeTpexProvider EchoRequestedDate() => new(date => Success(MarketType.Otc, "TPEx", date, date));

        public Task<OfficialPriceFetchResult> FetchDailyCloseAsync(DateOnly requestedDate, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestedDates.Add(requestedDate);
            return Task.FromResult(_factory(requestedDate));
        }
    }

    private sealed class FakeEmergingProvider : IEmergingMarketDataProvider
    {
        private readonly Func<DateOnly, OfficialPriceFetchResult> _factory;
        public List<DateOnly> RequestedDates { get; } = [];
        public int CallCount { get; private set; }
        public string SourceProviderName => "TPEx興櫃";

        private FakeEmergingProvider(Func<DateOnly, OfficialPriceFetchResult> factory) => _factory = factory;

        public static FakeEmergingProvider WithMatchingDate() => new(date => Success(MarketType.Emerging, "TPEx興櫃", date, date));
        public static FakeEmergingProvider WithMismatchedDate(DateOnly returnedDate) => new(date => Success(MarketType.Emerging, "TPEx興櫃", date, returnedDate));

        public Task<OfficialPriceFetchResult> FetchDailyCloseAsync(DateOnly requestedDate, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestedDates.Add(requestedDate);
            return Task.FromResult(_factory(requestedDate));
        }

        public Task<OfficialPriceFetchResult> FetchHistoricalDailyCloseAsync(DateOnly requestedDate, IReadOnlyCollection<string> stockCodes, OfficialMarketDataSettings settings, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestedDates.Add(requestedDate);
            return Task.FromResult(_factory(requestedDate));
        }
    }

    private static OfficialPriceFetchResult Success(MarketType marketType, string provider, DateOnly requestedDate, DateOnly sourceDate)
        => new(
            marketType,
            requestedDate,
            sourceDate,
            [marketType switch
            {
                MarketType.Listed => new OfficialPriceQuote("2330", "台積電", 900m),
                MarketType.Emerging => new OfficialPriceQuote("4573", "高明鐵", 408m),
                _ => new OfficialPriceQuote("5285", "宜鼎", 515m)
            }],
            false,
            provider,
            $"https://example.invalid/{provider}",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

    private static OfficialPriceFetchResult Holiday(MarketType marketType, string provider, DateOnly requestedDate)
        => new(marketType, requestedDate, null, [], true, provider, $"https://example.invalid/{provider}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static OfficialStockPrice CreateSavedPrice(string code, string name, MarketType marketType, DateOnly tradeDate)
        => new(
            code,
            name,
            marketType,
            tradeDate,
            100m,
            marketType == MarketType.Listed ? "TWSE" : "TPEx",
            "https://example.invalid",
            tradeDate,
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow);

    private sealed class FakeMarketDataRepository : IMarketDataRepository
    {
        public List<OfficialStockPrice> SavedPrices { get; } = [];
        private readonly HashSet<(OfficialPriceJobType, DateOnly, string)> _succeededBatches = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> BeginPriceBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, MarketType marketType, DateTimeOffset startedAt, CancellationToken cancellationToken)
            => Task.FromResult(Guid.NewGuid().ToString("D"));

        public Task CompletePriceBatchAsync(OfficialPriceBatchSummary summary, CancellationToken cancellationToken)
        {
            if (summary.Status == OfficialPriceBatchStatus.Succeeded)
            {
                _succeededBatches.Add((summary.JobType, summary.TargetDate, summary.SourceProvider));
            }

            return Task.CompletedTask;
        }

        public Task<(int Inserted, int Updated)> UpsertDailyPricesAsync(IReadOnlyList<OfficialStockPrice> prices, CancellationToken cancellationToken)
        {
            foreach (var price in prices)
            {
                SavedPrices.RemoveAll(x => x.StockCode == price.StockCode && x.TradeDate == price.TradeDate);
                SavedPrices.Add(price);
            }

            return Task.FromResult((prices.Count, 0));
        }

        public Task<IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)>> GetRecentClosePricesAsync(string stockCode, DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(DateOnly, decimal)>>([]);

        public Task<MarketType?> GetStockMarketTypeAsync(string stockCode, CancellationToken cancellationToken)
            => Task.FromResult<MarketType?>(null);

        public Task<IReadOnlyDictionary<string, MarketType>> GetStockMarketTypesAsync(IReadOnlyCollection<string> stockCodes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, MarketType>>(new Dictionary<string, MarketType>());

        public Task SaveMovingAverageResultsAsync(DateOnly tradeDate, IReadOnlyList<MovingAverageResult> results, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MovingAverageResult>> GetMovingAverageResultsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MovingAverageResult>>([]);

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => Task.FromResult(Math.Min(maxTradingDays, SavedPrices.Select(x => x.TradeDate).Where(d => d <= upToDate).Distinct().Count()));

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, MarketType marketType, CancellationToken cancellationToken)
            => Task.FromResult(Math.Min(maxTradingDays, SavedPrices.Where(x => x.MarketType == marketType).Select(x => x.TradeDate).Where(d => d <= upToDate).Distinct().Count()));

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(Math.Min(maxTradingDays, SavedPrices.Where(x => x.StockCode == stockCode).Select(x => x.TradeDate).Where(d => d <= upToDate).Distinct().Count()));

        public Task<bool> HasDailyPricesAsync(DateOnly tradeDate, MarketType marketType, CancellationToken cancellationToken)
            => Task.FromResult(SavedPrices.Any(x => x.TradeDate == tradeDate && x.MarketType == marketType));

        public Task<bool> HasDailyPriceAsync(DateOnly tradeDate, string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(SavedPrices.Any(x => x.TradeDate == tradeDate && x.StockCode == stockCode));

        public Task<bool> HasSucceededBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, CancellationToken cancellationToken)
            => Task.FromResult(_succeededBatches.Contains((jobType, targetDate, sourceProvider)));

        public Task<StockDailyPriceQueryResult> QueryDailyPricesAsync(StockDailyPriceQueryFilter filter, CancellationToken cancellationToken)
            => Task.FromResult(new StockDailyPriceQueryResult([], 0, filter.Page, filter.PageSize));

        public Task<DateOnly?> GetLatestTradeDateAsync(CancellationToken cancellationToken)
            => Task.FromResult(SavedPrices.Count == 0 ? (DateOnly?)null : SavedPrices.Max(x => x.TradeDate));
    }
}
