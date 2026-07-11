using YiHeLee.Infrastructure.Excel;

namespace YiHeLee.Tests;

/// <summary>
/// 「現價」欄位串接外部 DDE，儲存格可能是錯誤值、空白、0 或文字；解析器必須明確分類並回報原因，
/// 不得把 Excel 錯誤值（COM VT_ERROR，0x800Axxxx 的 Int32）誤當成價格。
/// </summary>
public sealed class CurrentPriceCellParserTests
{
    [Theory]
    [InlineData(123.5)]
    [InlineData(0.01)]
    public void 正常數字_解析成功(double value)
    {
        var result = CurrentPriceCellParser.Parse(value);

        Assert.True(result.IsValid);
        Assert.Equal((decimal)value, result.Price);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void 千分位文字_解析成功()
    {
        var result = CurrentPriceCellParser.Parse("1,234.5");

        Assert.True(result.IsValid);
        Assert.Equal(1234.5m, result.Price);
    }

    [Fact]
    public void 空儲存格_回報空白原因()
    {
        var result = CurrentPriceCellParser.Parse(null);

        Assert.False(result.IsValid);
        Assert.Contains("空白", result.Issue, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5.5)]
    public void 零或負數_視為DDE未連線(double value)
    {
        var result = CurrentPriceCellParser.Parse(value);

        Assert.False(result.IsValid);
        Assert.Contains("非正數", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcelNA錯誤值_回報看盤軟體未連線()
    {
        // #N/A 經 COM Interop 封送為 Int32：0x800A0000 | 2042。
        var result = CurrentPriceCellParser.Parse(unchecked((int)0x800A07FA));

        Assert.False(result.IsValid);
        Assert.Contains("#N/A", result.Issue, StringComparison.Ordinal);
        Assert.Contains("DDE", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcelNAME錯誤值_回報DDE函數無法辨識()
    {
        // #NAME? = 0x800A0000 | 2029；看盤軟體未開啟時 DDE 公式常見此錯誤。
        var result = CurrentPriceCellParser.Parse(unchecked((int)0x800A07ED));

        Assert.False(result.IsValid);
        Assert.Contains("#NAME?", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void Excel封鎖外部連線_回報BLOCKED與處理方式()
    {
        // #BLOCKED! = 0x800A0000 | 2047；Excel 封鎖外部 DDE 連線（實機曾出現 1382 筆）。
        var result = CurrentPriceCellParser.Parse(unchecked((int)0x800A07FF));

        Assert.False(result.IsValid);
        Assert.Contains("#BLOCKED!", result.Issue, StringComparison.Ordinal);
        Assert.Contains("DDE", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void 未知的Excel錯誤值_仍回報代碼()
    {
        // 0x800A0000 | 2060 未列在對照表內，必須回報代碼而不是誤判為價格。
        var result = CurrentPriceCellParser.Parse(unchecked((int)0x800A080C));

        Assert.False(result.IsValid);
        Assert.Contains("2060", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void 一般整數不得誤判為錯誤值()
    {
        // 高價股（例如 2042 元）與 Excel 錯誤代碼 2042 不同：只有 0x800Axxxx 形式才是 VT_ERROR。
        var result = CurrentPriceCellParser.Parse(2042);

        Assert.True(result.IsValid);
        Assert.Equal(2042m, result.Price);
    }

    [Theory]
    [InlineData("--")]
    [InlineData("連線中")]
    [InlineData("N/A")]
    public void 狀態文字_回報無法解析(string text)
    {
        var result = CurrentPriceCellParser.Parse(text);

        Assert.False(result.IsValid);
        Assert.Contains(text, result.Issue, StringComparison.Ordinal);
    }
}
