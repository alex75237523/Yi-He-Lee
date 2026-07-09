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
            Sources = []
        };
        var service = new SettingsValidationService();

        service.EnsureFixedSources(settings);

        Assert.Equal(AppSettings.FixedDailyRunTime, settings.DailyRunTime);
        Assert.Equal(2, settings.Sources.Count(x => x.Required && x.Enabled));
        Assert.Contains("#92D050", settings.ExcludedHoldingFillColors, StringComparer.OrdinalIgnoreCase);
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
