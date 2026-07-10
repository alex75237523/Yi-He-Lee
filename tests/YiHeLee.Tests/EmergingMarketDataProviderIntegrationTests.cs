using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.MarketData;
using YiHeLee.Infrastructure.Time;

namespace YiHeLee.Tests;

/// <summary>
/// 針對 TPEx 官方興櫃股票當日行情端點的端對端整合測試：實際呼叫官方 OpenAPI，
/// 驗證真實回應可被解析且資料日期等於今日（Asia/Taipei）。
/// 標記 Category=Integration；無網路環境可用 `dotnet test --filter Category!=Integration` 排除。
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmergingMarketDataProviderIntegrationTests
{
    [Fact]
    public async Task 可從真實官方端點取得今日興櫃當日行情()
    {
        var clock = new TaipeiClock();
        var logger = new SilentLogger();
        using var httpClient = new HttpClient();
        var provider = new EmergingMarketDataProvider(httpClient, logger, clock);
        var today = clock.GetTaipeiToday();

        var result = await provider.FetchDailyCloseAsync(today, new OfficialMarketDataSettings(), CancellationToken.None);

        Assert.Equal(MarketType.Emerging, result.MarketType);
        Assert.False(result.IsHolidayOrNoData);
        Assert.NotNull(result.SourceDataDate);
        Assert.NotEmpty(result.Quotes);
    }

    private sealed class SilentLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
