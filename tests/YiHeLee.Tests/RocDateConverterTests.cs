using YiHeLee.Infrastructure.MarketData;

namespace YiHeLee.Tests;

public sealed class RocDateConverterTests
{
    [Fact]
    public void 西元日期轉民國年斜線格式()
    {
        var result = RocDateConverter.ToRocSlash(new DateOnly(2026, 7, 9));
        Assert.Equal("115/07/09", result);
    }

    [Fact]
    public void 西元日期轉西元緊湊格式()
    {
        var result = RocDateConverter.ToWesternCompact(new DateOnly(2026, 7, 9));
        Assert.Equal("20260709", result);
    }

    [Fact]
    public void 可解析民國年緊湊格式()
    {
        var ok = RocDateConverter.TryParseRocCompact("1150708", out var date);
        Assert.True(ok);
        Assert.Equal(new DateOnly(2026, 7, 8), date);
    }

    [Fact]
    public void 可解析民國年斜線格式()
    {
        var ok = RocDateConverter.TryParseRocSlash("115/07/08", out var date);
        Assert.True(ok);
        Assert.Equal(new DateOnly(2026, 7, 8), date);
    }

    [Fact]
    public void 可解析西元緊湊格式()
    {
        var ok = RocDateConverter.TryParseWesternCompact("20260708", out var date);
        Assert.True(ok);
        Assert.Equal(new DateOnly(2026, 7, 8), date);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2026-07-08")]
    [InlineData("115/13/40")]
    public void 格式錯誤時回傳false不丟例外(string value)
    {
        Assert.False(RocDateConverter.TryParseRocSlash(value, out _));
        Assert.False(RocDateConverter.TryParseWesternCompact(value, out _));
    }
}
