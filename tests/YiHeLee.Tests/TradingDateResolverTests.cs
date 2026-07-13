using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

/// <summary>
/// 驗證上一交易日解析（2026-07-13 盤中／收盤流程拆分）：
/// 禁止 today-1（星期一的上一交易日是上星期五；國定假日後為假期前最後交易日）；
/// 上一交易日均價快照不存在、不完整或收盤更新失敗時，禁止退回更舊的均價資料，
/// 必須回報「基準均價資料尚未就緒」。
/// </summary>
public sealed class TradingDateResolverTests
{
    [Fact]
    public async Task 星期二盤中使用星期一均價()
    {
        // 2026-07-14 為星期二；上一交易日為 2026-07-13 星期一。
        var repo = new FakeRepo
        {
            LatestPriceDate = new DateOnly(2026, 7, 13),
            LatestMovingAverageDate = new DateOnly(2026, 7, 13),
            LatestAttemptDate = new DateOnly(2026, 7, 13)
        };
        var resolver = new TradingDateResolver(repo);

        var result = await resolver.ResolveBaselineAsync(new DateOnly(2026, 7, 14), CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal(new DateOnly(2026, 7, 13), result.BaselineTradeDate);
        Assert.Equal(new DateOnly(2026, 7, 14), result.EvaluationDate);
    }

    [Fact]
    public async Task 星期一盤中使用上星期五均價_不得使用today減一()
    {
        // 2026-07-13 為星期一；today-1 是星期日（非交易日），真正上一交易日為 2026-07-10 星期五。
        var repo = new FakeRepo
        {
            LatestPriceDate = new DateOnly(2026, 7, 10),
            LatestMovingAverageDate = new DateOnly(2026, 7, 10),
            LatestAttemptDate = new DateOnly(2026, 7, 10)
        };
        var resolver = new TradingDateResolver(repo);

        var result = await resolver.ResolveBaselineAsync(new DateOnly(2026, 7, 13), CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal(new DateOnly(2026, 7, 10), result.BaselineTradeDate);
        Assert.NotEqual(new DateOnly(2026, 7, 12), result.BaselineTradeDate); // 不是 today-1（星期日）。
    }

    [Fact]
    public async Task 國定假日後第一個交易日使用真正上一交易日均價()
    {
        // 假設 2026-07-09（四）、2026-07-10（五）為連續假日休市：
        // 2026-07-13（一）盤中應使用 2026-07-08（三）的均價，即使中間相隔多個日曆日。
        var repo = new FakeRepo
        {
            LatestPriceDate = new DateOnly(2026, 7, 8),
            LatestMovingAverageDate = new DateOnly(2026, 7, 8),
            LatestAttemptDate = new DateOnly(2026, 7, 8),
            HolidayDates = { new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10) }
        };
        var resolver = new TradingDateResolver(repo);

        var result = await resolver.ResolveBaselineAsync(new DateOnly(2026, 7, 13), CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal(new DateOnly(2026, 7, 8), result.BaselineTradeDate);
    }

    [Fact]
    public async Task 上一交易日均價快照不存在時_禁止退回更舊快照_標記基準未就緒()
    {
        // 收盤價已保存到 2026-07-13，但均價快照最新只有 2026-07-10：
        // 禁止退回 07-10 的舊均價冒充上一交易日，必須標記未就緒。
        var repo = new FakeRepo
        {
            LatestPriceDate = new DateOnly(2026, 7, 13),
            LatestMovingAverageDate = new DateOnly(2026, 7, 10),
            LatestAttemptDate = new DateOnly(2026, 7, 13)
        };
        var resolver = new TradingDateResolver(repo);

        var result = await resolver.ResolveBaselineAsync(new DateOnly(2026, 7, 14), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Null(result.BaselineTradeDate);
        Assert.Contains("2026-07-13", result.NotReadyReason, StringComparison.Ordinal);
        Assert.Contains("禁止退回更舊的均價快照", result.NotReadyReason, StringComparison.Ordinal);
        Assert.Contains("請先執行收盤更新或歷史回補", result.NotReadyReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 上一交易日收盤更新失敗時_禁止用兩個交易日前的資料冒充_標記基準未就緒()
    {
        // 2026-07-13 曾嘗試收盤更新（非休市批次）但沒有留下任何收盤價：
        // 最新收盤價停在 2026-07-10，禁止把 07-10 當成上一交易日基準。
        var repo = new FakeRepo
        {
            LatestPriceDate = new DateOnly(2026, 7, 10),
            LatestMovingAverageDate = new DateOnly(2026, 7, 10),
            LatestAttemptDate = new DateOnly(2026, 7, 13)
        };
        var resolver = new TradingDateResolver(repo);

        var result = await resolver.ResolveBaselineAsync(new DateOnly(2026, 7, 14), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Null(result.BaselineTradeDate);
        Assert.Contains("2026-07-13", result.NotReadyReason, StringComparison.Ordinal);
        Assert.Contains("收盤更新尚未成功", result.NotReadyReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 資料庫完全沒有官方收盤價時_標記基準未就緒()
    {
        var resolver = new TradingDateResolver(new FakeRepo());

        var result = await resolver.ResolveBaselineAsync(new DateOnly(2026, 7, 14), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Null(result.BaselineTradeDate);
        Assert.Contains("尚無", result.NotReadyReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 今日收盤產生的新均價不會被同日盤中使用_基準嚴格早於判斷日()
    {
        // 即使今日（2026-07-14）收盤更新已提前完成，盤中基準仍必須是上一交易日 2026-07-13：
        // FakeRepo 依「日期 < 判斷日」過濾，模擬 SQL 的 TradeDate < $beforeDate 條件。
        var repo = new FakeRepo
        {
            LatestPriceDate = new DateOnly(2026, 7, 13),
            LatestMovingAverageDate = new DateOnly(2026, 7, 13),
            LatestAttemptDate = new DateOnly(2026, 7, 13)
        };
        var resolver = new TradingDateResolver(repo);

        var result = await resolver.ResolveBaselineAsync(new DateOnly(2026, 7, 14), CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.True(result.BaselineTradeDate < result.EvaluationDate,
            "BaselineTradeDate 必須嚴格早於 EvaluationDate，今日新均價下一交易日才可使用。");
    }

    [Theory]
    [InlineData("2026-07-11")] // 星期六
    [InlineData("2026-07-12")] // 星期日
    public async Task 週六週日為已知非交易日(string date)
    {
        var resolver = new TradingDateResolver(new FakeRepo());
        Assert.True(await resolver.IsKnownNonTradingDayAsync(DateOnly.Parse(date), CancellationToken.None));
    }

    [Fact]
    public async Task 平日預設為候選交易日_官方批次記錄休市時視為非交易日()
    {
        var repo = new FakeRepo { HolidayDates = { new DateOnly(2026, 7, 9) } };
        var resolver = new TradingDateResolver(repo);

        Assert.True(await resolver.IsKnownNonTradingDayAsync(new DateOnly(2026, 7, 9), CancellationToken.None));
        Assert.False(await resolver.IsKnownNonTradingDayAsync(new DateOnly(2026, 7, 14), CancellationToken.None));
    }

    /// <summary>只實作 TradingDateResolver 需要的查詢；日期過濾模擬 SQL 的 &lt; $beforeDate 條件。</summary>
    private sealed class FakeRepo : IMarketDataRepository
    {
        public DateOnly? LatestPriceDate { get; init; }
        public DateOnly? LatestMovingAverageDate { get; init; }
        public DateOnly? LatestAttemptDate { get; init; }
        public HashSet<DateOnly> HolidayDates { get; } = [];

        public Task<DateOnly?> GetLatestPriceTradeDateBeforeAsync(DateOnly beforeDate, CancellationToken cancellationToken)
            => Task.FromResult(LatestPriceDate is DateOnly d && d < beforeDate ? LatestPriceDate : null);

        public Task<DateOnly?> GetLatestMovingAverageTradeDateBeforeAsync(DateOnly beforeDate, CancellationToken cancellationToken)
            => Task.FromResult(LatestMovingAverageDate is DateOnly d && d < beforeDate ? LatestMovingAverageDate : null);

        public Task<DateOnly?> GetLatestDailyCloseAttemptDateBeforeAsync(DateOnly beforeDate, CancellationToken cancellationToken)
            => Task.FromResult(LatestAttemptDate is DateOnly d && d < beforeDate ? LatestAttemptDate : null);

        public Task<bool> HasHolidayBatchAsync(DateOnly targetDate, CancellationToken cancellationToken)
            => Task.FromResult(HolidayDates.Contains(targetDate));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> BeginPriceBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, MarketType marketType, DateTimeOffset startedAt, CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task CompletePriceBatchAsync(OfficialPriceBatchSummary summary, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(int Inserted, int Updated)> UpsertDailyPricesAsync(IReadOnlyList<OfficialStockPrice> prices, CancellationToken cancellationToken)
            => Task.FromResult((0, 0));

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
            => Task.FromResult(0);

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, MarketType marketType, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(0);

        public Task<bool> HasDailyPricesAsync(DateOnly tradeDate, MarketType marketType, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> HasDailyPriceAsync(DateOnly tradeDate, string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> HasSucceededBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<bool> HasResolvedHolidayBatchAsync(DateOnly targetDate, string sourceProvider, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<StockDailyPriceQueryResult> QueryDailyPricesAsync(StockDailyPriceQueryFilter filter, CancellationToken cancellationToken)
            => Task.FromResult(new StockDailyPriceQueryResult([], 0, filter.Page, filter.PageSize));

        public Task<DateOnly?> GetLatestTradeDateAsync(CancellationToken cancellationToken) => Task.FromResult<DateOnly?>(null);

        public Task<IReadOnlySet<string>> GetConfirmedNoEmergingDataCodesAsync(DateOnly tradeDate, IReadOnlyCollection<string> stockCodes, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task RecordConfirmedNoEmergingDataAsync(DateOnly tradeDate, IReadOnlyCollection<string> stockCodes, DateTimeOffset checkedAt, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
