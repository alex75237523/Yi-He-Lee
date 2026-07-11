using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class SettingsValidationServiceTests
{
    [Fact]
    public void 固定來源與十三點三十五分不可被設定檔關閉()
    {
        var settings = new AppSettings
        {
            DailyRunTime = new TimeOnly(9, 0),
            Sources = [],
            OfficialMarketData = new OfficialMarketDataSettings
            {
                TpexDailyCloseUrlTemplate = "https://www.tpex.org.tw/web/stock/aftertrading/daily_close_quotes/stk_quote_result.php?l=zh-tw&d={0}&s=0,asc,0",
                EmergingHistoricalUrlTemplate = string.Empty
            }
        };
        var service = new SettingsValidationService();

        service.EnsureFixedSources(settings);

        Assert.Equal(AppSettings.FixedDailyRunTime, settings.DailyRunTime);
        Assert.Equal(2, settings.Sources.Count(x => x.Required && x.Enabled));
        Assert.Contains("#92D050", settings.ExcludedHoldingFillColors, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("afterTrading/otc", settings.OfficialMarketData.TpexDailyCloseUrlTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("emerging/historical", settings.OfficialMarketData.EmergingHistoricalUrlTemplate, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("#92D050", "#92D050")]
    [InlineData("92d050", "#92D050")]
    public void 顏色色碼可標準化(string input, string expected)
    {
        Assert.True(SettingsValidationService.TryNormalizeColor(input, out var normalized));
        Assert.Equal(expected, normalized);
    }
}
