using YiHeLee.Domain;

namespace YiHeLee.Tests;

public sealed class StockIdentityResolverTests
{
    [Theory]
    [InlineData("0050")]
    [InlineData("0056")]
    [InlineData("2330")]
    [InlineData("5285")]
    public void 一般股票與四碼ETF代碼格式有效且納入策略(string code)
    {
        var identity = StockIdentityResolver.Resolve(code);

        Assert.True(identity.IsFormatValid);
        Assert.True(identity.IsEligibleForMovingAverageStrategy);
    }

    [Theory]
    [InlineData("00713")]
    [InlineData("00878")]
    [InlineData("00918")]
    [InlineData("00940")]
    public void 五碼ETF代碼格式有效且納入策略(string code)
    {
        var identity = StockIdentityResolver.Resolve(code);

        Assert.True(identity.IsFormatValid);
        Assert.Equal(SecurityProductType.Etf, identity.ProductType);
        Assert.True(identity.IsEligibleForMovingAverageStrategy);
    }

    [Fact]
    public void 槓桿反向ETF代碼00631L格式有效且納入策略()
    {
        var identity = StockIdentityResolver.Resolve("00631L");

        Assert.True(identity.IsFormatValid);
        Assert.Equal(SecurityProductType.LeveragedOrInverseEtf, identity.ProductType);
        Assert.True(identity.IsEligibleForMovingAverageStrategy);
    }

    [Fact]
    public void 主動式ETF代碼00982A格式有效且納入策略()
    {
        var identity = StockIdentityResolver.Resolve("00982A");

        Assert.True(identity.IsFormatValid);
        Assert.Equal(SecurityProductType.ActiveEtf, identity.ProductType);
        Assert.True(identity.IsEligibleForMovingAverageStrategy);
    }

    [Fact]
    public void 債券ETF代碼字尾B格式有效且納入策略()
    {
        var identity = StockIdentityResolver.Resolve("00679B");

        Assert.True(identity.IsFormatValid);
        Assert.Equal(SecurityProductType.BondEtf, identity.ProductType);
        Assert.True(identity.IsEligibleForMovingAverageStrategy);
    }

    [Fact]
    public void 存託憑證DR格式有效且納入策略()
    {
        var identity = StockIdentityResolver.Resolve("910322");

        Assert.True(identity.IsFormatValid);
        Assert.Equal(SecurityProductType.DepositoryReceipt, identity.ProductType);
        Assert.True(identity.IsEligibleForMovingAverageStrategy);
    }

    [Fact]
    public void 權證格式有效但明確排除於均線策略之外並留下原因()
    {
        // 6 碼數字、非 91 開頭視為權證。
        var identity = StockIdentityResolver.Resolve("070001");

        Assert.True(identity.IsFormatValid);
        Assert.Equal(SecurityProductType.Warrant, identity.ProductType);
        Assert.False(identity.IsEligibleForMovingAverageStrategy);
        Assert.False(string.IsNullOrWhiteSpace(identity.IneligibleReason));
    }

    [Theory]
    [InlineData("10037677")]
    [InlineData("10336638")]
    [InlineData("13818762")]
    [InlineData("15260141")]
    [InlineData("7529267")]
    [InlineData("8699353")]
    public void 八位數或七位數金額格式不得視為股票代碼(string amount)
    {
        var identity = StockIdentityResolver.Resolve(amount);

        Assert.False(identity.IsFormatValid);
        Assert.False(identity.IsEligibleForMovingAverageStrategy);
    }

    [Fact]
    public void 空白代碼格式無效()
    {
        var identity = StockIdentityResolver.Resolve(string.Empty);

        Assert.False(identity.IsFormatValid);
        Assert.False(identity.IsEligibleForMovingAverageStrategy);
    }

    [Fact]
    public void StockCodeNormalizer去除空白並轉大寫()
    {
        Assert.Equal("00631L", StockCodeNormalizer.Normalize(" 00631l "));
        Assert.Equal("5285", StockCodeNormalizer.Normalize("5285"));
        Assert.Equal(string.Empty, StockCodeNormalizer.Normalize(null));
    }
}
