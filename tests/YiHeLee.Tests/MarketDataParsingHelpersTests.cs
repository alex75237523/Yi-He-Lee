using YiHeLee.Infrastructure.MarketData;

namespace YiHeLee.Tests;

public sealed class MarketDataParsingHelpersTests
{
    [Theory]
    [InlineData("1,234.50", 1234.50)]
    [InlineData(" 99.10 ", 99.10)]
    [InlineData("2,465.00", 2465.00)]
    [InlineData("0.01", 0.01)]
    public void 含逗號或空白的價格可正確解析(string raw, decimal expected)
    {
        var ok = MarketDataParsingHelpers.TryParsePrice(raw, out var value);
        Assert.True(ok);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("--")]
    [InlineData("---")]
    [InlineData("N/A")]
    [InlineData("NA")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 無成交標記回傳false且不丟例外(string? raw)
    {
        var ok = MarketDataParsingHelpers.TryParsePrice(raw, out var value);
        Assert.False(ok);
        Assert.Equal(0m, value);
    }
}
