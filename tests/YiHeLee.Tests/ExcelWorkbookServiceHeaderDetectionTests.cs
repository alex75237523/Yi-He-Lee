using YiHeLee.Infrastructure.Excel;

namespace YiHeLee.Tests;

/// <summary>
/// 針對 <see cref="ExcelWorkbookService"/> 表格邊界辨識與股票代碼候選過濾的純邏輯測試。
/// 不啟動 Excel COM，直接以 internal 方法與 <see cref="ExcelWorkbookService.CellValueAccessor"/>
/// （由 InternalsVisibleTo 開放給測試專案）驗證表頭同義字、區塊邊界與代碼格式判斷。
/// </summary>
public sealed class ExcelWorkbookServiceHeaderDetectionTests
{
    [Fact]
    public void 表頭現貨現價可正確識別為現價欄位()
    {
        // 第1列：代碼／股名／現貨現價；第2~3列為持股資料。
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "現貨現價"],
            ["2330", "台積電", "900"],
            ["5285", "宜鼎", "500"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 3, columnCount: 3);

        var header = Assert.Single(headers);
        Assert.Equal(1, header.RelativeRow);
        Assert.Equal(3, header.CurrentPriceColumn);
        Assert.Equal(3, header.EndRelativeRow);
    }

    [Fact]
    public void 表頭現價與現貨現價皆可識別為同一組持股表格()
    {
        var accessorCurrentPrice = BuildAccessor(
        [
            ["代號", "名稱", "現價"],
            ["2330", "台積電", "900"]
        ]);
        var accessorSpotCurrentPrice = BuildAccessor(
        [
            ["代號", "名稱", "現貨現價"],
            ["2330", "台積電", "900"]
        ]);

        Assert.Single(ExcelWorkbookService.FindActiveHoldingHeaders(accessorCurrentPrice, 2, 3));
        Assert.Single(ExcelWorkbookService.FindActiveHoldingHeaders(accessorSpotCurrentPrice, 2, 3));
    }

    [Fact]
    public void 遇到日期權益數保證金小計等其他表格時上一個持股區塊必須結束()
    {
        // 第1列：持股表頭；第2~3列：持股資料；第4列：其他彙總表表頭（日期／權益數／保證金／小計）；
        // 第5~6列：彙總表資料（其中一欄剛好是 8 位數金額，不得被誤判為股票代碼延續掃描）。
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "現價"],
            ["2330", "台積電", "900"],
            ["5285", "宜鼎", "500"],
            ["日期", "權益數", "保證金", "小計"],
            ["2026/07/09", "10037677", "500000", "10537677"],
            ["2026/07/10", "13818762", "600000", "14418762"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 6, columnCount: 4);

        var header = Assert.Single(headers);
        Assert.Equal(1, header.RelativeRow);
        // 區塊必須在第4列（其他表格表頭）之前結束，即 EndRelativeRow = 3，不得延伸到第5、6列。
        Assert.Equal(3, header.EndRelativeRow);
    }

    [Fact]
    public void 遇到新的持股表頭時上一個持股區塊必須結束()
    {
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "現價"],
            ["2330", "台積電", "900"],
            ["代碼", "股名", "現價"],
            ["5285", "宜鼎", "500"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 4, columnCount: 3);

        Assert.Equal(2, headers.Count);
        // 第1段資料只有第2列（第3列是新表頭，區塊必須在此結束）；第2段資料為第4列。
        Assert.Equal(2, headers[0].EndRelativeRow);
        Assert.Equal(4, headers[1].EndRelativeRow);
    }

    [Fact]
    public void 已出場表頭視為區塊邊界且不納入持股()
    {
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "現價"],
            ["2330", "台積電", "900"],
            ["代碼", "股名", "出場價", "出場日"],
            ["5285", "宜鼎", "500", "2026/07/01"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 4, columnCount: 4);

        var header = Assert.Single(headers);
        Assert.Equal(1, header.RelativeRow);
        // 資料只到第2列；第3列已出場表頭必須作為區塊結束邊界，第4列已出場資料不得納入。
        Assert.Equal(2, header.EndRelativeRow);
    }

    [Theory]
    [InlineData("0050", true)]
    [InlineData("2330", true)]
    [InlineData("00631L", true)]
    [InlineData("00982A", true)]
    [InlineData("50", true)]     // 1~3 碼純數字候選，交由官方主檔進一步確認補零。
    [InlineData("10037677", false)]
    [InlineData("13818762", false)]
    [InlineData("", false)]
    public void 股票代碼候選過濾規則(string code, bool expected)
    {
        Assert.Equal(expected, ExcelWorkbookService.IsAcceptableStockCodeCandidate(code));
    }

    [Fact]
    public void 表頭正規化去除空白並統一全形斜線()
    {
        Assert.Equal("代碼", ExcelWorkbookService.NormalizeHeader(" 代 碼 "));
        Assert.Equal("股名/現價", ExcelWorkbookService.NormalizeHeader("股名／現價"));
    }

    private static ExcelWorkbookService.CellValueAccessor BuildAccessor(string[][] rows)
    {
        var rowCount = rows.Length;
        var columnCount = rows.Max(r => r.Length);
        var values = new object[rowCount + 1, columnCount + 1];
        for (var r = 0; r < rowCount; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                values[r + 1, c + 1] = rows[r][c];
            }
        }

        return new ExcelWorkbookService.CellValueAccessor(values, rowCount, columnCount);
    }
}
