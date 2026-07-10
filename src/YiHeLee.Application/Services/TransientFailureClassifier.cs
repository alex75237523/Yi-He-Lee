using System.Net.Sockets;
using System.Text.RegularExpressions;
using YiHeLee.Application.Exceptions;

namespace YiHeLee.Application.Services;

/// <summary>
/// 判斷「暫時性錯誤」（可重試）。既有 TWSE／TPEx Provider／Parser 對 HTTP 失敗與
/// HTML／JSON 結構性錯誤統一包裝為 <see cref="RetryableJobException"/>（既有慣例，供每日排程沿用），
/// 且 <see cref="MarketPriceService"/> 會將這類失敗內部捕捉並轉成 <c>OfficialPriceBatchStatus.Failed</c>／
/// <c>NotPublished</c> 摘要（不會拋出例外），因此本類別同時提供依例外與依錯誤訊息文字兩種判斷方式：
/// HTTP 408／429／5xx、逾時、網路中斷才視為暫時性；驗證頁、欄位缺漏、結構變更、參數錯誤等
/// 不得盲目重試（依 PROJECT_INSTRUCTIONS 第六節）。無法辨識訊息內容時，預設視為暫時性但仍受
/// MaxRetryCount 上限約束，不會無限重試。
/// </summary>
public static class TransientFailureClassifier
{
    private static readonly Regex HttpStatusRegex = new(@"HTTP\s*回應失敗[：:]\s*(\d{3})", RegexOptions.Compiled);

    private static readonly string[] NonTransientMarkers =
    [
        "結構可能已變更",
        "欄位不完整",
        "缺少",
        "格式無法解析",
        "無法轉為數字",
        "找不到完整表頭",
        "不是有效 JSON",
        "驗證頁",
    ];

    public static bool IsTransient(Exception exception)
    {
        var inspected = exception.InnerException ?? exception;

        if (inspected is HttpRequestException or TaskCanceledException or TimeoutException or IOException or SocketException)
        {
            return true;
        }

        return IsTransientMessage(inspected.Message);
    }

    /// <summary>
    /// 依錯誤訊息文字判斷是否暫時性。用於 <see cref="MarketPriceService.FetchAndSaveSingleAsync"/>
    /// 內部已捕捉例外並轉為 <c>OfficialPriceBatchStatus.Failed</c>／<c>NotPublished</c> 摘要的情形——
    /// 這類失敗不會以例外形式拋出，只能依 <c>OfficialPriceBatchSummary.ErrorMessage</c> 判斷是否重試。
    /// </summary>
    public static bool IsTransientMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return true;
        }

        var statusMatch = HttpStatusRegex.Match(message);
        if (statusMatch.Success && int.TryParse(statusMatch.Groups[1].Value, out var statusCode))
        {
            return statusCode is 408 or 429 || statusCode >= 500;
        }

        if (NonTransientMarkers.Any(marker => message.Contains(marker, StringComparison.Ordinal)))
        {
            return false;
        }

        // 無法辨識的訊息內容：預設視為暫時性，但仍受 MaxRetryCount 上限約束，不會無限重試。
        return true;
    }
}
