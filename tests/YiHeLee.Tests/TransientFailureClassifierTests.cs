using YiHeLee.Application.Exceptions;
using YiHeLee.Application.Services;

namespace YiHeLee.Tests;

public sealed class TransientFailureClassifierTests
{
    [Theory]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void HTTP暫時性狀態碼視為暫時性錯誤(int statusCode)
    {
        var ex = new RetryableJobException($"TWSE HTTP 回應失敗：{statusCode} 內部錯誤");

        Assert.True(TransientFailureClassifier.IsTransient(ex));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    public void HTTP非暫時性狀態碼視為不可重試(int statusCode)
    {
        var ex = new RetryableJobException($"TPEx HTTP 回應失敗：{statusCode} 參數錯誤");

        Assert.False(TransientFailureClassifier.IsTransient(ex));
    }

    [Theory]
    [InlineData("TWSE 回應缺少 date 欄位，無法驗證資料日期，拒絕視為成功。")]
    [InlineData("TWSE 回應缺少 tables 陣列，網站結構可能已變更。")]
    [InlineData("TPEx 每日收盤行情表格欄位不完整（缺少代號／名稱／收盤），網站結構可能已變更。")]
    [InlineData("TWSE 官方回應不是有效 JSON，可能為錯誤頁或驗證頁。")]
    public void HTML或JSON結構性錯誤不得盲目重試(string message)
    {
        var ex = new RetryableJobException(message);

        Assert.False(TransientFailureClassifier.IsTransient(ex));
    }

    [Fact]
    public void 網路逾時例外視為暫時性錯誤()
    {
        var ex = new RetryableJobException("連線失敗", new TaskCanceledException());

        Assert.True(TransientFailureClassifier.IsTransient(ex));
    }

    [Fact]
    public void HttpRequestException視為暫時性錯誤()
    {
        var ex = new RetryableJobException("連線失敗", new HttpRequestException("連線被拒絕"));

        Assert.True(TransientFailureClassifier.IsTransient(ex));
    }

    [Fact]
    public void 無法辨識的訊息預設視為暫時性但仍受重試上限約束()
    {
        var ex = new RetryableJobException("一個從未見過的全新錯誤訊息");

        Assert.True(TransientFailureClassifier.IsTransient(ex));
    }
}
