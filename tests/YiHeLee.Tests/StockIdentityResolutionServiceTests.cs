using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class StockIdentityResolutionServiceTests
{
    [Fact]
    public async Task 代碼0050已在官方主檔時直接解析且不補零()
    {
        var repository = new FakeMarketDataRepository(new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase)
        {
            ["0050"] = MarketType.Listed
        });
        var service = new StockIdentityResolutionService(repository);

        var result = await service.ResolveAsync(["0050"], CancellationToken.None);

        var resolution = result["0050"];
        Assert.True(resolution.IsRecognized);
        Assert.Equal("0050", resolution.ResolvedCode);
        Assert.Equal(MarketType.Listed, resolution.MarketType);
    }

    [Fact]
    public async Task 代碼50經官方主檔確認0050存在時解析為0050()
    {
        // Excel 把 0050 讀成數字 50，前導零遺失；官方主檔已有 0050，應解析為 0050，不得盲目補零。
        var repository = new FakeMarketDataRepository(new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase)
        {
            ["0050"] = MarketType.Listed
        });
        var service = new StockIdentityResolutionService(repository);

        var result = await service.ResolveAsync(["50"], CancellationToken.None);

        var resolution = result["50"];
        Assert.True(resolution.IsRecognized);
        Assert.Equal("0050", resolution.ResolvedCode);
        Assert.Equal(MarketType.Listed, resolution.MarketType);
    }

    [Fact]
    public async Task 代碼50官方主檔查無對應補零候選時不得盲目補零()
    {
        // 官方主檔完全沒有 0050 或 00050，不得假設一定是 0050；必須標示為無法識別。
        var repository = new FakeMarketDataRepository(new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase));
        var service = new StockIdentityResolutionService(repository);

        var result = await service.ResolveAsync(["50"], CancellationToken.None);

        var resolution = result["50"];
        Assert.False(resolution.IsRecognized);
        Assert.Equal("50", resolution.ResolvedCode);
    }

    [Fact]
    public async Task 八位數金額不得被視為股票代碼()
    {
        var repository = new FakeMarketDataRepository(new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase));
        var service = new StockIdentityResolutionService(repository);

        var result = await service.ResolveAsync(["10037677"], CancellationToken.None);

        var resolution = result["10037677"];
        Assert.False(resolution.IsRecognized);
        Assert.False(resolution.Identity.IsFormatValid);
    }

    [Fact]
    public async Task 代碼00982A已在官方主檔時保留原樣()
    {
        var repository = new FakeMarketDataRepository(new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase)
        {
            ["00982A"] = MarketType.Listed
        });
        var service = new StockIdentityResolutionService(repository);

        var result = await service.ResolveAsync(["00982A"], CancellationToken.None);

        var resolution = result["00982A"];
        Assert.True(resolution.IsRecognized);
        Assert.Equal("00982A", resolution.ResolvedCode);
    }

    private sealed class FakeMarketDataRepository : IMarketDataRepository
    {
        private readonly IReadOnlyDictionary<string, MarketType> _marketTypes;

        public FakeMarketDataRepository(IReadOnlyDictionary<string, MarketType> marketTypes) => _marketTypes = marketTypes;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> BeginPriceBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, MarketType marketType, DateTimeOffset startedAt, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CompletePriceBatchAsync(OfficialPriceBatchSummary summary, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<(int Inserted, int Updated)> UpsertDailyPricesAsync(IReadOnlyList<OfficialStockPrice> prices, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<(DateOnly TradeDate, decimal ClosePrice)>> GetRecentClosePricesAsync(string stockCode, DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MarketType?> GetStockMarketTypeAsync(string stockCode, CancellationToken cancellationToken)
            => Task.FromResult(_marketTypes.TryGetValue(stockCode, out var mt) ? mt : (MarketType?)null);

        public Task<IReadOnlyDictionary<string, MarketType>> GetStockMarketTypesAsync(IReadOnlyCollection<string> stockCodes, CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, MarketType>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in stockCodes)
            {
                if (_marketTypes.TryGetValue(code, out var mt))
                {
                    result[code] = mt;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, MarketType>>(result);
        }

        public Task SaveMovingAverageResultsAsync(DateOnly tradeDate, IReadOnlyList<MovingAverageResult> results, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MovingAverageResult>> GetMovingAverageResultsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, MarketType marketType, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<int> GetDistinctTradeDateCountAsync(DateOnly upToDate, int maxTradingDays, string stockCode, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> HasDailyPricesAsync(DateOnly tradeDate, MarketType marketType, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> HasDailyPriceAsync(DateOnly tradeDate, string stockCode, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> HasSucceededBatchAsync(OfficialPriceJobType jobType, DateOnly targetDate, string sourceProvider, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<StockDailyPriceQueryResult> QueryDailyPricesAsync(StockDailyPriceQueryFilter filter, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<DateOnly?> GetLatestTradeDateAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
