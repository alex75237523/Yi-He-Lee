using YiHeLee.Infrastructure.Excel;

namespace YiHeLee.Tests;

/// <summary>
/// 「進場價/平均價」不是 DDE 欄位，但儲存格仍可能是 Excel 錯誤值、空白、0、負數或無法解析的文字；
/// 解析器必須明確分類並回報原因，且錯誤訊息不得寫成 DDE 異常，避免與「現價」欄位的原因混淆。
/// </summary>
public sealed class EntryAveragePriceCellParserTests
{
    [Theory]
    [InlineData(123.5)]
    [InlineData(0.01)]
    public void 正常數字_解析成功(double value)
    {
        var result = EntryAveragePriceCellParser.Parse(value);

        Assert.True(result.IsValid);
        Assert.Equal((decimal)value, result.Price);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void 整數_解析成功()
    {
        var result = EntryAveragePriceCellParser.Parse(501);

        Assert.True(result.IsValid);
        Assert.Equal(501m, result.Price);
    }

    [Fact]
    public void 千分位文字_解析成功()
    {
        var result = EntryAveragePriceCellParser.Parse("1,234.5");

        Assert.True(result.IsValid);
        Assert.Equal(1234.5m, result.Price);
    }

    [Fact]
    public void 可解析的數字文字_解析成功()
    {
        var result = EntryAveragePriceCellParser.Parse("501.5");

        Assert.True(result.IsValid);
        Assert.Equal(501.5m, result.Price);
    }

    [Fact]
    public void 空儲存格_回報空白原因且不提及DDE()
    {
        var result = EntryAveragePriceCellParser.Parse(null);

        Assert.False(result.IsValid);
        Assert.Contains("空白", result.Issue, StringComparison.Ordinal);
        Assert.DoesNotContain("DDE", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void 零_視為無效且不提及DDE()
    {
        var result = EntryAveragePriceCellParser.Parse(0);

        Assert.False(result.IsValid);
        Assert.Contains("非正數", result.Issue, StringComparison.Ordinal);
        Assert.DoesNotContain("DDE", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void 負數_視為無效()
    {
        var result = EntryAveragePriceCellParser.Parse(-5.5);

        Assert.False(result.IsValid);
        Assert.Contains("非正數", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcelNA錯誤值_回報錯誤值且不誤稱為DDE異常()
    {
        // #N/A 經 COM Interop 封送為 Int32：0x800A0000 | 2042。
        // 訊息可明確標示「非 DDE 欄位」以避免使用者誤會，但不得寫成「DDE 尚未連線／看盤軟體未開啟」
        // 等只有「現價」欄位才適用的 DDE 異常措辭。
        var result = EntryAveragePriceCellParser.Parse(unchecked((int)0x800A07FA));

        Assert.False(result.IsValid);
        Assert.Contains("#N/A", result.Issue, StringComparison.Ordinal);
        Assert.DoesNotContain("看盤軟體", result.Issue, StringComparison.Ordinal);
        Assert.DoesNotContain("DDE 尚未", result.Issue, StringComparison.Ordinal);
        Assert.DoesNotContain("DDE 連線", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcelValue錯誤值_回報錯誤值()
    {
        // #VALUE! = 0x800A0000 | 2015。
        var result = EntryAveragePriceCellParser.Parse(unchecked((int)0x800A07DF));

        Assert.False(result.IsValid);
        Assert.Contains("#VALUE!", result.Issue, StringComparison.Ordinal);
    }

    [Fact]
    public void 一般整數不得誤判為錯誤值()
    {
        // 高價股（例如 2042 元）與 Excel 錯誤代碼 2042 不同：只有 0x800Axxxx 形式才是 VT_ERROR。
        var result = EntryAveragePriceCellParser.Parse(2042);

        Assert.True(result.IsValid);
        Assert.Equal(2042m, result.Price);
    }

    [Theory]
    [InlineData("--")]
    [InlineData("成本價")]
    [InlineData("N/A")]
    public void 無法解析的文字_回報原因(string text)
    {
        var result = EntryAveragePriceCellParser.Parse(text);

        Assert.False(result.IsValid);
        Assert.Contains(text, result.Issue, StringComparison.Ordinal);
    }
}
