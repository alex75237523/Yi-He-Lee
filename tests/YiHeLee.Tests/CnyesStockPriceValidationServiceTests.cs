using YiHeLee.Application.Abstractions;
using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class CnyesStockPriceValidationServiceTests
{
    private static readonly DateOnly TradeDate = new(2026, 7, 9);
    private static readonly Uri SourceUrl = new("https://www.cnyes.com/twstock/a_technical4.aspx");

    [Fact]
    public async Task 鉅亨頁面日期與目標交易日期相同時才比對()
    {
        var ma = Ma("2330", 100m, 100m, 100m, 100m);
        var service = CreateService(new Dictionary<string, MovingAverageResult> { ["2330"] = ma }, out _);
        var batch = CreateBatch(TradeDate, [Cnyes("2330", "台積電", 100m, 100m, 100m, 100m, 100m)]);

        var records = await service.ValidateAsync(TradeDate, new Dictionary<string, MarketType> { ["2330"] = MarketType.Listed }, [batch], CancellationToken.None);

        Assert.Contains(records, r => r.WindowDays == 5 && r.Outcome == CnyesValidationOutcome.Matched);
    }

    [Fact]
    public async Task 鉅亨頁面日期不同時拒絕比對並標記日期不符()
    {
        var service = CreateService(new Dictionary<string, MovingAverageResult>(), out _);
        var wrongDate = TradeDate.AddDays(-1);
        var batch = CreateBatch(wrongDate, [Cnyes("2330", "台積電", 100m, 100m, 100m, 100m, 100m)]);

        var records = await service.ValidateAsync(TradeDate, new Dictionary<string, MarketType> { ["2330"] = MarketType.Listed }, [batch], CancellationToken.None);

        Assert.All(records, r => Assert.Equal(CnyesValidationOutcome.SourceDateMismatch, r.Outcome));
        Assert.All(records, r => Assert.Null(r.CalculatedValue));
    }

    [Fact]
    public async Task 股票未出現在多頭空頭清單時標記不適用()
    {
        var ma = Ma("2330", 100m, 100m, 100m, 100m);
        var service = CreateService(new Dictionary<string, MovingAverageResult> { ["2330"] = ma }, out _);
        var batch = CreateBatch(TradeDate, [Cnyes("2454", "聯發科", 900m, 900m, 900m, 900m, 900m)]);

        var records = await service.ValidateAsync(TradeDate, new Dictionary<string, MarketType> { ["2330"] = MarketType.Listed }, [batch], CancellationToken.None);

        var record = Assert.Single(records, r => r.StockCode == "2330");
        Assert.Equal(CnyesValidationOutcome.NotApplicable, record.Outcome);
    }

    [Fact]
    public async Task 差異小於等於0點01視為相符()
    {
        var ma = Ma("2330", 100.00m, 100.00m, 100.00m, 100.00m);
        var service = CreateService(new Dictionary<string, MovingAverageResult> { ["2330"] = ma }, out _);
        var batch = CreateBatch(TradeDate, [Cnyes("2330", "台積電", 100.00m, 100.01m, 100.00m, 100.00m, 100.00m)]);

        var records = await service.ValidateAsync(TradeDate, new Dictionary<string, MarketType> { ["2330"] = MarketType.Listed }, [batch], CancellationToken.None);

        var ma5Record = records.Single(r => r.StockCode == "2330" && r.WindowDays == 5);
        Assert.Equal(CnyesValidationOutcome.Matched, ma5Record.Outcome);
        Assert.Equal(0.01m, ma5Record.Difference);
    }

    [Fact]
    public async Task 差異大於0點01記錄為差異()
    {
        var ma = Ma("2330", 100.00m, 100.00m, 100.00m, 100.00m);
        var service = CreateService(new Dictionary<string, MovingAverageResult> { ["2330"] = ma }, out _);
        var batch = CreateBatch(TradeDate, [Cnyes("2330", "台積電", 100.00m, 100.02m, 100.00m, 100.00m, 100.00m)]);

        var records = await service.ValidateAsync(TradeDate, new Dictionary<string, MarketType> { ["2330"] = MarketType.Listed }, [batch], CancellationToken.None);

        var ma5Record = records.Single(r => r.StockCode == "2330" && r.WindowDays == 5);
        Assert.Equal(CnyesValidationOutcome.Mismatched, ma5Record.Outcome);
    }

    [Fact]
    public async Task 僅回補5日資料時MA20_60_120標記資料不足_MA5仍正常比對()
    {
        // 模擬只回補5日：MovingAverageResult 只有 MA5，MA20/60/120 為 null。
        var ma = new MovingAverageResult("2330", TradeDate, 100m, 100m, null, null, null, 5, CalculationStatus.InsufficientHistory);
        var service = CreateService(new Dictionary<string, MovingAverageResult> { ["2330"] = ma }, out _);
        var batch = CreateBatch(TradeDate, [Cnyes("2330", "台積電", 100m, 100m, 99m, 98m, 97m)]);

        var records = await service.ValidateAsync(TradeDate, new Dictionary<string, MarketType> { ["2330"] = MarketType.Listed }, [batch], CancellationToken.None);

        Assert.Equal(CnyesValidationOutcome.Matched, records.Single(r => r.WindowDays == 5).Outcome);
        Assert.Equal(CnyesValidationOutcome.InsufficientHistory, records.Single(r => r.WindowDays == 20).Outcome);
        Assert.Equal(CnyesValidationOutcome.InsufficientHistory, records.Single(r => r.WindowDays == 60).Outcome);
        Assert.Equal(CnyesValidationOutcome.InsufficientHistory, records.Single(r => r.WindowDays == 120).Outcome);
    }

    [Fact]
    public async Task 鉅亨網本次無資料時標記來源不可用且不影響官方資料()
    {
        var ma = Ma("2330", 100m, 100m, 100m, 100m);
        var service = CreateService(new Dictionary<string, MovingAverageResult> { ["2330"] = ma }, out var repository);

        var records = await service.ValidateAsync(TradeDate, new Dictionary<string, MarketType> { ["2330"] = MarketType.Listed }, [], CancellationToken.None);

        var record = Assert.Single(records);
        Assert.Equal(CnyesValidationOutcome.SourceUnavailable, record.Outcome);
        Assert.Null(record.CalculatedValue);
        Assert.Null(record.CnyesValue);
        Assert.Single(repository.Saved); // 僅新增驗證紀錄，未觸碰任何官方資料表
    }

    private static CnyesStockPriceValidationService CreateService(
        IReadOnlyDictionary<string, MovingAverageResult> maResults,
        out FakeValidationRepository repository)
    {
        repository = new FakeValidationRepository();
        return new CnyesStockPriceValidationService(new FakeMovingAverageService(maResults), repository, new FakeClock(), new FakeLogger());
    }

    private static MovingAverageResult Ma(string code, decimal ma5, decimal ma20, decimal ma60, decimal ma120)
        => new(code, TradeDate, ma5, ma5, ma20, ma60, ma120, 120, CalculationStatus.Ok);

    private static TechnicalIndicator Cnyes(string code, string name, decimal close, decimal ma5, decimal ma20, decimal ma60, decimal ma120)
        => new(TradeDate, IndicatorType.BullishAlignment, MarketType.Listed, code, name, close, ma5, ma20, ma60, ma120,
            SourceUrl.ToString(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static CrawlBatch CreateBatch(DateOnly pageDate, IReadOnlyList<TechnicalIndicator> items) => new(
        new SourceDefinition("CNYES_BULLISH_ALIGNMENT", "鉅亨網－股價多頭排列", SourceUrl, IndicatorType.BullishAlignment, "CnyesTechnicalAlignment", true, true),
        MarketType.Listed, TradeDate, pageDate, items, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false);

    private sealed class FakeMovingAverageService : IMovingAverageService
    {
        private readonly IReadOnlyDictionary<string, MovingAverageResult> _results;
        public FakeMovingAverageService(IReadOnlyDictionary<string, MovingAverageResult> results) => _results = results;

        public Task<MovingAverageResult> CalculateAsync(string stockCode, DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult(Resolve(stockCode, tradeDate));

        public Task<IReadOnlyList<MovingAverageResult>> CalculateManyAsync(IReadOnlyCollection<string> stockCodes, DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<MovingAverageResult>>(stockCodes.Select(c => Resolve(c, tradeDate)).ToList());

        private MovingAverageResult Resolve(string code, DateOnly tradeDate)
            => _results.TryGetValue(code, out var result)
                ? result
                : new MovingAverageResult(code, tradeDate, null, null, null, null, null, 0, CalculationStatus.InsufficientHistory);
    }

    private sealed class FakeValidationRepository : IStockPriceValidationRepository
    {
        public List<CnyesValidationRecord> Saved { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveValidationRecordsAsync(IReadOnlyList<CnyesValidationRecord> records, CancellationToken cancellationToken)
        {
            Saved.AddRange(records);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CnyesValidationRecord>> GetValidationRecordsAsync(DateOnly tradeDate, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CnyesValidationRecord>>(Saved.Where(x => x.TradeDate == tradeDate).ToList());
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset GetTaipeiNow() => new(TradeDate.ToDateTime(new TimeOnly(13, 35)), TimeSpan.FromHours(8));
        public DateOnly GetTaipeiToday() => TradeDate;
    }

    private sealed class FakeLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
