using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class MovingAverageServiceTests
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);

    [Fact]
    public async Task 資料充足時MA5_MA20_MA60_MA120均可人工驗算()
    {
        // 收盤價 = 100 + i（i=0 為 targetDate 當日），可完全人工驗算平均值。
        var history = BuildHistory(days: 130);
        var repository = new FakeMarketDataRepository(history);
        var service = new MovingAverageService(repository);

        var result = await service.CalculateAsync("5285", TradeDate, CancellationToken.None);

        Assert.Equal(CalculationStatus.Ok, result.CalculationStatus);
        // 計算最多只需要 120 個有效交易日（MA120 為最長視窗），超過的歷史資料不需要一併取回。
        Assert.Equal(120, result.AvailableTradingDayCount);
        Assert.Equal(100m, result.ClosePrice);
        Assert.Equal(102m, result.MovingAverage5);     // (100+101+102+103+104)/5
        Assert.Equal(109.5m, result.MovingAverage20);   // 平均 100..119 對應 i=0..19
        Assert.Equal(129.5m, result.MovingAverage60);
        Assert.Equal(159.5m, result.MovingAverage120);
    }

    [Fact]
    public async Task 恰好120個有效交易日時MA120可計算()
    {
        var history = BuildHistory(days: 120);
        var repository = new FakeMarketDataRepository(history);
        var service = new MovingAverageService(repository);

        var result = await service.CalculateAsync("5285", TradeDate, CancellationToken.None);

        Assert.Equal(CalculationStatus.Ok, result.CalculationStatus);
        Assert.NotNull(result.MovingAverage120);
    }

    [Fact]
    public async Task 只有119個有效交易日時MA120為null且狀態為交易日數不足()
    {
        var history = BuildHistory(days: 119);
        var repository = new FakeMarketDataRepository(history);
        var service = new MovingAverageService(repository);

        var result = await service.CalculateAsync("5285", TradeDate, CancellationToken.None);

        Assert.Null(result.MovingAverage120);
        Assert.Equal(CalculationStatus.InsufficientHistory, result.CalculationStatus);
        // MA5／MA20／MA60 資料足夠，不得因 MA120 不足就一併不計算。
        Assert.NotNull(result.MovingAverage5);
        Assert.NotNull(result.MovingAverage20);
        Assert.NotNull(result.MovingAverage60);
    }

    [Fact]
    public async Task 只有45個有效交易日時MA60與MA120皆為null但MA5MA20仍正常()
    {
        var history = BuildHistory(days: 45);
        var repository = new FakeMarketDataRepository(history);
        var service = new MovingAverageService(repository);

        var result = await service.CalculateAsync("5285", TradeDate, CancellationToken.None);

        Assert.Equal(45, result.AvailableTradingDayCount);
        Assert.NotNull(result.MovingAverage5);
        Assert.NotNull(result.MovingAverage20);
        Assert.Null(result.MovingAverage60);
        Assert.Null(result.MovingAverage120);
        Assert.Equal(CalculationStatus.InsufficientHistory, result.CalculationStatus);
    }

    [Fact]
    public async Task 當日尚無官方收盤價時全部為null且交易日數為0()
    {
        // 最新一筆資料日期早於 targetDate，代表官方尚未提供當日收盤價，不得使用昨日資料補值。
        var history = BuildHistory(days: 130).Select(x => (x.TradeDate.AddDays(-1), x.ClosePrice)).ToArray();
        var repository = new FakeMarketDataRepository(history);
        var service = new MovingAverageService(repository);

        var result = await service.CalculateAsync("5285", TradeDate, CancellationToken.None);

        Assert.Equal(0, result.AvailableTradingDayCount);
        Assert.Null(result.ClosePrice);
        Assert.Null(result.MovingAverage5);
        Assert.Equal(CalculationStatus.InsufficientHistory, result.CalculationStatus);
    }

    private static (DateOnly TradeDate, decimal ClosePrice)[] BuildHistory(int days)
        => Enumerable.Range(0, days).Select(i => (TradeDate.AddDays(-i), 100m + i)).ToArray();

    private sealed class FakeMarketDataRepository : IMarketDataRepository
    {
        private readonly IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)> _history;

        public FakeMarketDataRepository(IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)> history)
        {
            _history = history;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> BeginPriceBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, MarketType marketType, DateTimeOffset startedAt, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CompletePriceBatchAsync(OfficialPriceBatchSummary summary, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<(int Inserted, int Updated)> UpsertDailyPricesAsync(IReadOnlyList<OfficialStockPrice> prices, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)>> GetRecentClosePricesAsync(string stockCode, DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)>>(
                _history.Where(x => x.TradeDate <= upToDate).OrderByDescending(x => x.TradeDate).Take(maxTradingDays).ToArray());

        public Task<MarketType?> GetStockMarketTypeAsync(string stockCode, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, MarketType>> GetStockMarketTypesAsync(IReadOnlyCollection<string> stockCodes, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task SaveMovingAverageResultsAsync(DateOnly tradeDate, IReadOnlyList<MovingAverageResult> results, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MovingAverageResult>> GetMovingAverageResultsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> HasSucceededBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
