using YiHeLee.Infrastructure.Excel;

namespace YiHeLee.Tests;

/// <summary>
/// 針對 <see cref="ExcelWorkbookService"/> 表格邊界辨識與股票代碼候選過濾的純邏輯測試。
/// 不啟動 Excel COM，直接以 internal 方法與 <see cref="ExcelWorkbookService.CellValueAccessor"/>
/// （由 InternalsVisibleTo 開放給測試專案）驗證表頭同義字、區塊邊界與代碼格式判斷。
/// 2026-07-11 需求恢復：有效持股表頭必須能分別找到「進場價/平均價」與「現價」兩個獨立欄位，缺一不可。
/// </summary>
public sealed class ExcelWorkbookServiceHeaderDetectionTests
{
    [Fact]
    public void 表頭進場價平均價半形斜線可正確識別()
    {
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "進場價/平均價", "現價"],
            ["2330", "台積電", "800", "900"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 2, columnCount: 4);

        var header = Assert.Single(headers);
        Assert.Equal(3, header.EntryAveragePriceColumn);
        Assert.Equal(4, header.CurrentPriceColumn);
    }

    [Fact]
    public void 表頭進場價平均價全形斜線可正確識別()
    {
        // NormalizeHeader 會把全形斜線「／」正規化為半形「/」，因此「進場價／平均價」也必須能識別。
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "進場價／平均價", "現價"],
            ["2330", "台積電", "800", "900"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 2, columnCount: 4);

        var header = Assert.Single(headers);
        Assert.Equal(3, header.EntryAveragePriceColumn);
    }

    [Fact]
    public void 表頭現價與現貨現價仍可辨識()
    {
        var accessorCurrentPrice = BuildAccessor(
        [
            ["代號", "名稱", "進場價/平均價", "現價"],
            ["2330", "台積電", "800", "900"]
        ]);
        var accessorSpotCurrentPrice = BuildAccessor(
        [
            ["代號", "名稱", "進場價/平均價", "現貨現價"],
            ["2330", "台積電", "800", "900"]
        ]);

        Assert.Single(ExcelWorkbookService.FindActiveHoldingHeaders(accessorCurrentPrice, 2, 4));
        Assert.Single(ExcelWorkbookService.FindActiveHoldingHeaders(accessorSpotCurrentPrice, 2, 4));
    }

    [Fact]
    public void 持股表必須分別取得進場價欄與現價欄()
    {
        var accessor = BuildAccessor(
        [
            ["進場日", "代號", "股名", "進場價/平均價", "張數", "現價"],
            ["2026/01/01", "2330", "台積電", "800", "10", "900"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 2, columnCount: 6);

        var header = Assert.Single(headers);
        Assert.Equal(2, header.CodeColumn);
        Assert.Equal(3, header.NameColumn);
        Assert.Equal(4, header.EntryAveragePriceColumn);
        Assert.Equal(5, header.QuantityColumn);
        Assert.Equal(6, header.CurrentPriceColumn);
        // 進場價/平均價欄與現價欄必須是彼此獨立的欄位索引，不可相同。
        Assert.NotEqual(header.EntryAveragePriceColumn, header.CurrentPriceColumn);
    }

    [Fact]
    public void 只有現價沒有進場價平均價時_不視為有效持股表頭()
    {
        // 有效持股表頭必須同時找到「進場價/平均價」與「現價」兩個獨立欄位；只有現價時不得誤判為有效持股。
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "現價"],
            ["2330", "台積電", "900"],
            ["5285", "宜鼎", "500"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 3, columnCount: 3);

        Assert.Empty(headers);
    }

    [Fact]
    public void 只有進場價平均價沒有現價時_不視為有效持股表頭()
    {
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "進場價/平均價"],
            ["2330", "台積電", "800"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 2, columnCount: 3);

        Assert.Empty(headers);
    }

    [Fact]
    public void 兩個價格欄位相鄰時仍不得讀錯欄()
    {
        // 「進場價/平均價」與「現價」緊鄰（D、E兩欄）時，欄位索引必須各自正確對應，不得混淆。
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "張數", "進場價/平均價", "現價"],
            ["2330", "台積電", "10", "800", "900"],
            ["5285", "宜鼎", "20", "480", "520"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 3, columnCount: 5);

        var header = Assert.Single(headers);
        Assert.Equal(4, header.EntryAveragePriceColumn);
        Assert.Equal(5, header.CurrentPriceColumn);
    }

    [Fact]
    public void 真實客戶表頭格式可正確解析()
    {
        // 真實客戶工作簿表頭：進場日／代號／股名／進場價/平均價／張數／現價。
        var accessor = BuildAccessor(
        [
            ["進場日", "代號", "股名", "進場價/平均價", "張數", "現價"],
            ["2026/06/01", "5285", "宜鼎", "501", "8", "520"],
            ["2026/06/02", "2330", "台積電", "800", "5", "900"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 3, columnCount: 6);

        var header = Assert.Single(headers);
        Assert.Equal(1, header.RelativeRow);
        Assert.Equal(3, header.EndRelativeRow);
        Assert.Equal(2, header.CodeColumn);
        Assert.Equal(3, header.NameColumn);
        Assert.Equal(4, header.EntryAveragePriceColumn);
        Assert.Equal(6, header.CurrentPriceColumn);
    }

    [Fact]
    public void 遇到日期權益數保證金小計等其他表格時上一個持股區塊必須結束()
    {
        // 第1列：持股表頭；第2~3列：持股資料；第4列：其他彙總表表頭（日期／權益數／保證金／小計）；
        // 第5~6列：彙總表資料（其中一欄剛好是 8 位數金額，不得被誤判為股票代碼延續掃描）。
        var accessor = BuildAccessor(
        [
            ["代碼", "股名", "進場價/平均價", "現價"],
            ["2330", "台積電", "800", "900"],
            ["5285", "宜鼎", "480", "500"],
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
            ["代碼", "股名", "進場價/平均價", "現價"],
            ["2330", "台積電", "800", "900"],
            ["代碼", "股名", "進場價/平均價", "現價"],
            ["5285", "宜鼎", "480", "500"]
        ]);

        var headers = ExcelWorkbookService.FindActiveHoldingHeaders(accessor, rowCount: 4, columnCount: 4);

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
            ["代碼", "股名", "進場價/平均價", "現價"],
            ["2330", "台積電", "800", "900"],
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
        Assert.Equal("進場價/平均價", ExcelWorkbookService.NormalizeHeader("進場價／平均價"));
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
