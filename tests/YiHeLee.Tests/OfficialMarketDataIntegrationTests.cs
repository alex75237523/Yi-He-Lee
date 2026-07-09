using YiHeLee.Application.Abstractions;
using YiHeLee.Domain;
using YiHeLee.Infrastructure.MarketData;
using YiHeLee.Infrastructure.Time;

namespace YiHeLee.Tests;

/// <summary>
/// 驗證 TWSE／TPEx 目前官方端點與欄位仍可使用的少量整合測試。
/// 這些測試會發出真實 HTTP 請求，標記 Category=Integration；
/// 在無法連網的建置環境可用 `dotnet test --filter Category!=Integration` 排除。
/// 若執行環境確實無法連線，測試會以明確失敗訊息呈現原因，不會假裝略過或成功。
/// </summary>
[Trait("Category", "Integration")]
public sealed class OfficialMarketDataIntegrationTests
{
    private static readonly OfficialMarketDataSettings Settings = new();

    [Fact]
    public async Task TWSE官方端點目前仍可取得每日收盤行情且欄位可解析()
    {
        using var httpClient = new HttpClient();
        var provider = new TwseMarketDataProvider(httpClient, new SilentLogger(), new TaipeiClock());

        // 使用最近一個已知的交易日（週間）避免假日造成誤判；此測試只驗證端點與欄位仍存在，不驗證特定數值。
        var requestedDate = FindRecentWeekday();

        OfficialPriceFetchResult result;
        try
        {
            result = await provider.FetchDailyCloseAsync(requestedDate, Settings, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Assert.Fail($"無法連線至 TWSE 官方端點，可能為執行環境未提供網際網路存取（非程式邏輯錯誤）：{ex.Message}");
            return;
        }

        // 若剛好是尚未公布或休市，仍應能正常解析出結果（不拋出未預期例外），僅不要求一定有資料列。
        Assert.True(result.IsHolidayOrNoData || result.Quotes.Count > 0,
            "TWSE 回應內容格式可能已變更：非休市但也沒有任何可解析的股票收盤價。");
    }

    [Fact]
    public async Task TPEx官方端點目前仍可取得每日收盤行情且欄位可解析()
    {
        using var httpClient = new HttpClient();
        var provider = new TpexMarketDataProvider(httpClient, new SilentLogger(), new TaipeiClock());

        var requestedDate = FindRecentWeekday();

        OfficialPriceFetchResult result;
        try
        {
            result = await provider.FetchDailyCloseAsync(requestedDate, Settings, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Assert.Fail($"無法連線至 TPEx 官方端點，可能為執行環境未提供網際網路存取（非程式邏輯錯誤）：{ex.Message}");
            return;
        }

        Assert.True(result.IsHolidayOrNoData || result.Quotes.Count > 0,
            "TPEx 回應內容格式可能已變更：沒有任何可解析的股票收盤價。");
    }

    private static DateOnly FindRecentWeekday()
    {
        var date = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8).Date); // 概略台北日期，僅供整合測試挑選查詢日
        while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            date = date.AddDays(-1);
        }

        return date;
    }

    private sealed class SilentLogger : IAppLogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
