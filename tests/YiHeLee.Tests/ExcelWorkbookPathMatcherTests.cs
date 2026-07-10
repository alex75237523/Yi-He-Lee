using YiHeLee.Infrastructure.Excel;

namespace YiHeLee.Tests;

public sealed class ExcelWorkbookPathMatcherTests
{
    [Fact]
    public void IsSamePath_相同完整路徑_應回傳True()
    {
        var path = Path.Combine(Path.GetTempPath(), "YiHeLee", "親帶績效.xlsx");

        Assert.True(ExcelWorkbookPathMatcher.IsSamePath(path, path));
    }

    [Fact]
    public void IsSamePath_FileUri與本機路徑相同_應回傳True()
    {
        var path = Path.Combine(Path.GetTempPath(), "YiHeLee", "親帶績效.xlsx");
        var fileUri = new Uri(path).AbsoluteUri;

        Assert.True(ExcelWorkbookPathMatcher.IsSamePath(fileUri, path));
    }

    [Fact]
    public void HasSameFileName_不同資料夾的同名檔案_應回傳True()
    {
        var configured = Path.Combine(Path.GetTempPath(), "A", "親帶績效.xlsx");
        var opened = Path.Combine(Path.GetTempPath(), "B", "親帶績效.xlsx");

        Assert.True(ExcelWorkbookPathMatcher.HasSameFileName(opened, configured));
        Assert.False(ExcelWorkbookPathMatcher.IsSamePath(opened, configured));
    }

    [Fact]
    public void NormalizeConfiguredPath_空白路徑_應拋出ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ExcelWorkbookPathMatcher.NormalizeConfiguredPath("  "));
    }
}
