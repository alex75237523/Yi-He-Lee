using YiHeLee.Application.Services;
using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class HoldingRowExclusionServiceTests
{
    [Theory]
    [InlineData("#92D050")]
    [InlineData("92d050")]
    public void 綠色人工標記_略過判斷(string fillColor)
    {
        var settings = AppSettings.CreateDefault();
        var service = new HoldingRowExclusionService();

        Assert.True(service.ShouldExclude(settings, fillColor, ["5285", "宜鼎"]));
    }

    [Fact]
    public void 列內含不判斷文字_略過判斷()
    {
        var settings = AppSettings.CreateDefault();
        var service = new HoldingRowExclusionService();

        Assert.True(service.ShouldExclude(settings, string.Empty, ["5285", "宜鼎", "暫停判斷"]));
    }

    [Fact]
    public void 一般持股列_不可誤排除()
    {
        var settings = AppSettings.CreateDefault();
        var service = new HoldingRowExclusionService();

        Assert.False(service.ShouldExclude(settings, "#FFFF00", ["5285", "宜鼎", "融資"]));
    }
}
